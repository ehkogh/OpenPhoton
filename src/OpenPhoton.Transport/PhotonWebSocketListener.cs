using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Photon.SocketServer;
using Photon.SocketServer.Rpc.Protocols;
using ProtocolType = Photon.SocketServer.Rpc.Protocols.ProtocolType;

namespace OpenPhoton.Transport;

/// <summary>
/// Serves Photon over WebSocket, and over secure WebSocket when given a certificate.
/// </summary>
/// <remarks>
/// <para>
/// Needed because current clients reach the name server over WSS before anything else:
/// under <c>AuthMode.AuthOnceWss</c> the name server connection is always secure WebSocket,
/// whatever protocol the client then uses for master and game. A server without this is
/// unreachable from the first step.
/// </para>
/// <para>
/// What the v5 client does on this transport, from its <c>PhotonClientWebSocket</c>,
/// <c>TPeer</c> and <c>PeerBase</c>:
/// </para>
/// <list type="bullet">
/// <item>Connects to <c>/?libversion=..&amp;sid=..&amp;peerId=..&amp;app=..</c> and offers its
/// serialisation (<c>GpBinaryV18</c>) as the WebSocket subprotocol.</item>
/// <item>Sends one Photon message per binary frame, starting at the <c>0xF3</c> header.
/// There is no <c>0xFB</c> record header: WebSocket already frames.</item>
/// <item>Sends no binary init. With a token it sends a token init wrapped in a <c>0xFB</c>
/// record; without one it sends nothing but a ping, as internal operation 1, and treats the
/// server's answer to that ping as its init response.</item>
/// <item>Skips the key exchange, because the transport is already encrypted.</item>
/// </list>
/// <para>
/// So once the upgrade is done this behaves like the UDP listener: each frame is a whole
/// message, handed to the same handler every other transport uses.
/// </para>
/// </remarks>
public sealed class PhotonWebSocketListener(IPhotonMessageHandler handler, X509Certificate2? certificate, Action<string>? log = null)
{
    readonly IPhotonMessageHandler _handler = handler;
    readonly X509Certificate2? _certificate = certificate;
    readonly Action<string> _log = log ?? (_ => { });
    long _nextConnectionId = 1_000_000;

    const int MaxRequestBytes = 16 * 1024;
    const int MaxMessageBytes = PhotonTcpFraming.MaxRecordLength;
    static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    public NetworkProtocolType Transport =>
        _certificate is null ? NetworkProtocolType.WebSocket : NetworkProtocolType.SecureWebSocket;

    public async Task RunAsync(IEnumerable<int> ports, CancellationToken ct)
    {
        var running = new List<Task>();

        foreach (var port in ports)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            try
            {
                listener.Start();
            }
            catch (SocketException ex)
            {
                _log($"{Scheme} {port}: bind failed ({ex.SocketErrorCode})");
                continue;
            }

            _log($"listening on {Scheme} :{port}");
            running.Add(AcceptLoopAsync(listener, ct));
        }

