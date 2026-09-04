using AirPlay.Core2.Extensions;
using AirPlay.Core2.Models;
using AirPlay.Core2.Models.Configs;
using AirPlay.Core2.Models.Messages.Rtsp;
using AirPlay.Core2.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rebex.Security.Cryptography;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using static AirPlay.Core2.Models.Messages.Rtsp.RtspRequestMessage;

namespace AirPlay.Core2.Connections;

public partial class RtspConnection : IDisposable
{
    private static readonly TimeSpan HandshakeReadTimeout = TimeSpan.FromSeconds(90);

    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<RtspConnection>? _logger;
    private readonly AirTunesConfig _airTunesConfig;
    private readonly ReceiverIdentity _identity;
    private readonly AuthenticationRateLimiter _authenticationRateLimiter;

    private readonly TcpClient _client;
    private readonly IPEndPoint _endPoint;

    private readonly Ed25519 _ed25519;
    private readonly byte[] _publicKey;

    private Curve25519? _curve25519;
    private byte[]? _ecdhOurs;
    private byte[]? _ecdhTheirs;
    private byte[]? _edTheirs;
    private byte[]? _ecdhShared;

    private bool _pairVerified;
    private bool _authenticated;
    private byte[]? _keyMsg;
    private readonly string _authenticationNonce = DigestAuthenticator.CreateNonce();

    private string? _ActiveRemote;
    private string? _DACPID;

    private DeviceSession? _deviceSession;
    private volatile bool _disconnectRequested;

    public RtspConnection(
        TcpClient client,
        IOptions<AirTunesConfig> options,
        ReceiverIdentity identity,
        AuthenticationRateLimiter authenticationRateLimiter,
        ILoggerFactory? loggerFactory = null)
    {
        _client = client;
        _client.NoDelay = true;
        _endPoint = client.Client.RemoteEndPoint as IPEndPoint
            ?? throw new ArgumentException("TcpClient must be connected to a remote endpoint");

        _airTunesConfig = options.Value;
        _authenticationRateLimiter = authenticationRateLimiter;

        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<RtspConnection>();

        _ed25519 = identity.CreateSigningKey();
        _publicKey = identity.PublicKey.ToArray();
        _identity = identity;
    }

