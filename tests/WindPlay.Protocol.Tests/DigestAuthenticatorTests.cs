using AirPlay.Core2.Security;
using Xunit;

namespace WindPlay.Protocol.Tests;

public sealed class DigestAuthenticatorTests
{
    [Fact]
    public void ValidLegacyDigestIsAccepted()
    {
        const string authorization = "Digest username=\"Mike\", realm=\"WindPlay\", " +
            "nonce=\"0123456789abcdef\", uri=\"/pair-setup\", " +
            "response=\"e4564c39ee6dbde217894d77c62658ae\"";

        Assert.True(DigestAuthenticator.Verify(
            authorization, "POST", "/pair-setup", "1234", "0123456789abcdef"));
    }

    [Fact]
    public void ValidQopAuthDigestIsAccepted()
    {
        const string authorization = "Digest username=\"Mike\", realm=\"WindPlay\", " +
            "nonce=\"0123456789abcdef\", uri=\"/pair-setup\", qop=auth, " +
            "nc=00000001, cnonce=\"a1b2c3\", response=\"eb41ba98b457ada21d32d6ae00e12573\"";

        Assert.True(DigestAuthenticator.Verify(
            authorization, "POST", "/pair-setup", "1234", "0123456789abcdef"));
    }

    [Theory]
    [InlineData("wrong-password", "0123456789abcdef", "/pair-setup")]
    [InlineData("1234", "wrong-nonce", "/pair-setup")]
    [InlineData("1234", "0123456789abcdef", "/different")]
    public void ModifiedCredentialMaterialIsRejected(string password, string nonce, string target)
    {
        const string authorization = "Digest username=\"Mike\", realm=\"WindPlay\", " +
            "nonce=\"0123456789abcdef\", uri=\"/pair-setup\", " +
            "response=\"e4564c39ee6dbde217894d77c62658ae\"";

        Assert.False(DigestAuthenticator.Verify(authorization, "POST", target, password, nonce));
    }

    [Fact]
    public void DuplicateOrMalformedFieldsAreRejected()
    {
        const string authorization = "Digest username=\"Mike\", username=\"Other\", realm=\"WindPlay\"";

        Assert.False(DigestAuthenticator.Verify(
            authorization, "POST", "/pair-setup", "1234", "nonce"));
    }
}
