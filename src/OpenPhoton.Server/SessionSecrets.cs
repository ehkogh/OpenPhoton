using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace OpenPhoton.Server;

/// <summary>
/// Payload-encryption secrets the name server hands out, kept for the master and game server.
/// </summary>
/// <remarks>
/// <para>
/// Under <c>AuthMode.AuthOnce</c> the client runs no key exchange on the master or game
/// server. If the name server's authenticate response carries <c>EncryptionData</c>, the
/// client instead encrypts with the secret given there (<c>RealtimeClient.SetupEncryption</c>
/// → <c>InitPayloadEncryption</c>), so whichever server receives those messages has to hold
/// the same secret.
/// </para>
/// <para>
/// Keyed by user rather than by token because every hop issues a fresh token, while the
/// secret is issued once at the name server and used for the rest of the session. All three
/// roles run in this process, so a shared table is enough; Photon carries it inside the
/// token instead, which only matters once the roles are split across machines.
/// </para>
/// </remarks>
public sealed class SessionSecrets
{
    /// <summary>Same size as the key the key exchange produces: one SHA-256 output.</summary>
    public const int SecretLength = 32;

    readonly ConcurrentDictionary<string, byte[]> _byUser = new();

    public byte[] Issue(string userId)
    {
        var secret = RandomNumberGenerator.GetBytes(SecretLength);
        _byUser[userId] = secret;
        return secret;
    }

    public bool TryGet(string userId, out byte[] secret) => _byUser.TryGetValue(userId, out secret!);
}
