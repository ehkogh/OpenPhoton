using System.Security.Cryptography;

namespace Photon.SocketServer.Security;

/// <summary>
/// Payload encryption using the key agreed by <see cref="DiffieHellmanKeyExchange"/>.
/// </summary>
/// <remarks>
/// <para>
/// Photon calls this Rijndael; with a 128-bit block it is AES. The key is the SHA-256 of the
/// Diffie-Hellman shared secret, giving AES-256 in CBC mode with PKCS#7 padding.
/// </para>
/// <para>
/// The client reports its mode in its own banner — <c>PayloadEncryption</c> for the plain
/// variant, which uses a fixed all-zero IV rather than sending one per message.
/// <see cref="EncryptionModes.PayloadEncryptionWithIV"/> prepends a random IV to each
/// message instead, and is the better construction: a fixed IV in CBC means identical
/// plaintexts encrypt identically, so an observer can tell when a peer repeats itself. We
/// implement the fixed-IV mode because it is what the client offers, and support the
/// prefixed-IV mode for peers that ask for it.
/// </para>
/// </remarks>
public sealed class RijndaelCryptoProvider(byte[] encryptionKey, EncryptionModes mode = EncryptionModes.PayloadEncryption)
{
    /// <summary>AES block size in bytes; also the IV length.</summary>
    public const int BlockSize = 16;

    readonly byte[] _key = encryptionKey.Length == 32
        ? encryptionKey
        : throw new ArgumentException("Key must be 32 bytes (AES-256).", nameof(encryptionKey));

    readonly EncryptionModes _mode = mode;

    bool PrefixesIv => _mode is EncryptionModes.PayloadEncryptionWithIV
                            or EncryptionModes.PayloadEncryptionWithIVHMAC;

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        using var aes = CreateAes();

        if (!PrefixesIv)
        {
            aes.IV = new byte[BlockSize];
            return aes.EncryptCbc(plaintext, aes.IV);
        }

        var iv = RandomNumberGenerator.GetBytes(BlockSize);
        var ciphertext = aes.EncryptCbc(plaintext, iv);

        var output = new byte[BlockSize + ciphertext.Length];
        iv.CopyTo(output, 0);
        ciphertext.CopyTo(output, BlockSize);
        return output;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext)
    {
        using var aes = CreateAes();

        if (!PrefixesIv) return aes.DecryptCbc(ciphertext, new byte[BlockSize]);

        if (ciphertext.Length < BlockSize)
            throw new CryptographicException("Message is shorter than the IV it should carry.");

        return aes.DecryptCbc(ciphertext[BlockSize..], ciphertext[..BlockSize]);
    }

    /// <summary>Decrypts without throwing, so a bad message costs one peer rather than the process.</summary>
    public bool TryDecrypt(ReadOnlySpan<byte> ciphertext, out byte[]? plaintext)
    {
        try
        {
            plaintext = Decrypt(ciphertext);
            return true;
        }
        catch (CryptographicException)
        {
            plaintext = null;
            return false;
        }
    }

    Aes CreateAes()
    {
        var aes = Aes.Create();
        aes.Key = _key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return aes;
    }
}

/// <summary>Encryption schemes a peer can negotiate.</summary>
public enum EncryptionModes
{
    /// <summary>AES-CBC over the payload with a fixed IV.</summary>
    PayloadEncryption = 0,

    /// <summary>AES-CBC over the payload with a per-message IV prefixed to the ciphertext.</summary>
    PayloadEncryptionWithIV = 1,

    /// <summary>As <see cref="PayloadEncryptionWithIV"/>, plus an HMAC over the message.</summary>
    PayloadEncryptionWithIVHMAC = 2,

    /// <summary>Whole-datagram encryption, used on UDP.</summary>
    DatagramEncryption = 10,

    /// <summary>Datagram encryption with randomised initial sequence numbers.</summary>
    DatagramEncryptionWithRandomInitialNumbers = 11,

    /// <summary>Datagram encryption using GCM.</summary>
    DatagramEncryptionGCM = 13,
}
