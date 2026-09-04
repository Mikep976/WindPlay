using AirPlay.Core2.Connections;
using AirPlay.Core2.Models.Messages.Audio;
using Xunit;

namespace WindPlay.Protocol.Tests;

public sealed class RtspStreamSetupTests
{
    [Fact]
    public void AudioAndMirrorCanBeNegotiatedTogether()
    {
        object[] streams =
        [
            new Dictionary<string, object>
            {
                ["type"] = 96L,
                ["audioFormat"] = (long)AudioFormat.AAC_ELD,
                ["controlPort"] = 49_152L,
                ["latencyMin"] = 0L,
                ["latencyMax"] = 11_025L,
            },
            new Dictionary<string, object>
            {
                ["type"] = 110UL,
                ["streamConnectionID"] = ulong.MaxValue,
            },
        ];

        bool parsed = RtspConnection.TryParseStreamSetups(
            streams,
            hasAudioController: false,
            hasMirrorController: false,
            out List<RtspConnection.StreamSetup> setups);

        Assert.True(parsed);
        RtspConnection.AudioStreamSetup audio = Assert.IsType<RtspConnection.AudioStreamSetup>(setups[0]);
        Assert.Equal(AudioFormat.AAC_ELD, audio.Format);
        Assert.Equal((ushort)49_152, audio.ControlPort);
        Assert.Equal(0, audio.LatencyMin);
        Assert.Equal(11_025, audio.LatencyMax);
        RtspConnection.MirrorStreamSetup mirror = Assert.IsType<RtspConnection.MirrorStreamSetup>(setups[1]);
        Assert.Equal(ulong.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), mirror.StreamConnectionId);
    }

    [Fact]
    public void DuplicateStreamTypeIsRejectedBeforeControllersStart()
    {
        Dictionary<string, object> stream = new()
        {
            ["type"] = 110,
            ["streamConnectionID"] = 42,
        };

        bool parsed = RtspConnection.TryParseStreamSetups(
            [stream, new Dictionary<string, object>(stream)],
            hasAudioController: false,
            hasMirrorController: false,
            out List<RtspConnection.StreamSetup> setups);

        Assert.False(parsed);
        Assert.Empty(setups);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1234)]
    [InlineData(int.MaxValue)]
    public void UnsupportedAudioFormatIsRejected(int audioFormat)
    {
        object[] streams =
        [
            new Dictionary<string, object>
            {
                ["type"] = 96,
                ["audioFormat"] = audioFormat,
                ["controlPort"] = 50_000,
            },
        ];

        Assert.False(RtspConnection.TryParseStreamSetups(
            streams,
            hasAudioController: false,
            hasMirrorController: false,
            out _));
    }

    [Fact]
    public void ExistingControllerCannotBeSilentlyReplaced()
    {
        object[] streams =
        [
            new Dictionary<string, object>
            {
                ["type"] = 96,
                ["audioFormat"] = (int)AudioFormat.ALAC,
                ["controlPort"] = 50_000,
            },
        ];

        Assert.False(RtspConnection.TryParseStreamSetups(
            streams,
            hasAudioController: true,
            hasMirrorController: false,
            out _));
    }
}
