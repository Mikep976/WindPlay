using System.Security.Cryptography;
using System.Text;
using AirPlay.Core2.Models.Messages.Rtsp;

namespace AirPlay.Core2.Security;

/// <summary>Validates the HTTP Digest variant used by password-protected RAOP receivers.</summary>
public static class DigestAuthenticator
{
    public const string Realm = "WindPlay";

    public static string CreateNonce()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static string CreateChallenge(string nonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        return $"Digest realm=\"{Realm}\", nonce=\"{nonce}\", algorithm=MD5, qop=\"auth\"";
    }

    public static bool Verify(
        string authorization,
        string method,
        string requestTarget,
        string password,
        string expectedNonce)
    {
        if (!TryParseParameters(authorization, out Dictionary<string, string> parameters) ||
            !parameters.TryGetValue("username", out string? username) ||
            !parameters.TryGetValue("realm", out string? realm) ||
            !parameters.TryGetValue("nonce", out string? nonce) ||
            !parameters.TryGetValue("uri", out string? uri) ||
            !parameters.TryGetValue("response", out string? actualResponse) ||
            !string.Equals(realm, Realm, StringComparison.Ordinal) ||
            !string.Equals(nonce, expectedNonce, StringComparison.Ordinal) ||
            !string.Equals(uri, requestTarget, StringComparison.Ordinal) ||
            actualResponse.Length != 32)
            return false;

        if (parameters.TryGetValue("algorithm", out string? algorithm) &&
            !string.Equals(algorithm, "MD5", StringComparison.OrdinalIgnoreCase))
            return false;

        string ha1 = Md5Hex($"{username}:{realm}:{password}");
        string ha2 = Md5Hex($"{method}:{uri}");
        string expectedResponse;

        if (parameters.TryGetValue("qop", out string? qop))
        {
            if (!string.Equals(qop, "auth", StringComparison.OrdinalIgnoreCase) ||
                !parameters.TryGetValue("nc", out string? nonceCount) ||
                nonceCount.Length != 8 ||
                !parameters.TryGetValue("cnonce", out string? clientNonce) ||
                clientNonce.Length is 0 or > 256)
                return false;

            expectedResponse = Md5Hex($"{ha1}:{nonce}:{nonceCount}:{clientNonce}:auth:{ha2}");
        }
        else
        {
            expectedResponse = Md5Hex($"{ha1}:{nonce}:{ha2}");
        }

        try
        {
            byte[] expected = Convert.FromHexString(expectedResponse);
            byte[] actual = Convert.FromHexString(actualResponse);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryParseParameters(
        string authorization,
        out Dictionary<string, string> parameters)
    {
        parameters = [];
        if (authorization.Length is 0 or > 4096 ||
            !authorization.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
            return false;

        Dictionary<string, string> parsed = new(StringComparer.OrdinalIgnoreCase);
        ReadOnlySpan<char> value = authorization.AsSpan(7);
        int position = 0;

        while (position < value.Length)
        {
            while (position < value.Length && (value[position] is ' ' or '\t' or ','))
                position++;
            if (position == value.Length)
                break;

            int nameStart = position;
            while (position < value.Length && (char.IsAsciiLetterOrDigit(value[position]) || value[position] is '-' or '_'))
                position++;
            if (position == nameStart)
                return false;

            string name = value[nameStart..position].ToString();
            while (position < value.Length && value[position] is ' ' or '\t')
                position++;
            if (position >= value.Length || value[position++] != '=')
                return false;
            while (position < value.Length && value[position] is ' ' or '\t')
                position++;

            string parameterValue;
            if (position < value.Length && value[position] == '"')
            {
                position++;
                StringBuilder builder = new();
                bool terminated = false;
                while (position < value.Length)
                {
                    char character = value[position++];
                    if (character == '"')
                    {
                        terminated = true;
                        break;
                    }
                    if (character == '\\')
                    {
                        if (position >= value.Length)
                            return false;
                        character = value[position++];
                    }
                    if (char.IsControl(character))
                        return false;
                    builder.Append(character);
                }
                if (!terminated)
                    return false;
                parameterValue = builder.ToString();
            }
            else
            {
                int parameterStart = position;
                while (position < value.Length && value[position] != ',')
                    position++;
                parameterValue = value[parameterStart..position].Trim().ToString();
            }

            if (parameterValue.Length > 1024 || !parsed.TryAdd(name, parameterValue))
                return false;
        }

        parameters = parsed;
        return parsed.Count > 0;
    }

    private static string Md5Hex(string value)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
