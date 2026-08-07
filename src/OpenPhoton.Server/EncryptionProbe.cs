using System.Security.Cryptography;
using Photon.SocketServer.Security;

namespace OpenPhoton.Server;

/// <summary>
/// Searches the space of key/padding/IV conventions against real ciphertext.
/// </summary>
/// <remarks>
/// <para>
/// Diffie-Hellman succeeds silently even when the two sides disagree about how to turn the
/// shared secret into a key, so a mismatch only surfaces as an undecryptable message with
/// no clue as to which convention differs. The search space is small and each trial is
/// cheap, so enumerating it against a real message answers the question once instead of
/// forcing a guess per attempt.
/// </para>
/// <para>
/// Photon's own <c>EncryptionMethod</c> enum names two schemes — <c>Sha256Pkcs7</c> and
/// <c>Md5Iso10126</c> — which fixes the interesting axes: the hash used to derive the key
/// (and therefore AES-256 versus AES-128) travels together with the padding mode.
/// </para>
/// </remarks>
public static class EncryptionProbe
{
    public sealed record Candidate(string Description, byte[] Key, PaddingMode Padding, bool IvIsPrefixed);

    /// <summary>Runs the search and returns anything that decrypts to a plausible operation.</summary>
    public static IReadOnlyList<(Candidate Candidate, byte[] Plaintext)> Search(
        DiffieHellmanKeyExchange exchange, byte[] peerPublicKey, ReadOnlySpan<byte> ciphertext)
    {
        var found = new List<(Candidate, byte[])>();

        foreach (var (secretName, secret) in exchange.DeriveCandidateSecrets(peerPublicKey))
        {
            foreach (var (keyName, key, padding) in KeysFor(secret))
            {
                foreach (var prefixedIv in (bool[])[false, true])
                {
                    var description = $"{secretName}/{keyName}/{padding}/{(prefixedIv ? "ivPrefix" : "ivZero")}";
                    if (TryDecrypt(key, padding, prefixedIv, ciphertext, out var plaintext)
                        && LooksLikeOperation(plaintext!))
                    {
                        found.Add((new Candidate(description, key, padding, prefixedIv), plaintext!));
                    }
                }
            }
        }

        return found;
    }

    static IEnumerable<(string Name, byte[] Key, PaddingMode Padding)> KeysFor(byte[] secret)
    {
        yield return ("sha256", SHA256.HashData(secret), PaddingMode.PKCS7);
        yield return ("md5", MD5.HashData(secret), PaddingMode.ISO10126);

        yield return ("sha256", SHA256.HashData(secret), PaddingMode.ISO10126);
        yield return ("md5", MD5.HashData(secret), PaddingMode.PKCS7);

        if (secret.Length >= 32) yield return ("raw32", secret[..32], PaddingMode.PKCS7);
        if (secret.Length >= 16) yield return ("raw16", secret[..16], PaddingMode.PKCS7);
    }

    static bool TryDecrypt(byte[] key, PaddingMode padding, bool prefixedIv, ReadOnlySpan<byte> input, out byte[]? plaintext)
    {
        plaintext = null;
        if (input.Length < 16 || input.Length % 16 != 0) return false;

        var iv = prefixedIv ? input[..16].ToArray() : new byte[16];
        var body = prefixedIv ? input[16..] : input;
        if (body.Length == 0 || body.Length % 16 != 0) return false;

        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.CBC;
            aes.Padding = padding;
            plaintext = aes.DecryptCbc(body, iv);
            return plaintext.Length > 0;
        }
        catch (CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>
    /// Rejects the coincidences.
    /// </summary>
    /// <remarks>
    /// ISO10126 padding validates almost any input, since only the final byte is checked, so
    /// padding success alone means nothing. A real body here is an operation request: an
    /// opcode, then a 16-bit parameter count that is small and whose high byte is zero.
    /// </remarks>
    static bool LooksLikeOperation(byte[] plaintext)
    {
        if (plaintext.Length < 3) return false;

        var parameterCountHigh = plaintext[1];
        var parameterCountLow = plaintext[2];
        return parameterCountHigh == 0 && parameterCountLow <= 32;
    }
}
