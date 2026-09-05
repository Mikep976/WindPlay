namespace AirPlay.Core2.Security;

/// <summary>Bounds coded decoder surfaces from SPS, not just sender-supplied display metadata.</summary>
internal static class H264SpsLimits
{
    public static bool IsSafe(ReadOnlySpan<byte> nal)
    {
        if (nal.Length is < 5 or > 4096 || (nal[0] & 0x9f) != 7) return false;
        Span<byte> rbsp = stackalloc byte[4096];
        int size = 0, zeros = 0;
        for (int i = 1; i < nal.Length; i++)
        {
            byte value = nal[i];
            if (zeros >= 2 && value == 3)
            {
                if (i + 1 == nal.Length || nal[i + 1] > 3) return false;
                zeros = 0; continue;
            }
            rbsp[size++] = value;
            zeros = value == 0 ? zeros + 1 : 0;
        }
        try
        {
            var bits = new Bits(rbsp[..size]);
            uint profile = bits.Read(8);
            bits.Read(8);
            if (bits.Read(8) > 52) return false;
            bits.Ue(31); // sequence_parameter_set_id
            if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                if (bits.Ue(3) != 1 || bits.Ue(6) != 0 || bits.Ue(6) != 0) return false; // 8-bit 4:2:0 only
                bits.Read(1);
                if (bits.Read(1) != 0)
                    for (int list = 0; list < 8; list++)
                        if (bits.Read(1) != 0)
                        {
                            int last = 8, next = 8;
                            for (int i = 0; i < (list < 6 ? 16 : 64); i++)
                            {
                                if (next != 0) next = (last + bits.Se() + 256) & 255;
                                last = next == 0 ? last : next;
                            }
                        }
            }
            else if (profile is not (66 or 77 or 88)) return false;
            bits.Ue(12);
            uint order = bits.Ue(2);
            if (order == 0) bits.Ue(12);
            else if (order == 1)
            {
                bits.Read(1); bits.Se(); bits.Se();
                uint cycle = bits.Ue(255);
                for (uint i = 0; i < cycle; i++) bits.Se();
            }
            bits.Ue(4); // Bound DPB reference pressure.
            bits.Read(1);
            int width = checked(((int)bits.Ue(255) + 1) * 16);
            int height = checked(((int)bits.Ue(255) + 1) * 16);
            if (bits.Read(1) == 0) height *= 2;
            MirrorLimits.ValidateDimensions(width, height);
            return true;
        }
        catch (InvalidDataException) { return false; }
    }

    private ref struct Bits(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;
        public uint Read(int count)
        {
            if (count > _data.Length * 8 - _position) throw new InvalidDataException("Truncated SPS.");
            uint value = 0;
            for (int i = 0; i < count; i++, _position++) value = (value << 1) | (uint)((_data[_position / 8] >> (7 - _position % 8)) & 1);
            return value;
        }
        public uint Ue(uint maximum)
        {
            int zeros = 0;
            while (Read(1) == 0) if (++zeros > 16) throw new InvalidDataException("SPS integer exceeds budget.");
            uint value = ((1u << zeros) - 1) + Read(zeros);
            if (value > maximum) throw new InvalidDataException("SPS value exceeds budget.");
            return value;
        }
        public int Se()
        { uint value = Ue(65535); return (value & 1) == 0 ? -(int)(value / 2) : (int)((value + 1) / 2); }
    }
}
