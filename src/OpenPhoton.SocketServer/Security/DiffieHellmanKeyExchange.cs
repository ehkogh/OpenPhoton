using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace Photon.SocketServer.Security;

/// <summary>
/// Diffie-Hellman key agreement over the 768-bit MODP group, as Photon peers negotiate it.
/// </summary>
/// <remarks>
/// <para>
/// Established from a captured exchange: the client's key-exchange message carries a single
/// 96-byte parameter, and 96 bytes is 768 bits, which identifies the group as Oakley Group 1
/// (RFC 2409 section 6.1) with generator 2.
/// </para>
/// <para>
/// 768-bit MODP is far below what anyone would choose today, and no amount of care here
/// makes it strong. It is implemented because it is what the protocol specifies and a
/// compatible server has no say in the matter — the client offers this group and will not
/// negotiate another. The security this buys is obfuscation against casual inspection, not
/// confidentiality against an attacker who cares; anything genuinely sensitive should be
/// protected at a layer we control, such as running the whole thing over a VPN or TLS
/// tunnel.
/// </para>
/// </remarks>
public sealed class DiffieHellmanKeyExchange
{
    /// <summary>Oakley Group 1 prime (RFC 2409 §6.1), 768 bits.</summary>
    const string OakleyPrime768 =
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD1" +
        "29024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
        "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245" +
        "E485B576625E7EC6F44C42E9A63A3620FFFFFFFFFFFFFFFF";

    /// <summary>Public keys and the shared secret are all this wide.</summary>
    public const int KeyLength = 96;

    static readonly BigInteger Prime = ParsePrime();

    /// <summary>
    /// Generator 22, not the conventional 2.
    /// </summary>
    /// <remarks>
    /// Read from the client library, whose <c>OakleyGroups.Generator</c> is 22 and whose
    /// <c>DiffieHellmanCryptoProvider.primeRoot</c> is the same value. Assuming the usual 2
    /// produces a perfectly valid key agreement that simply disagrees with the peer's — both
    /// sides derive a secret, neither can tell they differ, and the only symptom is that
    /// every encrypted message afterwards fails to decrypt.
    /// </remarks>
    static readonly BigInteger Generator = new(22);

    readonly BigInteger _privateKey;

    public DiffieHellmanKeyExchange()
    {
        _privateKey = GeneratePrivateKey();
        PublicKey = ToFixedWidth(BigInteger.ModPow(Generator, _privateKey, Prime));
    }

    /// <summary>Our public key, to be sent to the peer.</summary>
    public byte[] PublicKey { get; }

    /// <summary>
    /// Derives the shared secret from the peer's public key.
    /// </summary>
    /// <remarks>
    /// The result is padded to a fixed width before use. Leading zero bytes occur in about
    /// one exchange in 256, and a variable-length secret would make both sides derive
    /// different keys on exactly those connections — an intermittent failure that looks like
    /// anything but its cause.
    /// </remarks>
    public byte[] DeriveSharedSecret(ReadOnlySpan<byte> peerPublicKey)
    {
        if (peerPublicKey.Length != KeyLength)
            throw new ArgumentException($"Public key must be {KeyLength} bytes, got {peerPublicKey.Length}.", nameof(peerPublicKey));

        var peer = new BigInteger(peerPublicKey, isUnsigned: true, isBigEndian: true);

        if (peer <= BigInteger.One || peer >= Prime - BigInteger.One)
            throw new ArgumentException("Peer public key is outside the safe range.", nameof(peerPublicKey));

        return ToFixedWidth(BigInteger.ModPow(peer, _privateKey, Prime));
    }

    /// <summary>Turns a shared secret into a 256-bit symmetric key.</summary>
    public static byte[] DeriveEncryptionKey(byte[] sharedSecret) => SHA256.HashData(sharedSecret);

    /// <summary>
    /// Every plausible reading of the peer's key and of the resulting secret.
    /// </summary>
    /// <remarks>
    /// Diffie-Hellman gives no signal when two sides disagree about byte order or padding:
    /// both compute a secret, neither can tell it differs, and the failure surfaces later as
    /// an undecryptable message. Rather than guess between the conventions, this enumerates
    /// them so the correct one can be identified against real ciphertext, once.
    /// </remarks>
    public IEnumerable<(string Name, byte[] Secret)> DeriveCandidateSecrets(byte[] peerPublicKey)
    {
        foreach (var (peerOrder, peer) in new (string, BigInteger)[]
        {
            ("peerBE", new BigInteger(peerPublicKey, isUnsigned: true, isBigEndian: true)),
            ("peerLE", new BigInteger(peerPublicKey, isUnsigned: true, isBigEndian: false)),
        })
        {
            if (peer <= BigInteger.One || peer >= Prime - BigInteger.One) continue;

            var secret = BigInteger.ModPow(peer, _privateKey, Prime);

            var bigEndian = secret.ToByteArray(isUnsigned: true, isBigEndian: true);
            var littleEndian = secret.ToByteArray(isUnsigned: true, isBigEndian: false);

            yield return ($"{peerOrder}/secretBEpadded", Pad(bigEndian));
            yield return ($"{peerOrder}/secretBEraw", bigEndian);
            yield return ($"{peerOrder}/secretLEpadded", Pad(littleEndian));
            yield return ($"{peerOrder}/secretLEraw", littleEndian);
        }
    }

    static byte[] Pad(byte[] raw)
    {
        if (raw.Length >= KeyLength) return raw;
        var padded = new byte[KeyLength];
        raw.CopyTo(padded, KeyLength - raw.Length);
        return padded;
    }

    static BigInteger ParsePrime() =>
        BigInteger.Parse("0" + OakleyPrime768, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    static BigInteger GeneratePrivateKey()
    {
        Span<byte> bytes = stackalloc byte[KeyLength];
        RandomNumberGenerator.Fill(bytes);
        return new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
    }

    /// <summary>Big-endian, left-padded to <see cref="KeyLength"/>.</summary>
    static byte[] ToFixedWidth(BigInteger value)
    {
        var raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (raw.Length == KeyLength) return raw;

        var padded = new byte[KeyLength];
        raw.CopyTo(padded, KeyLength - raw.Length);
        return padded;
    }
}
