using System.Net;
using AirPlay.Core2.Discovery;
using AirPlay.Core2.Models;
using AirPlay.Core2.Models.Configs;
using AirPlay.Core2.Security;
using AirPlay.Core2.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace WindPlay.Protocol.Tests;

public sealed class DacpSecurityTests
{
    [Fact]
    public void UnpairedDiscoveryFloodRetainsNoEntries()
    {
        using var identity = ReceiverIdentity.CreateRandom();
        using var mdns = new BoundedMdnsService(new(IPAddress.Loopback, 8), Options.Create(new AirTunesConfig()), identity);
        using var service = new DacpDiscoveryService(mdns, new SessionManager());
        for (int i = 0; i < 1000; i++) service.OnPacket(Packet(i.ToString(System.Globalization.CultureInfo.InvariantCulture), IPAddress.Loopback), IPAddress.Loopback);
        Assert.Equal(0, service.TrackedCount);
    }

    [Fact]
    public void DacpRequiresMatchingSenderAndExpiresAtBoundedTtl()
    {
        using var identity = ReceiverIdentity.CreateRandom();
        using var mdns = new BoundedMdnsService(new(IPAddress.Loopback, 8), Options.Create(new AirTunesConfig()), identity);
        var sessions = new SessionManager();
        using var service = new DacpDiscoveryService(mdns, sessions);
        using var session = new DeviceSession(new byte[16], new byte[32], 7001, IPAddress.Loopback)
        { DacpId = "ABC", ActiveRemote = "123", DeviceDisplayName = "test", DeviceMacAddress = "test", LocalAddress = IPAddress.Loopback };
        sessions.TryAddSession(new(IPAddress.Loopback, 5001), session);
        service.OnPacket(Packet("ABC", IPAddress.Loopback), IPAddress.Parse("127.0.0.2"));
        Assert.Null(session.DacpServiceEndPoint);
        service.OnPacket(Packet("ABC", IPAddress.Parse("127.0.0.2")), IPAddress.Loopback);
        Assert.Null(session.DacpServiceEndPoint);
        service.OnPacket(Packet("ABC", IPAddress.Loopback), IPAddress.Loopback);
        Assert.NotNull(session.DacpServiceEndPoint);
        Assert.Equal(1, service.TrackedCount);
        service.Expire(DateTimeOffset.UtcNow.AddSeconds(121));
        Assert.Null(session.DacpServiceEndPoint);
        Assert.Equal(0, service.TrackedCount);
    }

    private static DnsPacket Packet(string id, IPAddress address) => new(true, [],
    [new("iTunes_Ctrl_" + id + "._dacp._tcp.local", 33, uint.MaxValue, [], "sender.local", 7000),
     new("sender.local", 1, uint.MaxValue, address.GetAddressBytes(), null, 0)]);
}
