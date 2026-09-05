#if WINDPLAY_WINDOWS_TESTS
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AirPlay.Core2.Security;
using WindPlay.App.Configuration;
using WindPlay.App.Security;
using Xunit;

namespace WindPlay.Protocol.Tests
{
    // Compiles the real persistence classes against isolated test paths, without
    // initializing WinUI or touching the installed receiver's data directory.
    [SupportedOSPlatform("windows")]
    public sealed class WindowsIdentitySecurityTests : IDisposable
    {
        public WindowsIdentitySecurityTests() => AppPaths.EnsureDataDirectory();

        [Fact]
        public void DpapiRoundTripAndRotationPreserveIdentity()
        {
            ReceiverSecrets first = IdentityStore.LoadOrCreate();
            using var identity = first.Identity;
            Assert.True(ReceiverPassword.IsStrong(first.Passcode));
            Assert.DoesNotContain(first.Passcode, File.ReadAllText(AppPaths.IdentityFile));
            ReceiverSecrets loaded = IdentityStore.LoadOrCreate();
            using var loadedIdentity = loaded.Identity;
            Assert.Equal(first.Passcode, loaded.Passcode);
            Assert.Equal(identity.PublicKey.ToArray(), loadedIdentity.PublicKey.ToArray());
            string rotated = IdentityStore.RotatePassword();
            Assert.NotEqual(first.Passcode, rotated);
            Assert.True(ReceiverPassword.IsStrong(rotated));
            ReceiverSecrets replacement = IdentityStore.LoadOrCreate();
            using var replacementIdentity = replacement.Identity;
            Assert.Equal(rotated, replacement.Passcode);
            Assert.Equal(identity.PublicKey.ToArray(), replacementIdentity.PublicKey.ToArray());
        }

        [Fact]
        public void LegacyPinMigratesWithoutChangingReceiverIdentity()
        {
            ReceiverSecrets first = IdentityStore.LoadOrCreate();
            using var identity = first.Identity;
            var document = JsonNode.Parse(File.ReadAllText(AppPaths.IdentityFile))!.AsObject();
            byte[] entropy = SHA256.HashData(Encoding.UTF8.GetBytes("WindPlay.receiver-identity.v1"));
            document["protectedPasscode"] = Convert.ToBase64String(ProtectedData.Protect("1234"u8.ToArray(), entropy, DataProtectionScope.CurrentUser));
            File.WriteAllText(AppPaths.IdentityFile, document.ToJsonString());
            ReceiverSecrets migrated = IdentityStore.LoadOrCreate();
            using var migratedIdentity = migrated.Identity;
            Assert.True(ReceiverPassword.IsStrong(migrated.Passcode));
            Assert.Equal(identity.PublicKey.ToArray(), migratedIdentity.PublicKey.ToArray());
        }

        [Fact]
        public void CorruptDpapiSecretIsQuarantinedAndReplaced()
        {
            ReceiverSecrets first = IdentityStore.LoadOrCreate();
            first.Identity.Dispose();
            var document = JsonNode.Parse(File.ReadAllText(AppPaths.IdentityFile))!.AsObject();
            document["protectedPasscode"] = Convert.ToBase64String([1, 2, 3]);
            File.WriteAllText(AppPaths.IdentityFile, document.ToJsonString());
            ReceiverSecrets replacement = IdentityStore.LoadOrCreate();
            using var identity = replacement.Identity;
            Assert.True(ReceiverPassword.IsStrong(replacement.Passcode));
            Assert.Single(Directory.EnumerateFiles(AppPaths.DataDirectory, "identity-corrupt-*.dat"));
        }

        [Fact]
        public void PersistedSettingsCannotEnableRoutedAccess()
        {
            SettingsStore.Save(new ReceiverSettings { AllowNonPrivateNetworks = true, MaximumConnections = 100 });
            ReceiverSettings loaded = SettingsStore.Load();
            Assert.False(loaded.AllowNonPrivateNetworks);
            Assert.Equal(16, loaded.MaximumConnections);
        }

        public void Dispose() => Directory.Delete(AppPaths.DataDirectory, recursive: true);
    }
}

namespace WindPlay.App.Configuration
{
    internal static class AppPaths
    {
        public static string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "windplay-security-tests-" + Guid.NewGuid().ToString("N"));
        public static string IdentityFile => Path.Combine(DataDirectory, "identity.dat");
        public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
        public static void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);
    }
}
#endif