    public event EventHandler? ConnectionClosed;
    public event EventHandler<DeviceSession>? SessionPaired;

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
                // Normal service shutdown.
            }
            catch (Exception exception)
            {
                _logger?.MessageLoopFailed(_endPoint, exception);
            }
            finally
            {
                if (_deviceSession is not null)
                    _deviceSession.DisconnectRequested -= OnDeviceSessionDisconnectRequested;

                try
                {
                    ConnectionClosed?.Invoke(this, EventArgs.Empty);
                }
                finally
                {
                    ClearEphemeralSecrets();
                }
            }
        }, CancellationToken.None);
    }

    private async Task MessageLoopWorker(CancellationToken cancellationToken)
    {
        _logger?.RunningMessageLoopWorker(_endPoint);
        ConnectionClosed += (_, _) => _logger?.EndMessageLoopWorker(_endPoint);

        using var networkStream = _client.GetStream();
        using var reader = new RtspMessageReader();

        if (!_client.Connected) throw new InvalidOperationException("TcpClient is not connected");
        if (!networkStream.CanRead) throw new InvalidOperationException("Can't read the NetworkStream");

        while (!cancellationToken.IsCancellationRequested)
        {
            RtspRequestMessage? requestMessage;
            try
            {
                if (_deviceSession is null)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(HandshakeReadTimeout);
                    requestMessage = await reader.ReadAsync(networkStream, timeout.Token).ConfigureAwait(false);
                }
                else
                {
                    requestMessage = await reader.ReadAsync(networkStream, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger?.HandshakeTimedOut(_endPoint);
                break;
            }
            catch (RtspProtocolException exception)
            {
                _logger?.InvalidRtspMessage(_endPoint, exception.Message);
                break;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (requestMessage is null)
                break;

            RtspResponseMessage responseMessage = await HandleRequestMessageAsync(requestMessage, cancellationToken).ConfigureAwait(false);
            await HandleResponseMessageAsync(requestMessage, responseMessage, networkStream, cancellationToken).ConfigureAwait(false);

            if (_disconnectRequested || (_deviceSession?.RequestedDisconnect ?? false))
                break;
        }
    }

    private async Task<RtspResponseMessage> HandleRequestMessageAsync(RtspRequestMessage requestMessage, CancellationToken cancellationToken)
    {
        var responseMessage = requestMessage.CreateResponse();

        if (requestMessage.Protocol == RtspRequestMessage.WireProtocol.Http)
        {
            responseMessage.Status = RtspResponseMessage.StatusCode.NOTFOUND;
            responseMessage.Headers["Connection"] = ["close"];
            _disconnectRequested = true;
            return responseMessage;
        }

        if (requestMessage.Headers.TryGetSingleValue("Active-Remote", out string? activeRemote) &&
            IsSafeSenderIdentifier(activeRemote))
            _ActiveRemote = activeRemote;
        if (requestMessage.Headers.TryGetSingleValue("DACP-ID", out string? dacpId) &&
            IsSafeSenderIdentifier(dacpId))
            _DACPID = dacpId;

        _logger?.RtspRequestMessageReceived(_ActiveRemote ?? "unknown", requestMessage.Type, requestMessage.Path);

        // RAOP password authentication is negotiated on the initial SETUP. Apple's
        // pair-setup, pair-verify, and FairPlay key exchange happen before that challenge.
        if (_airTunesConfig.RequirePassword && !_authenticated && requestMessage.Type == RequestType.SETUP)
        {
            if (!_authenticationRateLimiter.CanAttempt(_endPoint.Address))
            {
                responseMessage.Status = RtspResponseMessage.StatusCode.FORBIDDEN;
                responseMessage.Headers["Connection"] = ["close"];
                _disconnectRequested = true;
                return responseMessage;
            }

            if (!requestMessage.Headers.TryGetSingleValue("Authorization", out string? authorization))
            {
                responseMessage.Status = RtspResponseMessage.StatusCode.UNAUTHORIZED;
                responseMessage.Headers["WWW-Authenticate"] = [DigestAuthenticator.CreateChallenge(_authenticationNonce)];
                return responseMessage;
            }

            if (!DigestAuthenticator.Verify(
                    authorization,
                    requestMessage.Type.ToString(),
                    requestMessage.Path,
                    _airTunesConfig.Password,
                    _authenticationNonce))
            {
                bool canRetry = _authenticationRateLimiter.RecordFailure(_endPoint.Address);
                responseMessage.Status = canRetry
                    ? RtspResponseMessage.StatusCode.UNAUTHORIZED
                    : RtspResponseMessage.StatusCode.FORBIDDEN;
                if (canRetry)
                    responseMessage.Headers["WWW-Authenticate"] = [DigestAuthenticator.CreateChallenge(_authenticationNonce)];
                else
                {
                    responseMessage.Headers["Connection"] = ["close"];
                    _disconnectRequested = true;
                }
                return responseMessage;
            }

            _authenticationRateLimiter.RecordSuccess(_endPoint.Address);
            _authenticated = true;
        }

        try
        {
            if (_deviceSession is null && RequiresDeviceSession(requestMessage))
                responseMessage.Status = RtspResponseMessage.StatusCode.BADREQUEST;
            else if (requestMessage.Type == RequestType.OPTIONS)
                OnOptionsRequested(responseMessage);
            else if (IsInfoRequest(requestMessage))
                await OnGetInfoRequested(requestMessage, responseMessage, cancellationToken);
            else if (requestMessage.Type == RequestType.POST && "/pair-setup".Equals(requestMessage.Path, StringComparison.OrdinalIgnoreCase))
                await OnPostPairSetupRequested(responseMessage, cancellationToken);
            else if (requestMessage.Type == RequestType.POST && "/pair-verify".Equals(requestMessage.Path, StringComparison.OrdinalIgnoreCase))
                await OnPostPairVerifyRequested(requestMessage, responseMessage, cancellationToken);
            else if (requestMessage.Type == RequestType.POST && "/fp-setup".Equals(requestMessage.Path, StringComparison.OrdinalIgnoreCase))
                await OnPostFpSetupRequested(requestMessage, responseMessage, cancellationToken);
            else if (requestMessage.Type == RequestType.SETUP)
                await OnSetupRequested(requestMessage, responseMessage, cancellationToken);
            else if (requestMessage.Type == RequestType.GET_PARAMETER)
                await OnGetParameterRequested(requestMessage, responseMessage, cancellationToken);
            else if (requestMessage.Type == RequestType.RECORD)
                OnRecordRequested(responseMessage); // The sender wants to start streaming.
            else if (requestMessage.Type == RequestType.POST && "/feedback".Equals(requestMessage.Path, StringComparison.OrdinalIgnoreCase))
                await OnPostFeedbackRequested(); // Sender heartbeat.
            else if (requestMessage.Type == RequestType.FLUSH)
                OnFlushRequested(requestMessage, responseMessage);
            else if (requestMessage.Type == RequestType.TEARDOWN)
                await OnTeardownRequested(requestMessage);
            else if (requestMessage.Type == RequestType.SET_PARAMETER)
                OnSetParameterRequested(requestMessage, responseMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            responseMessage.Status = RtspResponseMessage.StatusCode.BADREQUEST;
            _logger?.RequestHandlingFailed(_endPoint, requestMessage.Type, requestMessage.Path, exception);
        }

        return responseMessage;
    }

    private static bool IsInfoRequest(RtspRequestMessage requestMessage)
        => requestMessage.Type == RequestType.GET &&
            (requestMessage.Path.Equals("/info", StringComparison.OrdinalIgnoreCase) ||
             requestMessage.Path.StartsWith("/info?", StringComparison.OrdinalIgnoreCase));

    private static bool RequiresDeviceSession(RtspRequestMessage requestMessage)
        => requestMessage.Type is RequestType.GET_PARAMETER or RequestType.RECORD or
            RequestType.FLUSH or RequestType.TEARDOWN or RequestType.SET_PARAMETER ||
            (requestMessage.Type == RequestType.POST &&
             requestMessage.Path.Equals("/feedback", StringComparison.OrdinalIgnoreCase));

    private static bool IsSafeSenderIdentifier(string value)
        => value.Length is > 0 and <= 64 && value.All(character => char.IsAsciiLetterOrDigit(character));

    private async Task HandleResponseMessageAsync(RtspRequestMessage requestMessage, RtspResponseMessage responseMessage, NetworkStream networkStream, CancellationToken cancellationToken)
    {
        byte[] bodyBuffer = await responseMessage.ReadToEndAsync();
        responseMessage.Headers["Content-Length"] = [bodyBuffer.Length.ToString(CultureInfo.InvariantCulture)];

        StringBuilder stringBuilder = new();
        stringBuilder.Append(requestMessage.Protocol == RtspRequestMessage.WireProtocol.Http ? "HTTP/1.1 " : "RTSP/1.0 ")
            .Append((int)responseMessage.Status)
            .Append(' ')
            .Append(GetReasonPhrase(responseMessage.Status))
            .Append("\r\n");

        foreach (var header in responseMessage.Headers)
            stringBuilder.Append(header.Name).Append(": ").AppendJoin(',', header).Append("\r\n");

        stringBuilder.Append("\r\n");

        byte[] headerBuffer = Encoding.ASCII.GetBytes(stringBuilder.ToString());

        try
        {
            await networkStream.WriteAsync(headerBuffer, cancellationToken).ConfigureAwait(false);
            if (bodyBuffer.Length > 0)
                await networkStream.WriteAsync(bodyBuffer, cancellationToken).ConfigureAwait(false);
            await networkStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) 
        {
            _logger?.SendResponseMessageError(_ActiveRemote!, requestMessage.Type, requestMessage.Path);
        }
        finally
        {
            responseMessage.Dispose();
        }
    }

    private static string GetReasonPhrase(RtspResponseMessage.StatusCode status) => status switch
    {
        RtspResponseMessage.StatusCode.OK => "OK",
        RtspResponseMessage.StatusCode.NOCONTENT => "No Content",
        RtspResponseMessage.StatusCode.BADREQUEST => "Bad Request",
        RtspResponseMessage.StatusCode.UNAUTHORIZED => "Unauthorized",
        RtspResponseMessage.StatusCode.FORBIDDEN => "Forbidden",
        RtspResponseMessage.StatusCode.NOTFOUND => "Not Found",
        RtspResponseMessage.StatusCode.INTERNALSERVERERROR => "Internal Server Error",
        _ => "Unknown",
    };

    private void OnDeviceSessionDisconnectRequested(object? sender, EventArgs args)
    {
        _disconnectRequested = true;
        _client.Dispose();
    }

    private void ClearEphemeralSecrets()
    {
        if (_ecdhShared is not null)
            CryptographicOperations.ZeroMemory(_ecdhShared);
        if (_keyMsg is not null)
            CryptographicOperations.ZeroMemory(_keyMsg);
        _ecdhShared = null;
        _keyMsg = null;
    }

    public void Dispose() => _client.Dispose();
}

internal static partial class RtspConnectionLoggers
{
    [LoggerMessage(LogLevel.Information, "Running message loop worker for client [{endPoint}]")]
    public static partial void RunningMessageLoopWorker(this ILogger logger, EndPoint? endPoint);

    [LoggerMessage(LogLevel.Information, "End message loop worker and close client [{endPoint}]")]
    public static partial void EndMessageLoopWorker(this ILogger logger, EndPoint? endPoint);

    [LoggerMessage(LogLevel.Information, "RtspRequestMessage from [{activeRemote}] Received: [{requestType}] \"{requestPath}\"")]
    public static partial void RtspRequestMessageReceived(this ILogger logger, string activeRemote, RtspRequestMessage.RequestType requestType, string requestPath);

    [LoggerMessage(LogLevel.Warning, "Failed to send responseMessage to RtspRequestMessage from [{activeRemote}] Received: [{requestType}] \"{requestPath}\"")]
    public static partial void SendResponseMessageError(this ILogger logger, string activeRemote, RtspRequestMessage.RequestType requestType, string requestPath);

    [LoggerMessage(LogLevel.Warning, "Rejected an invalid RTSP message from [{endPoint}]: {reason}")]
    public static partial void InvalidRtspMessage(this ILogger logger, EndPoint? endPoint, string reason);

    [LoggerMessage(LogLevel.Warning, "Closed an incomplete AirPlay handshake from [{endPoint}] after the read timeout")]
    public static partial void HandshakeTimedOut(this ILogger logger, EndPoint? endPoint);

    [LoggerMessage(LogLevel.Error, "RTSP message loop failed for client [{endPoint}]")]
    public static partial void MessageLoopFailed(this ILogger logger, EndPoint? endPoint, Exception exception);

    [LoggerMessage(LogLevel.Warning, "Rejected malformed payload from [{endPoint}] for [{requestType}] \"{requestPath}\"")]
    public static partial void RequestHandlingFailed(this ILogger logger, EndPoint? endPoint, RtspRequestMessage.RequestType requestType, string requestPath, Exception exception);
}