        if (running.Count == 0) throw new InvalidOperationException($"No {Scheme} ports could be bound.");
        await Task.WhenAll(running);
    }

    string Scheme => _certificate is null ? "ws" : "wss";

    async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        using var registration = ct.Register(listener.Stop);

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                _log($"{Scheme}: accept failed ({ex.SocketErrorCode})");
                continue;
            }

            _ = ServeAsync(client, ct);
        }
    }

    async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;
        var remote = client.Client.RemoteEndPoint!;
        var local = client.Client.LocalEndPoint!;

        WebSocket socket;
        ProtocolType protocol;
        try
        {
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(HandshakeTimeout);
            (socket, protocol) = await AcceptAsync(client.GetStream(), remote, handshakeCts.Token);
        }
        catch (Exception ex)
        {
            _log($"[!] {Scheme} {remote}: handshake failed ({ex.GetType().Name}: {ex.Message})");
            return;
        }

        using var webSocket = socket;
        var sendGate = new SemaphoreSlim(1, 1);
        var id = Interlocked.Increment(ref _nextConnectionId);

        var connection = new RawConnection(
            id, remote, local,
            send: (bytes, token) => SendAsync(webSocket, sendGate, bytes, token),
            close: () => webSocket.Abort(),
            isConnected: () => webSocket.State == WebSocketState.Open)
        {
            Transport = Transport,
            NegotiatedProtocol = protocol,
        };

        Exception? error = null;
        await _handler.OnConnectedAsync(connection, ct);
        try
        {
            await ReceiveLoopAsync(webSocket, connection, ct);
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
        {
            error = ex;
        }
        catch (Exception ex)
        {
            _log($"[!] {connection}: {ex.GetType().Name}: {ex.Message}");
            error = ex;
        }
        finally
        {
            await _handler.OnDisconnectedAsync(connection, error, CancellationToken.None);
        }
    }

    /// <summary>TLS if configured, then the HTTP upgrade.</summary>
    async Task<(WebSocket Socket, ProtocolType Protocol)> AcceptAsync(Stream stream, EndPoint remote, CancellationToken ct)
    {
        if (_certificate is not null)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                ClientCertificateRequired = false,
            }, ct);
            stream = ssl;
        }

        var request = await ReadRequestAsync(stream, ct);
        var lines = request.Split("\r\n");
        var target = lines[0].Split(' ') is [_, var path, ..] ? path : "?";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        if (!headers.TryGetValue("Upgrade", out var upgrade) || !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase)
            || !headers.TryGetValue("Sec-WebSocket-Key", out var key))
        {
            await WriteAsync(stream, "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n", ct);
            throw new InvalidDataException($"not a WebSocket upgrade: '{lines[0]}'");
        }

        var offered = headers.TryGetValue("Sec-WebSocket-Protocol", out var list)
            ? list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : [];
        var chosen = offered.FirstOrDefault(p => p is "GpBinaryV18" or "GpBinaryV16");
        if (chosen is null)
        {
            await WriteAsync(stream, "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n", ct);
            throw new InvalidDataException($"no supported subprotocol in [{string.Join(", ", offered)}]");
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await WriteAsync(stream,
            "HTTP/1.1 101 Switching Protocols\r\n"
            + "Upgrade: websocket\r\n"
            + "Connection: Upgrade\r\n"
            + $"Sec-WebSocket-Accept: {accept}\r\n"
            + $"Sec-WebSocket-Protocol: {chosen}\r\n\r\n", ct);

        _log($"[i] {Scheme} {remote}  GET {target}  subprotocol {chosen}");

        var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
        {
            IsServer = true,
            SubProtocol = chosen,
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });

        return (socket, chosen == "GpBinaryV18" ? ProtocolType.GpBinaryV18 : ProtocolType.GpBinaryV16);
    }

    static async Task<string> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestBytes];
        var length = 0;

        while (true)
        {
            if (length == buffer.Length) throw new InvalidDataException("request headers too large");
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) throw new EndOfStreamException("closed before the request completed");
            length += read;

            var end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end >= 0) return Encoding.ASCII.GetString(buffer, 0, end);
        }
    }

    static async Task WriteAsync(Stream stream, string text, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct);
        await stream.FlushAsync(ct);
    }

    async Task ReceiveLoopAsync(WebSocket socket, RawConnection connection, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                return;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                _log($"[!] {connection}: message over {MaxMessageBytes} bytes, closing");
                return;
            }
            if (!result.EndOfMessage) continue;

            var bytes = message.ToArray();
            message.SetLength(0);

            if (result.MessageType != WebSocketMessageType.Binary)
            {
                _log($"[?] {connection}: ignored {result.MessageType} frame ({bytes.Length} bytes)");
                continue;
            }

            await DispatchAsync(connection, bytes, ct);
        }
    }

    /// <summary>One binary frame: usually a bare message, sometimes a 0xFB record or a raw ping.</summary>
    async ValueTask DispatchAsync(RawConnection connection, ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        if (frame.Length == 0) return;

        if (frame.Span[0] is PhotonTcpFraming.RecordMagic or PhotonTcpFraming.PingMagic)
        {
            var remaining = frame;
            while (remaining.Length > 0)
            {
                var kind = PhotonTcpFraming.TryRead(remaining.Span, out var payload, out var consumed);
                if (kind is FrameKind.Incomplete or FrameKind.Invalid)
                {
                    _log($"[!] {connection}: unreadable record in frame ({kind}), dropped");
                    return;
                }

                var copy = payload.ToArray();
                remaining = remaining[consumed..];

                if (kind == FrameKind.Ping)
                {
                    await _handler.OnPingAsync(connection, PhotonTcpFraming.ReadPingTimestamp(copy), ct);
                    continue;
                }

                await DispatchMessageAsync(connection, copy, ct);
            }
            return;
        }

        await DispatchMessageAsync(connection, frame, ct);
    }

    async ValueTask DispatchMessageAsync(RawConnection connection, ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        if (!PhotonMessages.TryRead(message, out var header, out var body))
        {
            _log($"[!] {connection}: frame with no RTS header (0x{message.Span[0]:X2}), dropped");
            return;
        }

        await _handler.OnMessageAsync(connection, header, body, ct);
    }

    /// <remarks>
    /// A WebSocket allows one send in flight; rooms send to a peer from many threads at once,
    /// so sends are serialised here exactly as <see cref="RawConnection"/> does for TCP.
    /// </remarks>
    static async ValueTask SendAsync(WebSocket socket, SemaphoreSlim gate, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (socket.State != WebSocketState.Open) return;
            await socket.SendAsync(bytes, WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
        finally
        {
            gate.Release();
        }
    }
}
