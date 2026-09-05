using System.Security.Cryptography;

namespace AirPlay.Core2.Security;

public static class ReceiverPassword
{
    // 20 independent base32 symbols = 100 bits. No look-alike I, O, 0, or 1.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public static string Create() => string.Create(20, 0, (characters, _) =>
    { for (int i = 0; i < characters.Length; i++) characters[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]; });
    public static bool IsStrong(string value) => value.Length == 20 && value.All(Alphabet.Contains);
}
