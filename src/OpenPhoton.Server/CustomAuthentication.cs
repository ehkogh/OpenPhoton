using System.Text;
using System.Text.Json;
using OpenPhoton.Realtime.Protocol;
using Photon.SocketServer;

namespace OpenPhoton.Server;

/// <summary>Where a client's credentials came from.</summary>
/// <remarks>
/// Photon's own numbering. Only <see cref="Custom"/> reaches an endpoint of ours; the
/// platform providers would each need their own verification against Steam, Oculus and so on,
/// which a self-hosted preservation server has no way to perform.
/// </remarks>
public static class ClientAuthenticationType
{
    public const byte Custom = 0;
    public const byte Steam = 1;
    public const byte Facebook = 2;
    public const byte Oculus = 3;
    public const byte PlayStation4 = 4;
    public const byte Xbox = 5;
    public const byte Viveport = 10;
    public const byte NintendoSwitch = 11;
    public const byte PlayStation5 = 12;
    public const byte Epic = 13;
    public const byte FacebookGaming = 15;
    public const byte None = 255;
}

/// <summary>What an authentication attempt decided.</summary>
public readonly record struct AuthenticationResult(
    bool Allowed, string? UserId, string? Nickname, string? Message, Dictionary<string, object>? Data)
{
    public static AuthenticationResult Allow(string? userId = null, string? nickname = null,
                                             Dictionary<string, object>? data = null) =>
        new(true, userId, nickname, null, data);

    public static AuthenticationResult Deny(string message) => new(false, null, null, message, null);
}

/// <summary>
/// Checks client credentials against an external service, the way Photon's custom
/// authentication does.
/// </summary>
/// <remarks>
/// <para>
/// The client sends an authentication type, a query string and optionally a body. The server
/// forwards them to a configured URL and reads a small JSON verdict back. The point is that
/// the game server never learns how identity works — it only learns whether this client has
/// one, which is what lets a private server sit in front of whatever account system its
/// operator already runs.
/// </para>
/// <para>
/// Disabled unless a URL is configured. A preservation server with no user directory should
/// let people in, not lock everyone out of a service that has no accounts to check against.
/// </para>
/// </remarks>
public sealed class CustomAuthenticationService(string? url, Action<string>? log = null)
{
    readonly string? _url = string.IsNullOrWhiteSpace(url) ? null : url;
    readonly Action<string> _log = log ?? (_ => { });

    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    public bool IsConfigured => _url is not null;

    /// <summary>
    /// What happens when a client offers no credentials at all.
    /// </summary>
    /// <remarks>
    /// Photon rejects anonymous clients when custom authentication is configured. That is
    /// right for a commercial deployment and wrong for a preservation one, where an old
    /// client build may not know how to authenticate at all — so it is a decision the
    /// operator makes rather than one baked in.
    /// </remarks>
    public bool AllowAnonymous { get; init; } = true;

    public async ValueTask<AuthenticationResult> AuthenticateAsync(OperationRequest request, CancellationToken ct)
    {
        if (_url is null) return AuthenticationResult.Allow();

        var type = Get(request, ParameterCode.ClientAuthenticationType) as byte? ?? ClientAuthenticationType.None;
        var parameters = Get(request, ParameterCode.ClientAuthenticationParams) as string;
        var data = Get(request, ParameterCode.ClientAuthenticationData);

        if (type == ClientAuthenticationType.None && parameters is null && data is null)
        {
            return AllowAnonymous
                ? AuthenticationResult.Allow()
                : AuthenticationResult.Deny("This server requires authentication.");
        }

        if (type != ClientAuthenticationType.Custom)
        {
            return AuthenticationResult.Deny($"Authentication type {type} is not supported by this server.");
        }

        try
        {
            var result = await CallAsync(parameters, data, ct);

            if (result.Allowed && result.UserId is null && IdentityIn(parameters) is { } identity)
                result = result with { UserId = identity };

            return result;
        }
        catch (Exception ex)
        {
            _log($"    custom authentication failed: {ex.GetType().Name}: {ex.Message}");
            return AuthenticationResult.Deny("The authentication service is unavailable.");
        }
    }

    /// <summary>
    /// The user id named in a client's authentication parameters, if any.
    /// </summary>
    /// <remarks>
    /// These arrive as a query string — one client sends
    /// <c>token=…&amp;user=usr_…&amp;hwid=…</c> — and are passed to the authentication
    /// service verbatim. Both spellings are accepted because Photon's own samples use
    /// <c>userId</c> while some clients use <c>user</c>.
    /// </remarks>
    static string? IdentityIn(string? parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters)) return null;

        foreach (var pair in parameters.TrimStart('?', '&').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=');
            if (split <= 0) continue;

            var key = pair[..split];
            if (!key.Equals("user", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("userId", StringComparison.OrdinalIgnoreCase)) continue;

            var value = Uri.UnescapeDataString(pair[(split + 1)..]);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    async ValueTask<AuthenticationResult> CallAsync(string? parameters, object? data, CancellationToken ct)
    {
        var url = string.IsNullOrWhiteSpace(parameters)
            ? _url!
            : _url + (_url!.Contains('?') ? "&" : "?") + parameters.TrimStart('?', '&');

        using var message = data is null
            ? new HttpRequestMessage(HttpMethod.Get, url)
            : new HttpRequestMessage(HttpMethod.Post, url) { Content = BodyOf(data) };

        using var response = await Client.SendAsync(message, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _log($"    custom authentication returned {(int)response.StatusCode}");
            return AuthenticationResult.Deny("Authentication was refused.");
        }

        return Parse(body);
    }

    static HttpContent BodyOf(object data) => data switch
    {
        byte[] bytes => new ByteArrayContent(bytes),
        string text => new StringContent(text, Encoding.UTF8, "application/json"),
        _ => new StringContent(data.ToString() ?? string.Empty, Encoding.UTF8, "text/plain"),
    };

    /// <summary>
    /// Reads Photon's authentication verdict.
    /// </summary>
    /// <remarks>
    /// <c>ResultCode</c> is the field that matters and 1 means success — not 0, which is
    /// failure. Treating it like an exit status inverts the whole check and admits exactly
    /// the clients that were meant to be turned away.
    /// </remarks>
    public static AuthenticationResult Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var resultCode = root.TryGetProperty("ResultCode", out var code) && code.TryGetInt32(out var value)
            ? value
            : 0;

        if (resultCode != 1)
        {
            var message = root.TryGetProperty("Message", out var text) ? text.GetString() : null;
            return AuthenticationResult.Deny(message ?? "Authentication was refused.");
        }

        var userId = root.TryGetProperty("UserId", out var user) ? user.GetString() : null;
        var nickname = root.TryGetProperty("Nickname", out var nick) ? nick.GetString() : null;

        Dictionary<string, object>? data = null;
        if (root.TryGetProperty("Data", out var extra) && extra.ValueKind == JsonValueKind.Object)
        {
            data = [];
            foreach (var property in extra.EnumerateObject()) data[property.Name] = Value(property.Value);
        }

        return AuthenticationResult.Allow(userId, nickname, data);
    }

    static object Value(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,

        JsonValueKind.Number => element.TryGetInt32(out var i) ? (object)i : element.GetDouble(),

        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => element.ToString(),
    };

    static object? Get(OperationRequest request, byte key) =>
        request.Parameters is not null && request.Parameters.TryGetValue(key, out var value) ? value : null;
}
