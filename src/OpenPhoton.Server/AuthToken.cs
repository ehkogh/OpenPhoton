using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace OpenPhoton.Server;

/// <summary>
/// Signed, expiring token issued by the name server and presented to the master and game servers.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes <c>AuthMode.AuthOnce</c> work: the client authenticates once, then
/// carries a token that the later servers validate without re-running authentication or
/// calling out anywhere. Photon does this with a shared secret in config — its
/// <c>Nameserver.xml.config</c> and <c>Master.xml.config</c> both carry the same
/// <c>AuthTokenKey</c> and <c>HMACTokenKey</c>, and an <c>AuthTokenExpirationSeconds</c> of
/// 1800 and 3600 respectively.
/// </para>
/// <para>
/// The token is opaque to the client, which stores and replays it without inspection, so
/// the encoding is ours to choose. What is not optional is that it be signed and expiring:
/// an unsigned token is a self-issued claim of identity, and any peer that reached the game
/// server directly could mint one for any user.
/// </para>
/// </remarks>
public sealed class AuthTokenIssuer(byte[] hmacKey, TimeSpan? lifetime = null)
{
    readonly byte[] _hmacKey = hmacKey;
    readonly TimeSpan _lifetime = lifetime ?? TimeSpan.FromSeconds(1800);

    /// <summary>Creates an issuer with a random key. Fine for one process; useless across a restart.</summary>
    public static AuthTokenIssuer CreateEphemeral() => new(RandomNumberGenerator.GetBytes(32));

    /// <summary>Creates an issuer from a shared secret, so several roles agree on validity.</summary>
    public static AuthTokenIssuer FromSecret(string secret, TimeSpan? lifetime = null) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), lifetime);

    public string Issue(string userId)
    {
        var expiry = DateTimeOffset.UtcNow.Add(_lifetime).ToUnixTimeSeconds();
        var payload = Encode(userId, expiry);
        var signature = HMACSHA256.HashData(_hmacKey, payload);

        return $"{Base64Url(payload)}.{Base64Url(signature)}";
    }

    /// <summary>Validates a token and recovers the user it was issued for.</summary>
    public bool TryValidate(object? token, out string? userId, out string? error)
    {
        userId = null;
        error = null;

        if (token is not string text || string.IsNullOrWhiteSpace(text))
        {
            error = "Missing token.";
            return false;
        }

        var separator = text.IndexOf('.');
        if (separator <= 0)
        {
            error = "Malformed token.";
            return false;
        }

        byte[] payload, signature;
        try
        {
            payload = FromBase64Url(text[..separator]);
            signature = FromBase64Url(text[(separator + 1)..]);
        }
        catch (FormatException)
        {
            error = "Malformed token.";
            return false;
        }

        var expected = HMACSHA256.HashData(_hmacKey, payload);
        if (!CryptographicOperations.FixedTimeEquals(expected, signature))
        {
            error = "Bad token signature.";
            return false;
        }

        if (payload.Length < 8)
        {
            error = "Truncated token.";
            return false;
        }

        var expiry = BinaryPrimitives.ReadInt64BigEndian(payload);
        if (DateTimeOffset.FromUnixTimeSeconds(expiry) < DateTimeOffset.UtcNow)
        {
            error = "Token expired.";
            return false;
        }

        userId = Encoding.UTF8.GetString(payload, 8, payload.Length - 8);
        return true;
    }

    static byte[] Encode(string userId, long expiry)
    {
        var name = Encoding.UTF8.GetBytes(userId);
        var payload = new byte[8 + name.Length];
        BinaryPrimitives.WriteInt64BigEndian(payload, expiry);
        name.CopyTo(payload, 8);
        return payload;
    }

    static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException() };
        return Convert.FromBase64String(padded);
    }
}
