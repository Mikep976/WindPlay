using AirPlay.Core2.Controllers;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

using SyncData = (ulong SyncTime, ulong SyncTimestamp);
using ResendRequest = (ushort MissingSeqNum, ushort Count);

namespace AirPlay.Core2.Connections.Audio;

public class AudioControlConnection : IDisposable
{
    private const ulong OFFSET_1900_TO_1970 = 2208988800UL;

    private readonly Socket _udpListener = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    //private readonly ushort _sendPort;
    private ushort _controlSeqNum = 0;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _tokenSource = new();

    public event EventHandler<SyncData>? SyncDataReceived;
    public event EventHandler<byte[]>? ResentDataReceived;

    public AudioControlConnection(IPAddress remoteAddress, ushort remotePort)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);
        if (remoteAddress.AddressFamily != AddressFamily.InterNetwork)
            throw new NotSupportedException("The current RAOP audio transport requires IPv4.");

        _udpListener.Bind(new IPEndPoint(IPAddress.Any, 0));
        LocalPort = checked((ushort)((IPEndPoint)_udpListener.LocalEndPoint!).Port);
        _udpListener.Connect(new IPEndPoint(remoteAddress, remotePort));
    }

    public ushort LocalPort { get; }

    public void BeginControlMessageLoopWorker()
    {
        Task.Run(async () => await ControlMessageLoopWorker(_tokenSource.Token), _tokenSource.Token);
    }

    public void EndControlMessageLoopWorker()
    {
        _tokenSource.Cancel();
        _udpListener.Close();
    }

    public void HandleResendPacket(ResendRequest resendRequest)
    {
        if (!_udpListener.Connected) return;

        lock (_lock)
        {
            _controlSeqNum++;

            byte[] packet = CreateResendPacket(_controlSeqNum, resendRequest.MissingSeqNum, resendRequest.Count);

            _udpListener.Send(packet, 0, packet.Length, SocketFlags.None);
        }
    }

    internal static byte[] CreateResendPacket(ushort controlSequence, ushort missingSequence, ushort count)
        =>
        [
            0x80,                          // RTP Version + Marker (Marker=1)
            0x55 | 0x80,                   // Payload type 85 + Marker bit
            (byte)(controlSequence >> 8),
            (byte)controlSequence,
            (byte)(missingSequence >> 8),
            (byte)missingSequence,
            (byte)(count >> 8),
            (byte)count,
        ];

    private async Task ControlMessageLoopWorker(CancellationToken cancellationToken)
    {
        byte[] packet = ArrayPool<byte>.Shared.Rent(AudioController.RAOP_PACKET_LENGTH);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int udpReceiveResult = await _udpListener.ReceiveAsync(packet, SocketFlags.None, cancellationToken);

                if (udpReceiveResult < 2)
                    continue;

                int type = packet[1] & ~0x80;

                if (type == 0x56)
                {
                    if (udpReceiveResult >= 16)
                        ResentDataReceived?.Invoke(this, packet.AsSpan(4, udpReceiveResult - 4).ToArray());
                }
                else if (type == 0x54 && udpReceiveResult >= 20)
                {
                    /* packetlen = 20
                     * bytes	description
                        8	RTP header without SSRC
                        8	current NTP time
                        4	RTP timestamp for the next audio packet
                     */

                    ulong seconds = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8));
                    ulong fraction = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(12));
                    ulong ntpTime = seconds * 1_000_000UL + (fraction * 1_000_000UL >> 32);
                    uint rtpTimestamp = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4));

                    ulong epochOffset = OFFSET_1900_TO_1970 * 1_000_000UL;
                    SyncDataReceived?.Invoke(this, (ntpTime >= epochOffset ? ntpTime - epochOffset : ntpTime, rtpTimestamp));
                }
                else
                {
                    //Console.WriteLine("Unknown packet");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    public void Dispose()
    {
        _udpListener.Dispose();
        _tokenSource.Dispose();
    }
}
