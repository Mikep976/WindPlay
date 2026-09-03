using System.Buffers.Binary;
using AirPlay.Core2.Models.Messages.Mirror;
using Xunit;

namespace WindPlay.Protocol.Tests;

public sealed class H264AnnexBConverterTests
{
    [Fact]
    public void AccessUnitWithSeiBeforeIdrIsRecognizedAsKeyFrame()
    {
        byte[] accessUnit =
        [
            0, 0, 0, 2, 0x06, 0x01,
            0, 0, 0, 3, 0x65, 0xaa, 0xbb,
        ];

        bool converted = H264AnnexBConverter.TryConvertAccessUnit(accessUnit, out int frameType);

        Assert.True(converted);
        Assert.Equal(5, frameType);
        Assert.Equal([0, 0, 0, 1], accessUnit[..4]);
        Assert.Equal([0, 0, 0, 1], accessUnit[6..10]);
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 0, 0, 4, 0x65 })]
    [InlineData(new byte[] { 0, 0, 0, 1, 0x80 })]
    [InlineData(new byte[] { 0, 0, 0 })]
    public void MalformedAccessUnitIsRejected(byte[] accessUnit)
    {
        Assert.False(H264AnnexBConverter.TryConvertAccessUnit(accessUnit, out _));
    }

    [Fact]
    public void AvcConfigurationProducesAnnexBParameterSets()
    {
        byte[] configuration =
        [
            1, 0x64, 0, 0x1f, 0xff, 0xe1,
            0, 3, 0x67, 0x64, 0,
            1,
            0, 2, 0x68, 0xee,
        ];

        bool converted = H264AnnexBConverter.TryCreateParameterSets(configuration, out byte[] parameterSets);

        Assert.True(converted);
        Assert.Equal([0, 0, 0, 1, 0x67, 0x64, 0, 0, 0, 0, 1, 0x68, 0xee], parameterSets);
    }

    [Fact]
    public void TruncatedAvcConfigurationIsRejected()
    {
        byte[] configuration = [1, 0x64, 0, 0x1f, 0xff, 0xe1, 0, 10, 0x67];

        Assert.False(H264AnnexBConverter.TryCreateParameterSets(configuration, out _));
    }

    [Fact]
    public void MirroringHeaderUsesLittleEndianFieldsAndCorrectNtpFraction()
    {
        byte[] bytes = new byte[MirroringHeader.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 42);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 0x0000000180000000UL);

        MirroringHeader header = new(bytes);

        Assert.Equal(42, header.PayloadSize);
        Assert.Equal(0, header.PayloadType);
        Assert.Equal(1_500_000, header.PayloadPts);
    }
}
