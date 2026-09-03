using AirPlay.Core2.Connections.Audio;
using Xunit;

namespace WindPlay.Protocol.Tests;

public sealed class AudioControlConnectionTests
{
    [Fact]
    public void ResendRequestUsesEightByteRaopLayout()
    {
        byte[] packet = AudioControlConnection.CreateResendPacket(
            controlSequence: 0x1234,
            missingSequence: 0xABCD,
            count: 0x0003);

        Assert.Equal(
            [0x80, 0xD5, 0x12, 0x34, 0xAB, 0xCD, 0x00, 0x03],
            packet);
    }
}
