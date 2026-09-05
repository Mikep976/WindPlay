using AirPlay.Core2.Security;
using AirPlay.Core2.Models.Messages.Mirror;
using Xunit;

namespace WindPlay.Protocol.Tests;

public sealed class SpsSecurityTests
{
    [Fact]
    public void SpsActualCodedDimensionsAreBounded()
    {
        Assert.True(H264SpsLimits.IsSafe(CreateSps(120, 68)));
        Assert.True(H264SpsLimits.IsSafe(CreateSps(240, 135)));
        Assert.False(H264SpsLimits.IsSafe(CreateSps(256, 256)));
        Assert.False(H264SpsLimits.IsSafe([0x67, 66, 0, 31, 0, 0, 0, 0]));
        Assert.False(H264SpsLimits.IsSafe([0x67, 66, 0]));
    }

    internal static byte[] CreateSps(int widthMacroblocks, int heightMacroblocks)
    {
        var bits = new System.Text.StringBuilder("010000100000000000011111"); // Baseline, level 3.1
        void Ue(int value) { string code = Convert.ToString(value + 1, 2); bits.Append('0', code.Length - 1); bits.Append(code); }
        Ue(0); Ue(0); Ue(0); Ue(0); Ue(1); bits.Append('0');
        Ue(widthMacroblocks - 1); Ue(heightMacroblocks - 1); bits.Append("11001");
        while (bits.Length % 8 != 0) bits.Append('0');
        var data = new List<byte> { 0x67 };
        for (int i = 0; i < bits.Length; i += 8) data.Add(Convert.ToByte(bits.ToString(i, 8), 2));
        return data.ToArray();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    public void EmbeddedAnnexBStartCodeCannotSmuggleAnotherSps(byte type)
    {
        byte[] accessUnit = [0, 0, 0, 6, type, 0, 0, 1, 0x67, 0xff];
        Assert.False(H264AnnexBConverter.TryConvertAccessUnit(accessUnit, out _));
    }

    [Fact]
    public void EscapedDataIsAcceptedButInvalidPreventionByteIsRejected()
    {
        Assert.True(H264AnnexBConverter.TryConvertAccessUnit([0, 0, 0, 5, 0x65, 0, 0, 3, 1], out _));
        Assert.False(H264AnnexBConverter.TryConvertAccessUnit([0, 0, 0, 5, 0x65, 0, 0, 3, 4], out _));
    }

    [Fact]
    public void OversizedInlineSpsAndHiddenSpsInPpsAreRejected()
    {
        byte[] sps = CreateSps(256, 256);
        byte[] accessUnit = [0, 0, 0, (byte)sps.Length, .. sps];
        Assert.False(H264AnnexBConverter.TryConvertAccessUnit(accessUnit, out _));
        sps = CreateSps(120, 68);
        byte[] config = [1, 66, 0, 31, 0xff, 0xe1, 0, (byte)sps.Length, .. sps,
            1, 0, 6, 0x68, 0, 0, 1, 0x67, 0xff];
        Assert.False(H264AnnexBConverter.TryCreateParameterSets(config, out _));
    }
}
