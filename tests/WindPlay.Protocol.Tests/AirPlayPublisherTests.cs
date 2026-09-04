using AirPlay.Core2;
using AirPlay.Core2.Models.Configs;
using AirPlay.Core2.Security;
using Xunit;

namespace WindPlay.Protocol.Tests;

public sealed class AirPlayPublisherTests
{
    [Fact]
    public void TxtRecordUsesDnsLengthPrefixedEntries()
    {
        KeyValuePair<string, string>[] properties =
        [
            new("ch", "2"),
            new("pw", "true"),
        ];

        byte[] record = AirPlayPublisher.PackTxtRecord(properties);

        Assert.Equal([4, (byte)'c', (byte)'h', (byte)'=', (byte)'2',
            7, (byte)'p', (byte)'w', (byte)'=', (byte)'t', (byte)'r', (byte)'u', (byte)'e'], record);
    }

    [Theory]
    [InlineData(true, "true", "0x84")]
    [InlineData(false, "false", "0x4")]
    public void DiscoveryPropertiesReflectPasswordPolicy(bool requirePassword, string passwordFlag, string systemFlags)
    {
        using ReceiverIdentity identity = CreateIdentity();
        AirTunesConfig config = new() { RequirePassword = requirePassword };

        Dictionary<string, string> raop = AirPlayPublisher
            .GetAirTunesTxtProperties(config, identity)
            .ToDictionary();
        Dictionary<string, string> airPlay = AirPlayPublisher
            .GetAirPlayTxtProperties(config, identity)
            .ToDictionary();

        Assert.Equal(passwordFlag, raop["pw"]);
        Assert.Equal(systemFlags, raop["sf"]);
        Assert.Equal("2", raop["ch"]);
        Assert.Equal("44100", raop["sr"]);
        Assert.Equal("16", raop["ss"]);
        Assert.Equal("1", raop["txtvers"]);
        Assert.Equal(passwordFlag, airPlay["pw"]);
        Assert.Equal("2", airPlay["vv"]);
        Assert.Equal(identity.DeviceId, airPlay["deviceid"]);
    }

    [Fact]
    public void TxtRecordRejectsOversizedEntry()
    {
        KeyValuePair<string, string>[] properties = [new("key", new string('x', 252))];

        Assert.Throws<InvalidOperationException>(() => AirPlayPublisher.PackTxtRecord(properties));
    }

    private static ReceiverIdentity CreateIdentity()
        => new(
            Enumerable.Range(0, ReceiverIdentity.SigningSeedLength).Select(value => (byte)value).ToArray(),
            [0, 1, 2, 3, 4, 5],
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"));
}
