using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace AirPlay.Core2.Connections.Audio;

/// <summary>Maintains the lightweight NTP-style timing exchange expected by AirPlay senders.</summary>
public sealed class AudioTimingConnection : IDisposable
{
    private const ulong SecondsFrom1900To1970 = 2_208_988_800UL;
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly CancellationTokenSource _tokenSource = new();
    private Task? _worker;
    private ushort _sequence;

    public AudioTimingConnection(IPAddress remoteAddress, ushort remotePort)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);
        if (remoteAddress.AddressFamily != AddressFamily.InterNetwork)
            throw new NotSupportedException("The current AirPlay timing transport requires IPv4.");
        if (remotePort == 0)
            throw new ArgumentOutOfRangeException(nameof(remotePort));

        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        LocalPort = checked((ushort)((IPEndPoint)_socket.LocalEndPoint!).Port);
        _socket.Connect(new IPEndPoint(remoteAddress, remotePort));
    }

    public ushort LocalPort { get; }

    public void BeginMessageLoopWorker()
        => _worker ??= Task.Run(() => MessageLoopWorker(_tokenSource.Token), CancellationToken.None);

    public void EndMessageLoopWorker()
    {
        _tokenSource.Cancel();
        _socket.Close();
    }

    private async Task MessageLoopWorker(CancellationToken cancellationToken)
    {
        byte[] request = new byte[32];
        byte[] response = new byte[128];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                request.AsSpan().Clear();
                request[0] = 0x80;
                request[1] = 0xd2;
                BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), ++_sequence);
                WriteCurrentNtpTimestamp(request.AsSpan(24));

                await _socket.SendAsync(request, SocketFlags.None, cancellationToken).ConfigureAwait(false);

                using (var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    receiveTimeout.CancelAfter(TimeSpan.FromSeconds(1));
                    try
                    {
                        _ = await _socket.ReceiveAsync(response, SocketFlags.None, receiveTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // A missed timing response is expected on a lossy wireless LAN.
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal static void WriteCurrentNtpTimestamp(Span<byte> destination)
    {
        if (destination.Length < sizeof(ulong))
            throw new ArgumentException("An NTP timestamp requires eight bytes.", nameof(destination));

        long unixTicks = DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks;
        ulong seconds = (ulong)(unixTicks / TimeSpan.TicksPerSecond) + SecondsFrom1900To1970;
        ulong remainingTicks = (ulong)(unixTicks % TimeSpan.TicksPerSecond);
        ulong fraction = (remainingTicks << 32) / TimeSpan.TicksPerSecond;
        BinaryPrimitives.WriteUInt32BigEndian(destination, checked((uint)seconds));
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], (uint)fraction);
    }

    public void Dispose()
    {
        EndMessageLoopWorker();
        _socket.Dispose();
        _tokenSource.Dispose();
    }
}
