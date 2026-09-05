using System.Buffers.Binary;
using System.Text;

namespace AirPlay.Core2.Discovery;

internal sealed record DnsQuestion(string Name, ushort Type, ushort Class);
internal sealed record DnsRecord(string Name, ushort Type, uint Ttl, byte[] Data, string? Target, ushort Port);
internal sealed record DnsPacket(bool Response, DnsQuestion[] Questions, DnsRecord[] Records)
{
    public const int MaximumBytes = 9000;
    public const int MaximumRecords = 64;

    public static DnsPacket Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length is < 12 or > MaximumBytes) throw Invalid();
        int questions = U16(packet, 4);
        int records = U16(packet, 6) + U16(packet, 8) + U16(packet, 10);
        if (questions > 16 || records > MaximumRecords || (U16(packet, 2) & 0x780f) != 0) throw Invalid();
        var qs = new DnsQuestion[questions];
        var rs = new DnsRecord[records];
        int cursor = 12, work = 0;
        for (int i = 0; i < questions; i++)
        {
            string name = ReadName(packet, ref cursor, ref work);
            Require(packet, cursor, 4);
            qs[i] = new(name, U16(packet, cursor), U16(packet, cursor + 2));
            cursor += 4;
        }
        for (int i = 0; i < records; i++)
        {
            string name = ReadName(packet, ref cursor, ref work);
            Require(packet, cursor, 10);
            ushort type = U16(packet, cursor);
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(packet[(cursor + 4)..]);
            int size = U16(packet, cursor + 8);
            cursor += 10;
            Require(packet, cursor, size);
            int end = cursor + size;
            string? target = null;
            ushort port = 0;
            if (type is 12 or 33 or 5)
            {
                int pos = cursor;
                if (type == 33)
                {
                    if (size < 7) throw Invalid();
                    port = U16(packet, pos + 4);
                    pos += 6;
                }
                target = ReadName(packet, ref pos, ref work);
                if (pos != end) throw Invalid();
            }
            if ((type == 1 && size != 4) || (type == 28 && size != 16)) throw Invalid();
            if (type == 16)
            {
                int pos = cursor;
                while (pos < end) { int length = packet[pos++]; if (length > end - pos) throw Invalid(); pos += length; }
            }
            // Only A records need raw bytes. Unknown RDATA is never copied or decoded.
            rs[i] = new(name, type, ttl, type == 1 ? packet.Slice(cursor, size).ToArray() : [], target, port);
            cursor = end;
        }
        if (cursor != packet.Length) throw Invalid();
        return new((U16(packet, 2) & 0x8000) != 0, qs, rs);
    }

    internal static string ReadName(ReadOnlySpan<byte> packet, ref int cursor, ref int work)
    {
        int position = cursor, resume = -1, wireLength = 1, steps = 0;
        Span<int> visited = stackalloc int[128];
        StringBuilder name = new(255);
        while (true)
        {
            if (++work > 8192 || steps >= visited.Length) throw Invalid();
            for (int i = 0; i < steps; i++) if (visited[i] == position) throw Invalid();
            visited[steps++] = position;
            Require(packet, position, 1);
            int length = packet[position++];
            if (length == 0) { cursor = resume >= 0 ? resume : position; return name.ToString(); }
            if ((length & 0xc0) == 0xc0)
            {
                Require(packet, position, 1);
                int target = ((length & 0x3f) << 8) | packet[position++];
                if (target < 12 || target >= position - 2) throw Invalid();
                if (resume < 0) resume = position;
                position = target;
                continue;
            }
            if (length > 63 || (wireLength += length + 1) > 255) throw Invalid();
            Require(packet, position, length);
            if (name.Length > 0) name.Append('.');
            try { name.Append(new UTF8Encoding(false, true).GetString(packet.Slice(position, length))); }
            catch (DecoderFallbackException) { throw Invalid(); }
            position += length;
        }
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
    private static void Require(ReadOnlySpan<byte> bytes, int offset, int count)
    { if (offset < 0 || count < 0 || offset > bytes.Length || count > bytes.Length - offset) throw Invalid(); }
    private static InvalidDataException Invalid() => new("Invalid or over-budget mDNS packet.");
}
