using System.Security.Cryptography;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace AirPlay.Core2.Security;

/// <summary>
/// Stable, per-install receiver identity used for AirPlay discovery and pairing.
/// Private signing material is never exposed after construction.
/// </summary>
public sealed class ReceiverIdentity : IDisposable
{
    public const int SigningSeedLength = 32;
    public const int DeviceIdentifierLength = 6;

    private readonly byte[] _signingSeed;
    private bool _disposed;

    public ReceiverIdentity(
        ReadOnlySpan<byte> signingSeed,
        ReadOnlySpan<byte> deviceIdentifier,
        Guid pairingIdentifier,
        Guid groupIdentifier,
        Guid displayIdentifier)
    {
        if (signingSeed.Length != SigningSeedLength)
            throw new ArgumentException($"The signing seed must be {SigningSeedLength} bytes.", nameof(signingSeed));
        if (deviceIdentifier.Length != DeviceIdentifierLength)
            throw new ArgumentException($"The device identifier must be {DeviceIdentifierLength} bytes.", nameof(deviceIdentifier));
        if (pairingIdentifier == Guid.Empty)
            throw new ArgumentException("The pairing identifier cannot be empty.", nameof(pairingIdentifier));
        if (groupIdentifier == Guid.Empty)
            throw new ArgumentException("The group identifier cannot be empty.", nameof(groupIdentifier));
        if (displayIdentifier == Guid.Empty)
            throw new ArgumentException("The display identifier cannot be empty.", nameof(displayIdentifier));

        _signingSeed = signingSeed.ToArray();

        byte[] normalizedDeviceIdentifier = deviceIdentifier.ToArray();
        normalizedDeviceIdentifier[0] = (byte)((normalizedDeviceIdentifier[0] | 0x02) & 0xfe);
        DeviceId = string.Join(':', normalizedDeviceIdentifier.Select(value => value.ToString("X2")));
        DeviceIdCompact = Convert.ToHexString(normalizedDeviceIdentifier);

        PairingIdentifier = pairingIdentifier;
        GroupIdentifier = groupIdentifier;
        DisplayIdentifier = displayIdentifier;

        PublicKey = new byte[32];
        Ed25519.GeneratePublicKey(_signingSeed, 0, PublicKey, 0);
        PublicKeyHex = Convert.ToHexString(PublicKey).ToLowerInvariant();
    }

    public string DeviceId { get; }

    public string DeviceIdCompact { get; }

    public Guid PairingIdentifier { get; }

    public Guid GroupIdentifier { get; }

    public Guid DisplayIdentifier { get; }

    public byte[] PublicKey { get; }

    public string PublicKeyHex { get; }

    public static ReceiverIdentity CreateRandom()
    {
        Span<byte> seed = stackalloc byte[SigningSeedLength];
        Span<byte> deviceIdentifier = stackalloc byte[DeviceIdentifierLength];
        RandomNumberGenerator.Fill(seed);
        RandomNumberGenerator.Fill(deviceIdentifier);

        try
        {
            return new ReceiverIdentity(seed, deviceIdentifier, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
            CryptographicOperations.ZeroMemory(deviceIdentifier);
        }
    }

    internal byte[] SignMessage(byte[] message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] signature = new byte[64];
        Ed25519.Sign(_signingSeed, 0, message, 0, message.Length, signature, 0);
        return signature;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CryptographicOperations.ZeroMemory(_signingSeed);
        GC.SuppressFinalize(this);
    }
}
