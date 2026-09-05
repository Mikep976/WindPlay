using System.Buffers.Binary;
using AirPlay.Core2.Security;

namespace AirPlay.Core2.Models.Messages.Mirror;

public static class H264AnnexBConverter
{
    private const int MaximumCodecConfigurationBytes = 64 * 1024;
    private static ReadOnlySpan<byte> StartCode => [0, 0, 0, 1];

    /// <summary>Converts four-byte big-endian NAL lengths to Annex-B start codes in place.</summary>
    public static bool TryConvertAccessUnit(Span<byte> accessUnit, out int frameType)
    {
        frameType = 0;
        if (accessUnit.Length > MirrorLimits.MaximumFrameBytes) return false;
        int offset = 0;
        int firstType = 0;
        bool containsIdr = false;

        while (offset < accessUnit.Length)
        {
            if (accessUnit.Length - offset < sizeof(uint))
                return false;

            uint rawLength = BinaryPrimitives.ReadUInt32BigEndian(accessUnit[offset..]);
            if (rawLength is 0 or > int.MaxValue)
                return false;

            int nalLength = (int)rawLength;
            int nalOffset = offset + sizeof(uint);
            if (nalLength > accessUnit.Length - nalOffset)
                return false;
            if ((accessUnit[nalOffset] & 0x80) != 0)
                return false;

            int nalType = accessUnit[nalOffset] & 0x1f;
            if (nalType is not (1 or 5 or 6 or 7 or 8 or 9 or 12)) return false;
            if (!HasValidEscaping(accessUnit.Slice(nalOffset, nalLength))) return false;
            if (nalType == 7 && !H264SpsLimits.IsSafe(accessUnit.Slice(nalOffset, nalLength))) return false;
            if (firstType == 0)
                firstType = nalType;
            containsIdr |= nalType == 5;

            StartCode.CopyTo(accessUnit[offset..]);
            offset = nalOffset + nalLength;
        }

        if (offset != accessUnit.Length || firstType == 0)
            return false;

        frameType = containsIdr ? 5 : firstType;
        return true;
    }

    /// <summary>Extracts all SPS/PPS units from an AVCDecoderConfigurationRecord.</summary>
    public static bool TryCreateParameterSets(ReadOnlySpan<byte> configuration, out byte[] parameterSets)
    {
        parameterSets = [];
        if (configuration.Length is < 7 or > MaximumCodecConfigurationBytes || configuration[0] != 1 || (configuration[4] & 0x03) != 3)
            return false;

        int offset = 6;
        int totalLength = 0;
        List<(int Offset, int Length)> units = [];

        int sequenceParameterSetCount = configuration[5] & 0x1f;
        if (sequenceParameterSetCount == 0 ||
            !TryReadUnits(configuration, sequenceParameterSetCount, 7, ref offset, units, ref totalLength))
            return false;

        if (offset >= configuration.Length)
            return false;

        int pictureParameterSetCount = configuration[offset++];
        if (pictureParameterSetCount == 0 ||
            !TryReadUnits(configuration, pictureParameterSetCount, 8, ref offset, units, ref totalLength))
            return false;

        if (totalLength > MaximumCodecConfigurationBytes)
            return false;

        parameterSets = GC.AllocateUninitializedArray<byte>(totalLength);
        int destinationOffset = 0;
        foreach ((int sourceOffset, int length) in units)
        {
            StartCode.CopyTo(parameterSets.AsSpan(destinationOffset));
            destinationOffset += StartCode.Length;
            configuration.Slice(sourceOffset, length).CopyTo(parameterSets.AsSpan(destinationOffset));
            destinationOffset += length;
        }

        return true;
    }

    private static bool TryReadUnits(
        ReadOnlySpan<byte> configuration,
        int count,
        int expectedType,
        ref int offset,
        List<(int Offset, int Length)> units,
        ref int totalLength)
    {
        if (count > 16) return false;
        for (int index = 0; index < count; index++)
        {
            if (configuration.Length - offset < sizeof(ushort))
                return false;

            int length = BinaryPrimitives.ReadUInt16BigEndian(configuration[offset..]);
            offset += sizeof(ushort);
            if (length == 0 || length > configuration.Length - offset)
                return false;
            if ((configuration[offset] & 0x9f) != expectedType) return false;
            if (!HasValidEscaping(configuration.Slice(offset, length))) return false;
            if ((configuration[offset] & 0x1f) == 7 && !H264SpsLimits.IsSafe(configuration.Slice(offset, length)))
                return false;

            totalLength = checked(totalLength + StartCode.Length + length);
            if (totalLength > MaximumCodecConfigurationBytes)
                return false;

            units.Add((offset, length));
            offset += length;
        }

        return true;
    }

    // Annex-B conversion must not expose a second, unvalidated NAL hidden inside
    // a length-prefixed unit. Check the complete EBSP, including PPS and slices.
    private static bool HasValidEscaping(ReadOnlySpan<byte> nal)
    {
        int zeros = 0;
        for (int i = 1; i < nal.Length; i++)
        {
            byte value = nal[i];
            if (zeros >= 2)
            {
                if (value < 3) return false;
                if (value == 3)
                {
                    if (i + 1 == nal.Length || nal[i + 1] > 3) return false;
                    zeros = 0;
                    continue;
                }
            }
            zeros = value == 0 ? zeros + 1 : 0;
        }
        return true;
    }
}
