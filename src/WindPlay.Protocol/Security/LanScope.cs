using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AirPlay.Core2.Security;

/// <summary>One directly attached IPv4 LAN, selected once per receiver start.</summary>
public sealed class LanScope(IPAddress address, int prefixLength, int interfaceIndex = 0)
{
    public IPAddress Address { get; } = address;
    public int PrefixLength { get; } = prefixLength;
    public int InterfaceIndex { get; } = interfaceIndex;

    public bool Contains(IPAddress peer)
    {
        if (peer.IsIPv4MappedToIPv6) peer = peer.MapToIPv4();
        if (Address.AddressFamily != AddressFamily.InterNetwork || peer.AddressFamily != AddressFamily.InterNetwork ||
            PrefixLength is < 8 or > 32) return false;
        var local = Address.GetAddressBytes();
        var remote = peer.GetAddressBytes();
        for (int i = 0; i < 4; i++)
        {
            int bits = Math.Clamp(PrefixLength - i * 8, 0, 8);
            int mask = 0xff << (8 - bits) & 0xff;
            if ((local[i] & mask) != (remote[i] & mask)) return false;
        }
        return true;
    }

    public static LanScope Select()
    {
        // Do not infer trust from RFC1918 alone. Exclude tunnels and virtual adapters;
        // require a physical Wi-Fi/Ethernet interface with a gateway and an actual prefix.
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
                !n.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase) &&
                !n.Description.Contains("vpn", StringComparison.OrdinalIgnoreCase) &&
                !n.Description.Contains("tap", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Id, StringComparer.Ordinal);
        foreach (var nic in candidates)
        {
            var properties = nic.GetIPProperties();
            if (!properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                continue;
            foreach (var unicast in properties.UnicastAddresses)
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                    NetworkAccessPolicy.IsPrivateOrLocal(unicast.Address) && !IPAddress.IsLoopback(unicast.Address) &&
                    unicast.PrefixLength is >= 8 and <= 30)
                    return new(unicast.Address, unicast.PrefixLength, properties.GetIPv4Properties().Index);
        }
        throw new InvalidOperationException("No directly attached private Ethernet/Wi-Fi LAN is available. Disable VPNs and connect to a trusted LAN.");
    }
}
