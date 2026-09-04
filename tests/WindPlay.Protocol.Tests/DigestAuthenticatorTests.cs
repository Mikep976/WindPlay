using AirPlay.Core2.Security;
using Xunit;

namespace WindPlay.Protocol.Tests;

public sealed class DigestAuthenticatorTests
{
    [Fact]
    public void ValidLegacyDigestIsAccepted()
    {
        const string authorization = "Digest username=\"Mike\", realm=\"raop\", " +
            "nonce=\"0123456789abcdef\", uri=\"/pair-setup\", " +
            "response=\"e226e57f4bf1bad1c3783b74c45c7392\"";

        Assert.True(DigestAuthenticator.Verify(
            authorization, "POST", "/pair-setup", "1234", "0123456789abcdef"));
    }

    [Fact]
    public void ValidQopAuthDigestIsAccepted()
    {
        const string authorization = "Digest username=\"Mike\", realm=\"raop\", " +
            "nonce=\"0123456789abcdef\", uri=\"/pair-setup\", qop=auth, " +
            "nc=00000001, cnonce=\"a1b2c3\", response=\"8ea56aaf293fa60473aa30c7b12ee4e1\"";

        Assert.True(DigestAuthenticator.Verify(
            authorization, "POST", "/pair-setup", "1234", "0123456789abcdef"));
    }

    [Theory]
    [InlineData("wrong-password", "0123456789abcdef", "/pair-setup")]
    [InlineData("1234", "wrong-nonce", "/pair-setup")]
    [InlineData("1234", "0123456789abcdef", "/different")]
    public void ModifiedCredentialMaterialIsRejected(string password, string nonce, string target)
    {
        const string authorization = "Digest username=\"Mike\", realm=\"raop\", " +
            "nonce=\"0123456789abcdef\", uri=\"/pair-setup\", " +
            "response=\"e226e57f4bf1bad1c3783b74c45c7392\"";

        Assert.False(DigestAuthenticator.Verify(authorization, "POST", target, password, nonce));
    }

    [Fact]
    public void DuplicateOrMalformedFieldsAreRejected()
    {
        const string authorization = "Digest username=\"Mike\", username=\"Other\", realm=\"raop\"";

        Assert.False(DigestAuthenticator.Verify(
            authorization, "POST", "/pair-setup", "1234", "nonce"));
    }

    [Fact]
    public void ChallengeUsesTheAppleCompatibleLegacyShape()
        => Assert.Equal(
            "Digest realm=\"raop\", nonce=\"0123456789abcdef\"",
            DigestAuthenticator.CreateChallenge("0123456789abcdef"));
}
