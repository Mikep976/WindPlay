using AirPlay.Core2.Decoders;
using AirPlay.Core2.Models.Messages.Audio;
using Xunit;

namespace WindPlay.Protocol.Tests;

public sealed class NativeAudioDecoderTests
{
    [Theory]
    [InlineData(AudioFormat.Unknown, 44_100, 2, 16, 352)]
    [InlineData(AudioFormat.ALAC, 48_000, 2, 16, 352)]
    [InlineData(AudioFormat.ALAC, 44_100, 1, 16, 352)]
    [InlineData(AudioFormat.ALAC, 44_100, 2, 24, 352)]
    [InlineData(AudioFormat.ALAC, 44_100, 2, 16, 480)]
    [InlineData(AudioFormat.AAC_ELD, 44_100, 2, 16, 352)]
    [InlineData(AudioFormat.AAC, 44_100, 2, 16, 480)]
    public void ConfigRejectsProfilesOutsideAdvertisedBoundary(
        AudioFormat format,
        int sampleRate,
        int channels,
        int bitDepth,
        int frameLength)
    {
        using NativeAudioDecoder decoder = new(format);

        int result = decoder.Config(sampleRate, channels, bitDepth, frameLength);

        Assert.Equal(-1, result);
        Assert.Equal(0, decoder.GetOutputStreamLength());
    }
}
