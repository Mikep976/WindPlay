using System.Buffers;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using AirPlay.Core2.Models.Messages.Mirror;
using AirPlay.Core2.Utils;

using AesSecret = (byte[] DecryptedAesKey, byte[] AesIv, byte[] EcdhShared);

namespace AirPlay.Core2.Connections.Mirror;

public sealed class MirrorDataConnection : IDisposable
{
    public const int MaximumPayloadBytes = 16 * 1024 * 1024;

    private readonly TcpListener _tcpListener = new(IPAddress.Any, 0);
    private readonly AESCTRBufferedCipher _cipher;
    private readonly IPAddress _expectedRemoteAddress;
    private readonly CancellationTokenSource _tokenSource = new();
    private readonly object _stateGate = new();

    private byte[]? _parameterSets;
    private Task? _worker;
    private bool _disposed;

    public MirrorDataConnection(string streamConnectionId, AesSecret aesSecret, IPAddress expectedRemoteAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamConnectionId);
        ArgumentNullException.ThrowIfNull(expectedRemoteAddress);
        _expectedRemoteAddress = expectedRemoteAddress.IsIPv4MappedToIPv6
            ? expectedRemoteAddress.MapToIPv4()
            : expectedRemoteAddress;
        _cipher = AESCTRBufferedCipher.CreateStream(
            streamConnectionId,
            aesSecret.DecryptedAesKey,
            aesSecret.EcdhShared);

        // Reserve the ephemeral port before SETUP returns it to the sender. This avoids
        // the check-then-bind race of probing a port and opening it later.
        _tcpListener.Start(backlog: 1);
        DataPort = checked((ushort)((IPEndPoint)_tcpListener.LocalEndpoint).Port);
    }

    public ushort DataPort { get; }

    public event EventHandler<Size>? FrameSizeChanged;

    /// <remarks>
    /// The frame is valid for the duration of the callback. Call <see cref="H264Data.Retain"/>
    /// before returning when it must be queued or used asynchronously.
    /// </remarks>
    public event EventHandler<H264Data>? DataReceived;

    public event Action<Exception>? ConnectionFaulted;

    public Size? FrameSize { get; private set; }

    public void BeginDataMessageLoopWorker()
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _worker ??= Task.Run(() => DataMessageLoopWorker(_tokenSource.Token), CancellationToken.None);
        }
    }

    public void EndDataMessageLoopWorker()
    {
        if (_tokenSource.IsCancellationRequested)
            return;

        _tokenSource.Cancel();
        _tcpListener.Stop();
    }

    private async Task DataMessageLoopWorker(CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient client = await _tcpListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            _tcpListener.Stop();
            if (client.Client.RemoteEndPoint is not IPEndPoint remoteEndPoint ||
                !Normalize(remoteEndPoint.Address).Equals(_expectedRemoteAddress))
                throw new UnauthorizedAccessException("A different network peer attempted to open the mirroring stream.");

            client.NoDelay = true;
            client.ReceiveBufferSize = 1024 * 1024;

            await using NetworkStream networkStream = client.GetStream();
            byte[] headerBuffer = GC.AllocateUninitializedArray<byte>(MirroringHeader.Length);

            while (!cancellationToken.IsCancellationRequested)
            {
                await networkStream.ReadExactlyAsync(headerBuffer, cancellationToken).ConfigureAwait(false);
                MirroringHeader header = new(headerBuffer);
                if (header.PayloadSize is < 0 or > MaximumPayloadBytes)
                    throw new InvalidDataException($"Mirroring payload exceeds {MaximumPayloadBytes} bytes.");

                byte[] payloadBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, header.PayloadSize));
                bool bufferOwnedByFrame = false;
                try
                {
                    if (header.PayloadSize > 0)
                        await networkStream.ReadExactlyAsync(
                            payloadBuffer.AsMemory(0, header.PayloadSize),
                            cancellationToken).ConfigureAwait(false);

                    if (header.PayloadType == 0)
                    {
                        if (header.PayloadSize == 0 || FrameSize is not Size frameSize || _parameterSets is null)
                            continue;

                        Span<byte> payload = payloadBuffer.AsSpan(0, header.PayloadSize);
                        _cipher.TransformInPlace(payload);
                        if (!H264AnnexBConverter.TryConvertAccessUnit(payload, out int frameType))
                            continue;

                        byte[] frameBuffer = payloadBuffer;
                        int frameLength = header.PayloadSize;
                        if (frameType == 5)
                        {
                            frameLength = checked(_parameterSets.Length + header.PayloadSize);
                            frameBuffer = ArrayPool<byte>.Shared.Rent(frameLength);
                            _parameterSets.CopyTo(frameBuffer, 0);
                            payload.CopyTo(frameBuffer.AsSpan(_parameterSets.Length));
                        }
                        else
                        {
                            bufferOwnedByFrame = true;
                        }

                        using H264Data frame = new(
                            frameBuffer,
                            frameLength,
                            frameType,
                            header.PayloadPts,
                            frameSize.Width,
                            frameSize.Height);
                        DataReceived?.Invoke(this, frame);
                    }
                    else if (header.PayloadType == 1)
                    {
                        if (header.PayloadSize == 0)
                            throw new NotSupportedException("The sender selected a video codec other than H.264.");

                        if (header.WidthSource > 0 && header.HeightSource > 0)
                        {
                            Size newSize = new(header.WidthSource, header.HeightSource);
                            if (FrameSize != newSize)
                            {
                                FrameSize = newSize;
                                FrameSizeChanged?.Invoke(this, newSize);
                            }
                        }

                        if (!H264AnnexBConverter.TryCreateParameterSets(
                            payloadBuffer.AsSpan(0, header.PayloadSize),
                            out byte[] parameterSets))
                            throw new InvalidDataException("The sender provided an invalid H.264 codec configuration.");

                        _parameterSets = parameterSets;
                    }
                }
                finally
                {
                    if (!bufferOwnedByFrame)
                        ArrayPool<byte>.Shared.Return(payloadBuffer, clearArray: false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal stream shutdown.
        }
        catch (EndOfStreamException)
        {
            // The sender closed the stream normally.
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
            // Listener shutdown interrupts an outstanding socket operation.
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            // Listener shutdown interrupts an outstanding socket operation.
        }
        catch (Exception exception)
        {
            ConnectionFaulted?.Invoke(exception);
        }
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        EndDataMessageLoopWorker();
        Task? worker = _worker;
        if (worker is null || worker.IsCompleted)
        {
            ReleaseResources();
            return;
        }

        _ = worker.ContinueWith(
            static (_, state) => ((MirrorDataConnection)state!).ReleaseResources(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ReleaseResources()
    {
        _tcpListener.Dispose();
        _tokenSource.Dispose();
        _cipher.Dispose();
    }

    private static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
