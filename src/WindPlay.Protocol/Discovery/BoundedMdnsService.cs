using System.Net;
using System.Net.Sockets;
using AirPlay.Core2.Models.Configs;
using AirPlay.Core2.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AirPlay.Core2.Discovery;

/// <summary>IPv4 mDNS on the selected LAN only; fixed receive buffer and bounded work.</summary>
public sealed class BoundedMdnsService(LanScope scope, IOptions<AirTunesConfig> options, ReceiverIdentity identity) : BackgroundService
{
    private static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);
    private readonly WorkBudget _input = new(256, 32, TimeSpan.FromSeconds(1));
    private readonly WorkBudget _output = new(8, 2, TimeSpan.FromSeconds(1));
    private Socket? _socket;
    private byte[] _advertisement = [];
    private byte[] _goodbye = [];
    private readonly byte[] _query = DnsWriter.Query("_dacp._tcp.local");
    internal event Action<DnsPacket, IPAddress>? PacketReceived;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.AllowNonPrivateNetworks) throw new InvalidOperationException("Routed/public access is disabled in security-hardened builds.");
        string name = options.Value.ServiceName.Replace('.', '_');
        if (System.Text.Encoding.UTF8.GetByteCount(name) > 40) throw new InvalidOperationException("Receiver name must fit 40 UTF-8 bytes.");
        var services = new[]
        {
            ("_airplay._tcp.local", name + "._airplay._tcp.local", options.Value.Port,
                AirPlayPublisher.PackTxtRecord(AirPlayPublisher.GetAirPlayTxtProperties(options.Value, identity))),
            ("_raop._tcp.local", identity.DeviceIdCompact + "@" + name + "._raop._tcp.local", options.Value.Port,
                AirPlayPublisher.PackTxtRecord(AirPlayPublisher.GetAirTunesTxtProperties(options.Value, identity)))
        };
        string host = "windplay-" + identity.DeviceIdCompact + ".local";
        _advertisement = DnsWriter.Advertise(host, scope.Address, services, 120);
        _goodbye = DnsWriter.Advertise(host, scope.Address, services, 0);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.ReceiveBufferSize = 64 * 1024;
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
        _socket.Bind(new IPEndPoint(IPAddress.Any, 5353));
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group.Address, scope.Address));
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, scope.Address.GetAddressBytes());
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        => await Task.WhenAll(ReceiveAsync(stoppingToken), AnnounceAsync(stoppingToken));

    private async Task ReceiveAsync(CancellationToken token)
    {
        byte[] buffer = new byte[DnsPacket.MaximumBytes + 1];
        while (!token.IsCancellationRequested)
        {
            SocketReceiveMessageFromResult received;
            try { received = await _socket!.ReceiveMessageFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), token); }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.MessageSize) { continue; }
            if ((received.SocketFlags & SocketFlags.Truncated) != 0 ||
                received.PacketInformation.Interface != scope.InterfaceIndex ||
                received.ReceivedBytes > DnsPacket.MaximumBytes || received.RemoteEndPoint is not IPEndPoint source ||
                !scope.Contains(source.Address) || !_input.TryCharge(source.Address)) continue;
            DnsPacket packet;
            try { packet = DnsPacket.Parse(buffer.AsSpan(0, received.ReceivedBytes)); }
            catch (InvalidDataException) { continue; }
            if (packet.Response && source.Port == 5353) PacketReceived?.Invoke(packet, source.Address);
            else if (!packet.Response && source.Port == 5353 && packet.Questions.Any(q =>
                (q.Class & 0x7fff) == 1 && (q.Name.EndsWith("._tcp.local", StringComparison.OrdinalIgnoreCase) ||
                    q.Name.EndsWith(".local", StringComparison.OrdinalIgnoreCase))) && _output.TryCharge(source.Address))
                await _socket!.SendToAsync(_advertisement, SocketFlags.None, Group, token);
        }
    }

    private async Task AnnounceAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await _socket!.SendToAsync(_advertisement, SocketFlags.None, Group, token);
            await _socket.SendToAsync(_query, SocketFlags.None, Group, token);
            await Task.Delay(TimeSpan.FromSeconds(30), token);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(1));
            if (_socket is not null) await _socket.SendToAsync(_goodbye, SocketFlags.None, Group, deadline.Token);
        }
        catch (SocketException) { }
        catch (OperationCanceledException) { }
        finally { await base.StopAsync(cancellationToken); _socket?.Dispose(); }
    }

    public override void Dispose() { base.Dispose(); _socket?.Dispose(); }
}
