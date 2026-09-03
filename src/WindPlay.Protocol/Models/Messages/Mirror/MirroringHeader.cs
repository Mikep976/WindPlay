using System.Buffers.Binary;

namespace AirPlay.Core2.Models.Messages.Mirror;

public readonly struct MirroringHeader
{
    public const int Length = 128;

    public int PayloadSize { get; }
    public short PayloadType { get; }
    public short PayloadOption { get; }
    public long PayloadNtp { get; }
    public long PayloadPts { get; }
    public int WidthSource { get; }
    public int HeightSource { get; }
    public int Width { get; }
    public int Height { get; }

    public MirroringHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < Length)
            throw new ArgumentException($"A mirroring header must be {Length} bytes.", nameof(header));

        uint payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (payloadSize > int.MaxValue)
            throw new InvalidDataException("The mirroring payload length is invalid.");

        PayloadSize = (int)payloadSize;
        PayloadType = (short)(BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) & 0xff);
        PayloadOption = (short)BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);

        if (PayloadType == 0)
        {
            PayloadNtp = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
            PayloadPts = NtpToPts(PayloadNtp);
        }
        else if (PayloadType == 1)
        {
            WidthSource = ReadSingleLittleEndian(header[40..]);
            HeightSource = ReadSingleLittleEndian(header[44..]);
            Width = ReadSingleLittleEndian(header[56..]);
            Height = ReadSingleLittleEndian(header[60..]);
        }
    }

    private static long NtpToPts(long ntp)
        => checked(((ntp >> 32) & 0xffffffffL) * 1_000_000L +
            (long)(((ulong)ntp & 0xffffffffUL) * 1_000_000UL >> 32));

    private static int ReadSingleLittleEndian(ReadOnlySpan<byte> data)
    {
        float value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data));
        return float.IsFinite(value) && value is >= 0 and <= 16_384 ? (int)value : 0;
    }
}
