using AirPlay.Core2.Connections;
using AirPlay.Core2.Extensions;
using AirPlay.Core2.Models.Configs;
using AirPlay.Core2.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace AirPlay.Core2.Services;

public class AirPlayService(
    ILoggerFactory loggerFactory,
    IOptions<AirPlayConfig> options,
    IOptions<AirTunesConfig> securityOptions) : BackgroundService
{
    private readonly ILogger<AirPlayService> _logger = loggerFactory.CreateLogger<AirPlayService>();

    private readonly TcpListener _tcpListener = new(IPAddress.Any, options.Value.Port);
    private readonly ConcurrentDictionary<IPEndPoint, ModifiedHttpConnection> _httpConnections = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _tcpListener.Start();

        while (!stoppingToken.IsCancellationRequested)
        {
            var client = await _tcpListener.AcceptTcpClientAsync(stoppingToken);
            _logger.HttpClientAccpeted(client.Client.RemoteEndPoint);

            if (client.Client.RemoteEndPoint is not IPEndPoint remoteEndPoint)
            {
                client.Close();
                continue;
            }

            if ((!securityOptions.Value.AllowNonPrivateNetworks && !NetworkAccessPolicy.IsPrivateOrLocal(remoteEndPoint.Address)) ||
                _httpConnections.Count >= Math.Clamp(securityOptions.Value.MaximumConcurrentConnections, 1, 64))
            {
                _logger.HttpClientRejected(remoteEndPoint);
                client.Dispose();
                continue;
            }

            var connection = new ModifiedHttpConnection(client, loggerFactory);
            connection.ConnectionClosed += (_, _) =>
            {
                _httpConnections.TryRemove(remoteEndPoint, out ModifiedHttpConnection? removed);
                removed?.Dispose();
                client.Dispose();
            };

            if (!_httpConnections.TryAdd(remoteEndPoint, connection))
            {
                connection.Dispose();
                continue;
            }

            connection.BeginMessageLoopWorker(stoppingToken);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _tcpListener.Stop();
        foreach (ModifiedHttpConnection connection in _httpConnections.Values)
            connection.Dispose();
        _httpConnections.Clear();

        return base.StopAsync(cancellationToken);
    }
}

internal static partial class AirPlayServiceLoggers
{
    [LoggerMessage(LogLevel.Information, "Client from [{endPoint}] accepted, creating HttpConnection..")]
    public static partial void HttpClientAccpeted(this ILogger logger, EndPoint? endPoint);

    [LoggerMessage(LogLevel.Warning, "Rejected HTTP client [{endPoint}] due to the local-network or connection-limit policy")]
    public static partial void HttpClientRejected(this ILogger logger, EndPoint? endPoint);
}
