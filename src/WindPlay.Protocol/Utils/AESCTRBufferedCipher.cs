using System.Security.Cryptography;
using System.Text;

namespace AirPlay.Core2.Utils;

internal class AESCTRBufferedCipher : IDisposable
{
    private const int BlockSize = 16;

    private readonly byte[] counter;
    private readonly byte[] keyStream = new byte[BlockSize];

    private readonly Aes aes;
    private readonly ICryptoTransform encryptor;
    private int keyStreamOffset = BlockSize;

    public AESCTRBufferedCipher(byte[] key, byte[] iv)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(iv);
        if (key.Length != BlockSize)
            throw new ArgumentException("AES-CTR requires a 128-bit key.", nameof(key));
        if (iv.Length != BlockSize)
            throw new ArgumentException("AES-CTR requires a 128-bit counter.", nameof(iv));

        counter = iv.ToArray();

        aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;

        encryptor = aes.CreateEncryptor();
    }

    public byte[] ProcessBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        byte[] output = GC.AllocateUninitializedArray<byte>(data.Length);
        Transform(data, output);
        return output;
    }

    public byte[] DoFinal(byte[] lastBlock) => ProcessBytes(lastBlock);

    public void Transform(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (output.Length < input.Length)
            throw new ArgumentException("The output buffer is too small.", nameof(output));

        int position = 0;
        while (position < input.Length)
        {
            if (keyStreamOffset == BlockSize)
                GenerateKeyStreamBlock();

            int count = Math.Min(BlockSize - keyStreamOffset, input.Length - position);
            for (int index = 0; index < count; index++)
                output[position + index] = (byte)(input[position + index] ^ keyStream[keyStreamOffset + index]);

            position += count;
            keyStreamOffset += count;
        }
    }

    public void TransformInPlace(Span<byte> data) => Transform(data, data);

    private void GenerateKeyStreamBlock()
    {
        encryptor.TransformBlock(counter, 0, BlockSize, keyStream, 0);
        keyStreamOffset = 0;

        for (int index = BlockSize - 1; index >= 0; index--)
        {
            if (++counter[index] != 0)
                break;
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(counter);
        CryptographicOperations.ZeroMemory(keyStream);
        aes.Dispose();
        encryptor.Dispose();
    }

    public static AESCTRBufferedCipher CreateDefault(byte[] ecdhShared)
    {
        byte[] aesKey = AESUtils.HashAndTruncate(Encoding.UTF8.GetBytes(AESUtils.PAIR_VERIFY_AES_KEY), ecdhShared);
        byte[] aesIv = AESUtils.HashAndTruncate(Encoding.UTF8.GetBytes(AESUtils.PAIR_VERIFY_AES_IV), ecdhShared);

        try
        {
            return new(aesKey, aesIv);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aesKey);
            CryptographicOperations.ZeroMemory(aesIv);
        }
    }

    public static AESCTRBufferedCipher CreateStream(string streamConnectionId, byte[] decryptedAesKey, byte[] ecdhShared)
    {
        byte[] eaesKey = AESUtils.HashAndTruncate(decryptedAesKey, ecdhShared);

        byte[] aesKey = AESUtils.HashAndTruncate(Encoding.UTF8.GetBytes(AESUtils.AIR_PLAY_STREAM_KEY + streamConnectionId), eaesKey);
        byte[] aesIv = AESUtils.HashAndTruncate(Encoding.UTF8.GetBytes(AESUtils.AIR_PLAY_STREAM_IV + streamConnectionId), eaesKey);

        try
        {
            return new(aesKey, aesIv);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(eaesKey);
            CryptographicOperations.ZeroMemory(aesKey);
            CryptographicOperations.ZeroMemory(aesIv);
        }
    }
}
