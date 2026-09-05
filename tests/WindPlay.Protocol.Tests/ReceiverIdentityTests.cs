using AirPlay.Core2.Security;
using Xunit;

namespace WindPlay.Protocol.Tests;

public sealed class ReceiverIdentityTests
{
    [Fact]
    public void SigningMatchesRfc8032EmptyMessageVector()
    {
        using var identity = new ReceiverIdentity(Convert.FromHexString(
            "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60"),
            new byte[6], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", identity.PublicKeyHex);
        Assert.Equal("E5564300C360AC729086E2CC806E828A84877F1EB8E5D974D873E06522490155" +
            "5FB8821590A33BACC61E39701CF9B46BD25BF5F0595BBE24655141438E7A100B",
            Convert.ToHexString(identity.SignMessage([])));
        identity.Dispose();
        Assert.Throws<ObjectDisposedException>(() => identity.SignMessage([]));
    }

    [Fact]
    public void PasswordHasHighEntropyAlphabetAndRejectsLegacyPins()
    {
        string first = ReceiverPassword.Create(), second = ReceiverPassword.Create();
        Assert.True(ReceiverPassword.IsStrong(first));
        Assert.Equal(20, first.Length);
        Assert.NotEqual(first, second);
        Assert.False(ReceiverPassword.IsStrong("1234"));
    }

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
