using System.Text;
using Photon.SocketServer.Rpc.Protocols.GpBinaryV16;
using Photon.SocketServer.Rpc.Protocols.GpBinaryV18;

namespace Photon.SocketServer.Rpc.Protocols;

/// <summary>
/// The init a client sends when it already holds a token: an HTTP-shaped request rather than
/// the fixed binary layout of <see cref="InitRequest"/>.
/// </summary>
/// <remarks>
/// <para>
/// Sent instead of the binary init whenever the client has a token to present, which under
/// <c>AuthMode.AuthOnce</c> / <c>AuthOnceWss</c> is every connection after the name server.
/// Taken from the v5 client's <c>PeerBase.WriteInitRequest</c>:
/// </para>
/// <code>
/// POST /?init&amp;app=&lt;appId&gt;&amp;clientversion=5.x&amp;protocol=GpBinaryV18&amp;sid=&lt;n&gt; HTTP/1.1\r\n
/// Host: &lt;server address&gt;\r\n
/// Content-Length: &lt;n&gt;\r\n
/// \r\n
/// &lt;token, serialised as a single value in the named protocol&gt;
/// </code>
/// <para>
/// It carries no RTS header: over UDP it is the whole reliable payload, over TCP and
/// WebSocket it is the payload of a <c>0xFB</c> record. The token replaces the Authenticate
/// operation entirely — the client does not authenticate again on these servers.
/// </para>
/// </remarks>
public sealed class TokenInitRequest
{
    static ReadOnlySpan<byte> Prefix => "POST /?"u8;

    public ProtocolType Protocol { get; init; }

    public string? ApplicationId { get; init; }

    public string? ClientVersion { get; init; }

    /// <summary>The token as the client sent it; whatever the name server issued, round-tripped.</summary>
    public object? Token { get; init; }

    public static bool IsTokenInit(ReadOnlySpan<byte> payload) => payload.StartsWith(Prefix);

    public static bool TryParse(ReadOnlySpan<byte> payload, out TokenInitRequest? request, out string? error)
    {
        request = null;
        error = null;

        if (!IsTokenInit(payload))
        {
            error = "Not a token init.";
            return false;
        }

        var headerEnd = payload.IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0)
        {
            error = "Token init has no end of headers.";
            return false;
        }

        var lines = Encoding.ASCII.GetString(payload[..headerEnd]).Split("\r\n");
        var requestLine = lines[0];
        var queryStart = requestLine.IndexOf('?');
        var queryEnd = requestLine.IndexOf(' ', queryStart);
        if (queryStart < 0 || queryEnd < 0)
        {
            error = $"Malformed request line '{requestLine}'.";
            return false;
        }

        var query = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in requestLine[(queryStart + 1)..queryEnd].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals < 0) query[Uri.UnescapeDataString(pair)] = null;
            else query[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..]);
        }

        ProtocolType protocol;
        switch (query.GetValueOrDefault("protocol"))
        {
            case "GpBinaryV16": protocol = ProtocolType.GpBinaryV16; break;
            case "GpBinaryV18": protocol = ProtocolType.GpBinaryV18; break;
            case var other:
                error = $"Unsupported protocol '{other}'.";
                return false;
        }

        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line[(colon + 1)..].Trim(), out contentLength);
        }

        var body = payload[(headerEnd + 4)..];
        if (contentLength > body.Length)
        {
            error = $"Token init body is {body.Length} bytes; Content-Length says {contentLength}.";
            return false;
        }

        object? token = null;
        if (contentLength > 0)
        {
            using var stream = new MemoryStream(body[..contentLength].ToArray());
            try
            {
                token = protocol == ProtocolType.GpBinaryV18 ? Protocol18.Deserialize(stream) : Protocol16.Deserialize(stream);
            }
            catch (Exception ex)
            {
                error = $"Token could not be deserialised ({ex.GetType().Name}: {ex.Message}).";
                return false;
            }
        }

        request = new TokenInitRequest
        {
            Protocol = protocol,
            ApplicationId = query.GetValueOrDefault("app"),
            ClientVersion = query.GetValueOrDefault("clientversion"),
            Token = token,
        };
        return true;
    }

    public override string ToString() =>
        $"TokenInit(app='{ApplicationId}', protocol={Protocol}, clientversion={ClientVersion}, token={(Token is null ? "none" : "present")})";
}
