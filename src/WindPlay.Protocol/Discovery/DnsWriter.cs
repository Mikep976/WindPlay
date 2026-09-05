using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace AirPlay.Core2.Discovery;

internal static class DnsWriter
{
    public static byte[] Query(string name)
    {
        using MemoryStream output = new();
        output.Write(new byte[12]);
        Name(output, name);
        U16(output, 12); U16(output, 1);
        byte[] result = output.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), 1);
        return result;
    }

    public static byte[] Advertise(string host, IPAddress address,
        (string Type, string Instance, ushort Port, byte[] Txt)[] services, uint ttl)
    {
        using MemoryStream output = new();
        output.Write(new byte[12]);
        foreach (var service in services)
        {
            Record(output, service.Type, 12, ttl, s => Name(s, service.Instance));
            Record(output, service.Instance, 33, ttl, s => { U16(s, 0); U16(s, 0); U16(s, service.Port); Name(s, host); });
            Record(output, service.Instance, 16, ttl, s => s.Write(service.Txt));
        }
        Record(output, host, 1, ttl, s => s.Write(address.GetAddressBytes()));
        byte[] packet = output.ToArray();
        if (packet.Length > DnsPacket.MaximumBytes) throw new InvalidOperationException("Discovery response exceeds limit.");
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0x8400);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), checked((ushort)(services.Length * 3 + 1)));
        return packet;
    }

    private static void Record(MemoryStream output, string name, ushort type, uint ttl, Action<MemoryStream> body)
    {
        Name(output, name); U16(output, type); U16(output, type == 12 ? (ushort)1 : (ushort)0x8001);
        Span<byte> value = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(value, ttl); output.Write(value);
        using MemoryStream data = new(); body(data);
        U16(output, checked((ushort)data.Length)); data.Position = 0; data.CopyTo(output);
    }

    private static void Name(Stream output, string name)
    {
        int total = 1;
        foreach (string label in name.TrimEnd('.').Split('.'))
        {
            byte[] data = Encoding.UTF8.GetBytes(label);
            if (data.Length is 0 or > 63 || (total += data.Length + 1) > 255) throw new InvalidOperationException("Invalid DNS name.");
            output.WriteByte((byte)data.Length); output.Write(data);
        }
        output.WriteByte(0);
    }

    private static void U16(Stream output, ushort value)
    { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, value); output.Write(bytes); }
}
