using System.Buffers.Binary;
using System.Text;
using Claunia.PropertyList;

namespace AirPlay.Core2.Security;

/// <summary>Binary-only AirPlay plist subset. No generic dependency parser sees network bytes.</summary>
internal static class BoundedPlist
{
    public const int MaximumBytes = 256 * 1024;
    public const int MaximumObjects = 1024;
    public const int MaximumDepth = 16;
    public const int MaximumVisits = 4096;
    public const int MaximumDecodedBytes = 1024 * 1024;

    public static NSObject Parse(byte[] bytes)
    {
        var reader = new Reader(bytes);
        return NSObject.Wrap(reader.Parse());
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly int[] _offsets;
        private readonly bool[] _active;
        private readonly int _refs;
        private readonly int _table;
        private readonly int _root;
        private int _visits;
        private int _allocated;
        private int _limit;

        public Reader(ReadOnlySpan<byte> data)
        {
            if (data.Length is < 42 or > MaximumBytes || !data[..8].SequenceEqual("bplist00"u8))
                throw Invalid();
            _data = data;
            var trailer = data[^32..];
            int offsetSize = trailer[6];
            _refs = trailer[7];
            if (offsetSize is not (1 or 2 or 4 or 8) || _refs is not (1 or 2 or 4 or 8)) throw Invalid();
            ulong count = BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]);
            ulong root = BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]);
            ulong table = BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]);
            if (count is 0 or > MaximumObjects || root >= count || table < 8 ||
                table > (ulong)(data.Length - 32) || count * (uint)offsetSize != (ulong)(data.Length - 32) - table)
                throw Invalid();
            _table = (int)table;
            _root = (int)root;
            _offsets = new int[(int)count];
            _active = new bool[(int)count];
            _visits = _allocated = 0;
            _limit = _table;
            HashSet<int> distinct = [];
            for (int i = 0; i < _offsets.Length; i++)
            {
                ulong offset = Unsigned(data.Slice(_table + i * offsetSize, offsetSize));
                if (offset < 8 || offset >= table || !distinct.Add((int)offset)) throw Invalid();
                _offsets[i] = (int)offset;
            }
        }

        public object Parse()
        {
            // Validate unreachable objects too. Both this pass and expansion share a work/allocation budget.
            for (int i = 0; i < _offsets.Length; i++) _ = Read(i, 0);
            return Read(_root, 0);
        }

        private object Read(int id, int depth)
        {
            if ((uint)id >= (uint)_offsets.Length || depth > MaximumDepth || ++_visits > MaximumVisits || _active[id])
                throw Invalid();
            _active[id] = true;
            int previousLimit = _limit;
            _limit = _table;
            foreach (int offset in _offsets)
                if (offset > _offsets[id] && offset < _limit) _limit = offset;
            try
            {
                int cursor = _offsets[id];
                byte marker = Take(ref cursor, 1)[0];
                int kind = marker >> 4, count = marker & 15;
                if (kind == 0)
                    return marker switch { 8 => false, 9 => true, _ => throw Invalid() };
                if (kind == 1)
                {
                    if (count > 3) throw Invalid();
                    return unchecked((long)Unsigned(Take(ref cursor, 1 << count)));
                }
                if (kind == 2)
                {
                    double value = count switch
                    {
                        2 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(Take(ref cursor, 4))),
                        3 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(Take(ref cursor, 8))),
                        _ => throw Invalid()
                    };
                    if (!double.IsFinite(value)) throw Invalid();
                    return value;
                }
                if (kind is not (4 or 5 or 6 or 10 or 13)) throw Invalid();
                if (count == 15)
                {
                    byte lengthMarker = Take(ref cursor, 1)[0];
                    if ((lengthMarker >> 4) != 1 || (lengthMarker & 15) > 3) throw Invalid();
                    ulong length = Unsigned(Take(ref cursor, 1 << (lengthMarker & 15)));
                    if (length > MaximumBytes) throw Invalid();
                    count = (int)length;
                }
                if (kind is 4 or 5 or 6)
                {
                    if (kind != 4 && count > 4096) throw Invalid();
                    var bytes = Take(ref cursor, checked(count * (kind == 6 ? 2 : 1)));
                    Charge(checked(bytes.Length * 2 + 32));
                    if (kind == 4) return bytes.ToArray();
                    if (kind == 5 && bytes.ContainsAnyInRange((byte)128, byte.MaxValue)) throw Invalid();
                    try { Encoding encoding = kind == 6 ? new UnicodeEncoding(true, false, true) : new UTF8Encoding(false, true); return encoding.GetString(bytes); }
                    catch (DecoderFallbackException) { throw Invalid(); }
                }
                if (count > 128) throw Invalid();
                int referenceStart = cursor;
                _ = Take(ref cursor, checked(count * _refs * (kind == 13 ? 2 : 1)));
                Charge(checked(count * 128 + 64));
                if (kind == 10)
                {
                    object[] items = new object[count];
                    for (int i = 0; i < count; i++) items[i] = Read(Reference(referenceStart + i * _refs), depth + 1);
                    return items;
                }
                Dictionary<string, object> values = new(StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    if (Read(Reference(referenceStart + i * _refs), depth + 1) is not string key || key.Length > 128 || values.ContainsKey(key))
                        throw Invalid();
                    values.Add(key, Read(Reference(referenceStart + (count + i) * _refs), depth + 1));
                }
                return values;
            }
            finally { _active[id] = false; _limit = previousLimit; }
        }

        private int Reference(int position)
        {
            ulong id = Unsigned(_data.Slice(position, _refs));
            if (id >= (ulong)_offsets.Length) throw Invalid();
            return (int)id;
        }

        private ReadOnlySpan<byte> Take(ref int cursor, int count)
        {
            if (count < 0 || cursor > _limit || count > _limit - cursor) throw Invalid();
            var value = _data.Slice(cursor, count);
            cursor += count;
            return value;
        }

        private void Charge(int bytes)
        {
            if (bytes > MaximumDecodedBytes - _allocated) throw Invalid();
            _allocated += bytes;
        }

        private static ulong Unsigned(ReadOnlySpan<byte> bytes)
        {
            ulong value = 0;
            foreach (byte b in bytes) value = (value << 8) | b;
            return value;
        }
    }

    private static InvalidDataException Invalid() => new("Invalid or over-budget binary AirPlay plist.");
}
