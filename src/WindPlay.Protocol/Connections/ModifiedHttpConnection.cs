using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AirPlay.Core2.Connections;

/// <summary>
/// Safely terminates legacy AirPlay HTTP probes. Mirroring and audio use the paired
/// RAOP/RTSP service; legacy media-control routes are not implemented in this build.
/// </summary>
public sealed partial class ModifiedHttpConnection : IDisposable
{
    internal const int MaximumHeaderBytes = 32 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<ModifiedHttpConnection>? _logger;
    private readonly TcpClient _client;
    private readonly IPEndPoint _endPoint;

    public ModifiedHttpConnection(TcpClient client, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _client.NoDelay = true;
        _endPoint = client.Client.RemoteEndPoint as IPEndPoint
            ?? throw new ArgumentException("TcpClient must be connected to a remote endpoint", nameof(client));
        _logger = loggerFactory?.CreateLogger<ModifiedHttpConnection>();
    }

    public event EventHandler? ConnectionClosed;

    public void BeginMessageLoopWorker(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await MessageLoopWorker(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal receiver shutdown.
            }
            catch (IOException)
            {
                // The probing client closed the connection.
            }
            catch (SocketException)
            {
                // The probing client reset the connection.
            }
            catch (ObjectDisposedException)
            {
                // Service shutdown disposes active clients.
            }
            catch (Exception exception)
            {
                _logger?.ConnectionFailed(_endPoint, exception);
            }
            finally
            {
                ConnectionClosed?.Invoke(this, EventArgs.Empty);
            }
        }, CancellationToken.None);
    }

    private async Task MessageLoopWorker(CancellationToken cancellationToken)
    {
        _logger?.RunningLegacyHttpMessageLoop(_endPoint);
        byte[] header = GC.AllocateUninitializedArray<byte>(MaximumHeaderBytes);
        int length = 0;

        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(RequestTimeout);
        await using NetworkStream stream = _client.GetStream();

        try
        {
            while (length < header.Length)
            {
                int read = await stream.ReadAsync(header.AsMemory(length), requestTimeout.Token).ConfigureAwait(false);
                if (read == 0)
                    return;

                length += read;
                if (FindHeaderTerminator(header.AsSpan(0, length)) >= 0)
                {
                    await WriteResponseAsync(stream, "404 Not Found", cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            await WriteResponseAsync(stream, "431 Request Header Fields Too Large", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger?.ConnectionIdle(_endPoint);
        }
        finally
        {
            _logger?.EndLegacyHttpMessageLoop(_endPoint);
        }
    }

    internal static int FindHeaderTerminator(ReadOnlySpan<byte> data)
    {
        for (int index = 0; index <= data.Length - 4; index++)
        {
            if (data[index] == (byte)'\r' && data[index + 1] == (byte)'\n' &&
                data[index + 2] == (byte)'\r' && data[index + 3] == (byte)'\n')
                return index;
        }

        return -1;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        string status,
        CancellationToken cancellationToken)
    {
        byte[] response = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\nServer: {Constants.AIRTUNES_SERVER_VERSION}\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _client.Dispose();
}

internal static partial class ModifiedHttpConnectionLoggers
{
    [LoggerMessage(LogLevel.Warning, "The legacy AirPlay HTTP connection [{endPoint}] timed out")]
    public static partial void ConnectionIdle(this ILogger logger, EndPoint? endPoint);

    [LoggerMessage(LogLevel.Information, "Running legacy AirPlay HTTP probe handler for client [{endPoint}]")]
    public static partial void RunningLegacyHttpMessageLoop(this ILogger logger, EndPoint? endPoint);

    [LoggerMessage(LogLevel.Information, "Closed legacy AirPlay HTTP probe handler for client [{endPoint}]")]
    public static partial void EndLegacyHttpMessageLoop(this ILogger logger, EndPoint? endPoint);

    [LoggerMessage(LogLevel.Warning, "Legacy AirPlay HTTP probe handler failed for client [{endPoint}]")]
    public static partial void ConnectionFailed(this ILogger logger, EndPoint? endPoint, Exception exception);
}
