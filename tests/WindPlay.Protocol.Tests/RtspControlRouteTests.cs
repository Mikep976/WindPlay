using AirPlay.Core2.Connections;
using Claunia.PropertyList;
using Xunit;

namespace WindPlay.Protocol.Tests;

public sealed class RtspControlRouteTests
{
    [Theory]
    [InlineData("seq=0", 0)]
    [InlineData("rtptime=123; seq=65535", 65535)]
    [InlineData("SEQ=42;rtptime=123", 42)]
    public void RtpSequenceUsesBoundedInvariantParsing(string value, int expected)
    {
        Assert.True(RtspConnection.TryParseRtpSequence(value, out int sequence));
        Assert.Equal(expected, sequence);
    }

    [Theory]
    [InlineData("seq=-1")]
    [InlineData("seq=65536")]
    [InlineData("seq=12.5")]
    [InlineData("rtptime=123")]
    public void InvalidRtpSequenceIsRejected(string value)
        => Assert.False(RtspConnection.TryParseRtpSequence(value, out _));

    [Theory]
    [InlineData("0.000000", 0)]
    [InlineData("-30.5", -30.5)]
    [InlineData("-144", -144)]
    public void VolumeUsesAirPlayDecibelRange(string value, double expected)
    {
        Assert.True(RtspConnection.TryParseVolume(value, out double volume));
        Assert.Equal(expected, volume);
    }

    [Theory]
    [InlineData("0,5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("0.1")]
    [InlineData("-145")]
    public void InvalidVolumeIsRejected(string value)
        => Assert.False(RtspConnection.TryParseVolume(value, out _));

    [Fact]
    public void ProgressUsesMonotonicSamplePositions()
    {
        Assert.True(RtspConnection.TryParseProgress("44100/88200/132300".AsSpan(), out var progress));
        Assert.Equal(TimeSpan.FromSeconds(2), progress.Duration);
        Assert.Equal(TimeSpan.FromSeconds(1), progress.Position);
    }

    [Theory]
    [InlineData("2/1/3")]
    [InlineData("1/3/2")]
    [InlineData("1/2")]
    [InlineData("one/2/3")]
    public void InvalidProgressIsRejected(string value)
        => Assert.False(RtspConnection.TryParseProgress(value.AsSpan(), out _));

    [Fact]
    public void EmptyTeardownClosesTheWholeSession()
    {
        Assert.True(RtspConnection.TryParseTeardown([], out bool audio, out bool mirror, out bool session));
        Assert.True(audio);
        Assert.True(mirror);
        Assert.True(session);
    }

    [Fact]
    public void TeardownCanCloseBothBundledStreams()
    {
        NSDictionary root = new()
        {
            {
                "streams",
                new NSArray
                {
                    new NSDictionary { { "type", 96 } },
                    new NSDictionary { { "type", 110 } },
                }
            },
        };
        byte[] body = BinaryPropertyListWriter.WriteToArray(root);

        Assert.True(RtspConnection.TryParseTeardown(body, out bool audio, out bool mirror, out bool session));
        Assert.True(audio);
        Assert.True(mirror);
        Assert.False(session);
    }

    [Fact]
    public void MalformedTeardownCannotPartiallyCloseAController()
    {
        NSDictionary root = new()
        {
            {
                "streams",
                new NSArray
                {
                    new NSDictionary { { "type", 96 } },
                    new NSDictionary { { "type", 999 } },
                }
            },
        };
        byte[] body = BinaryPropertyListWriter.WriteToArray(root);

        Assert.False(RtspConnection.TryParseTeardown(body, out bool audio, out bool mirror, out bool session));
        Assert.False(audio);
        Assert.False(mirror);
        Assert.False(session);
    }
}
