using AirPlay.Core2.Security;
using Xunit;

namespace WindPlay.Protocol.Tests;

public sealed class ReceiverIdentityTests
{
    [Fact]
    public void SamePersistedMaterialProducesStableIdentity()
    {
        byte[] seed = Enumerable.Range(1, ReceiverIdentity.SigningSeedLength).Select(value => (byte)value).ToArray();
        byte[] deviceIdentifier = [0x01, 0x23, 0x45, 0x67, 0x89, 0xab];
        Guid pairingIdentifier = Guid.Parse("359fa20f-ebd1-4df0-bdc8-f1915b369a88");
        Guid groupIdentifier = Guid.Parse("b94b65f5-c0c2-4218-b98f-6f4a12176d1a");
        Guid displayIdentifier = Guid.Parse("77de7a2b-ef4d-40c7-b190-29167d4f8c7d");

        using var first = new ReceiverIdentity(seed, deviceIdentifier, pairingIdentifier, groupIdentifier, displayIdentifier);
        using var second = new ReceiverIdentity(seed, deviceIdentifier, pairingIdentifier, groupIdentifier, displayIdentifier);

        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.Equal("02:23:45:67:89:AB", first.DeviceId);
        Assert.Equal(first.PublicKey, second.PublicKey);
        Assert.Equal(64, first.PublicKeyHex.Length);
        Assert.Equal(pairingIdentifier, first.PairingIdentifier);
    }

    [Fact]
    public void RandomIdentityUsesLocallyAdministeredUnicastDeviceIdentifier()
    {
        using ReceiverIdentity identity = ReceiverIdentity.CreateRandom();
        byte firstOctet = Convert.ToByte(identity.DeviceId[..2], 16);

        Assert.Equal(0, firstOctet & 0x01);
        Assert.Equal(0x02, firstOctet & 0x02);
        Assert.Equal(17, identity.DeviceId.Length);
        Assert.NotEqual(Guid.Empty, identity.PairingIdentifier);
    }
}
