using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using AirPlay.Core2.Security;
using WindPlay.App.Configuration;

namespace WindPlay.App.Security;

public sealed record ReceiverSecrets(ReceiverIdentity Identity, string Passcode);

public static class IdentityStore
{
    private static readonly byte[] AdditionalEntropy = SHA256.HashData(
        Encoding.UTF8.GetBytes("WindPlay.receiver-identity.v1"));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ReceiverSecrets LoadOrCreate()
    {
        AppPaths.EnsureDataDirectory();
        if (File.Exists(AppPaths.IdentityFile))
        {
            try
            {
                return Load();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                CryptographicException or JsonException or FormatException or ArgumentException)
            {
                string quarantineName = Path.Combine(
                    AppPaths.DataDirectory,
                    $"identity-corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.dat");
                try
                {
                    File.Move(AppPaths.IdentityFile, quarantineName, overwrite: false);
                }
                catch (IOException)
                {
                    // A new identity can still be used for this run if quarantine races.
                }
            }
        }

        return CreateAndSave();
    }

    private static ReceiverSecrets Load()
    {
        var file = new FileInfo(AppPaths.IdentityFile);
        if (file.Length is <= 0 or > 32 * 1024)
            throw new InvalidDataException("Receiver identity file has an invalid size.");

        IdentityDocument document = JsonSerializer.Deserialize<IdentityDocument>(
            File.ReadAllText(file.FullName),
            JsonOptions) ?? throw new InvalidDataException("Receiver identity file is empty.");
        if (document.Version != 1)
            throw new InvalidDataException("Receiver identity file version is unsupported.");

        byte[] protectedSeed = Convert.FromBase64String(document.ProtectedSigningSeed);
        byte[] protectedPasscode = Convert.FromBase64String(document.ProtectedPasscode);
        byte[] seed = ProtectedData.Unprotect(protectedSeed, AdditionalEntropy, DataProtectionScope.CurrentUser);
        byte[] passcodeBytes = ProtectedData.Unprotect(protectedPasscode, AdditionalEntropy, DataProtectionScope.CurrentUser);
        try
        {
            string passcode = Encoding.UTF8.GetString(passcodeBytes);
            if (passcode.Length != 4 || !passcode.All(char.IsAsciiDigit))
                throw new InvalidDataException("Receiver passcode is invalid.");

            ReceiverIdentity identity = new(
                seed,
                Convert.FromHexString(document.DeviceIdentifier),
                Guid.Parse(document.PairingIdentifier),
                Guid.Parse(document.GroupIdentifier),
                Guid.Parse(document.DisplayIdentifier));
            return new ReceiverSecrets(identity, passcode);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
            CryptographicOperations.ZeroMemory(passcodeBytes);
        }
    }

    private static ReceiverSecrets CreateAndSave()
    {
        byte[] seed = RandomNumberGenerator.GetBytes(ReceiverIdentity.SigningSeedLength);
        byte[] deviceIdentifier = RandomNumberGenerator.GetBytes(ReceiverIdentity.DeviceIdentifierLength);
        string passcode = RandomNumberGenerator.GetInt32(10_000).ToString("D4", CultureInfo.InvariantCulture);
        byte[] passcodeBytes = Encoding.UTF8.GetBytes(passcode);
        Guid pairingIdentifier = Guid.NewGuid();
        Guid groupIdentifier = Guid.NewGuid();
        Guid displayIdentifier = Guid.NewGuid();

        try
        {
            byte[] protectedSeed = ProtectedData.Protect(seed, AdditionalEntropy, DataProtectionScope.CurrentUser);
            byte[] protectedPasscode = ProtectedData.Protect(passcodeBytes, AdditionalEntropy, DataProtectionScope.CurrentUser);
            IdentityDocument document = new(
                1,
                Convert.ToBase64String(protectedSeed),
                Convert.ToHexString(deviceIdentifier),
                pairingIdentifier.ToString("D"),
                groupIdentifier.ToString("D"),
                displayIdentifier.ToString("D"),
                Convert.ToBase64String(protectedPasscode));

            string temporaryFile = AppPaths.IdentityFile + ".new";
            File.WriteAllText(temporaryFile, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryFile, AppPaths.IdentityFile, overwrite: true);

            ReceiverIdentity identity = new(
                seed,
                deviceIdentifier,
                pairingIdentifier,
                groupIdentifier,
                displayIdentifier);
            return new ReceiverSecrets(identity, passcode);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
            CryptographicOperations.ZeroMemory(deviceIdentifier);
            CryptographicOperations.ZeroMemory(passcodeBytes);
        }
    }

    private sealed record IdentityDocument(
        int Version,
        string ProtectedSigningSeed,
        string DeviceIdentifier,
        string PairingIdentifier,
        string GroupIdentifier,
        string DisplayIdentifier,
        string ProtectedPasscode);
}
