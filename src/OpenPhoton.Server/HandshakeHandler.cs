using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using OpenPhoton.Realtime.Protocol;
using OpenPhoton.Server.Rooms;
using OpenPhoton.Transport;
using Photon.SocketServer;
using Photon.SocketServer.Rpc.Protocols;
using Photon.SocketServer.Rpc.Protocols.GpBinaryV16;
using Photon.SocketServer.Security;

namespace OpenPhoton.Server;

/// <summary>State the server keeps for one connected peer.</summary>
public sealed class PeerState
{
    /// <summary>Serialisation the client asked for during init. Null until init arrives.</summary>
    public ProtocolType? Protocol { get; set; }

    /// <summary>Codec for this peer's negotiated protocol. Defaults to V16 before init.</summary>
    public IMessageProtocol Messages { get; set; } = MessageProtocol.V16;

    public string? ApplicationId { get; set; }

    public bool InitComplete => Protocol.HasValue;

    public bool Authenticated { get; set; }

    /// <summary>Identity the client uses inside a room. Assigned at authentication.</summary>
    public string? UserId { get; set; }

    /// <summary>Which role this connection is being served as, decided by the port it arrived on.</summary>
    public ServerRole Role { get; set; } = ServerRole.Master;

    /// <summary>
    /// Transport this peer connected over.
    /// </summary>
    /// <remarks>
    /// Needed because the handoff hands the client an address it then dials for itself. A
    /// UDP client sent to the TCP game port connects to something that will never understand
    /// it, and the failure surfaces as a client that reaches the game server and then stalls
    /// -- not as anything that looks like a wrong port.
    /// </remarks>
    public Photon.SocketServer.NetworkProtocolType Transport { get; set; }
        = Photon.SocketServer.NetworkProtocolType.Tcp;

    /// <summary>Diffie-Hellman shared secret, once the key exchange has completed.</summary>
    public byte[]? SharedSecret { get; set; }

    /// <summary>Symmetric key derived from <see cref="SharedSecret"/>.</summary>
    public byte[]? EncryptionKey { get; set; }

    public bool EncryptionEstablished => EncryptionKey is not null;

    /// <summary>
    /// Work an operation handler defers until after its response has been sent.
    /// </summary>
    /// <remarks>
    /// Room entry is the case that needs this. The joining client builds its room state from
    /// the operation response and only then can process the Join event; delivering the event
    /// first gives it an event about a room it does not yet have, which it cannot handle.
    /// So the handler admits the actor, returns its response, and defers the broadcast to
    /// here — the response goes out first, the notifications follow.
    /// </remarks>
    public Func<CancellationToken, ValueTask>? AfterResponse { get; set; }

    /// <summary>Cipher for this peer, once the key exchange has completed.</summary>
    public RijndaelCryptoProvider? Crypto { get; set; }

    /// <summary>Retained so a failed decryption can be diagnosed against the real exchange.</summary>
    public DiffieHellmanKeyExchange? Exchange { get; set; }

    public byte[]? PeerPublicKey { get; set; }

    public bool InLobby { get; set; }

    /// <summary>How rooms push events back to this peer.</summary>
    public IPeerChannel? Channel { get; set; }

    public Room? CurrentRoom { get; set; }

    public Actor? CurrentActor { get; set; }
}

/// <summary>
/// Completes the Photon handshake and keeps the connection alive.
/// </summary>
/// <remarks>
/// This is the smallest thing a client will accept as a server: answer init, then answer
/// pings. Everything above it — operations, rooms, events — is meaningless until a client
/// will stay connected, so this is the layer that has to be right first.
/// </remarks>
public sealed class HandshakeHandler : IPhotonMessageHandler
{
    readonly Action<string> _log;
    readonly ConcurrentDictionary<long, PeerState> _peers = new();
    readonly OperationDispatcher _dispatcher = new();

    readonly RoomRegistry _rooms = new();
    readonly RoomOperations _roomOperations;
    readonly MatchmakingOperations _matchmaking;
    readonly ServerEndpoints _endpoints;
    readonly AuthTokenIssuer _tokens;
    readonly SessionSecrets _secrets = new();
    readonly LobbyService _lobby;

    readonly Dictionary<ServerRole, OperationDispatcher> _byRole;

    /// <summary>Plugin loader, if a plugin was configured. Rooms get one instance each.</summary>
    public Plugins.PluginLoader? Plugins { get; private set; }

    /// <summary>
    /// Credential check for arriving clients. Accepts everyone unless a service is configured.
    /// </summary>
    /// <remarks>
    /// Settable so a deployment can point it at its own account system, and shared with the
    /// matchmaking operations so the name server and master apply the same rule the game
    /// server does — an authentication that only one of the three enforces is one a client
    /// can route around by skipping that server.
    /// </remarks>
    public CustomAuthenticationService Authentication
    {
        get => _authentication;
        set
        {
            _authentication = value;
            _matchmaking.Authentication = value;
        }
    }

    CustomAuthenticationService _authentication = new(null);

    /// <summary>Loads a plugin from a Photon-style plugin.config and attaches it to new rooms.</summary>
    public bool LoadPlugin(string configPath, string pluginDirectory)
    {
        var loader = new Plugins.PluginLoader(_log);
        if (!loader.TryLoadFrom(configPath, pluginDirectory)) return false;

        Plugins = loader;
        _rooms.PluginFactory = loader.CreateFor;
        return true;
    }

    /// <summary>Caps this deployment applies, taken from Photon's own configured defaults.</summary>
    public OperationalLimits Limits => _limits;

    readonly OperationalLimits _limits;

    public HandshakeHandler(Action<string> log, ServerEndpoints? endpoints = null, AuthTokenIssuer? tokens = null,
                            OperationalLimits? limits = null)
    {
        _log = log;
        _endpoints = endpoints ?? new ServerEndpoints();

        _tokens = tokens ?? AuthTokenIssuer.CreateEphemeral();

        _lobby = new LobbyService(log);
        _limits = limits ?? OperationalLimits.Default;
        _roomOperations = new RoomOperations(_rooms, _lobby, log, _limits);
        _matchmaking = new MatchmakingOperations(_rooms, _endpoints, _tokens, _lobby, log, _limits, _secrets);

        var nameServer = new OperationDispatcher();
        _matchmaking.RegisterNameServer(nameServer);

        var master = new OperationDispatcher();
        _matchmaking.RegisterMaster(master);

        RegisterOperations();
        _roomOperations.Register(_dispatcher);

        _byRole = new Dictionary<ServerRole, OperationDispatcher>
        {
            [ServerRole.NameServer] = nameServer,
            [ServerRole.Master] = master,
            [ServerRole.GameServer] = _dispatcher,
        };
    }

    /// <summary>
    /// Expires held slots and collects rooms past their grace period, until cancelled.
    /// </summary>
    /// <remarks>
    /// Both of those lapse by the clock rather than by anything a client does, so without
    /// something coming to look they never happen at all. A room whose last player dropped
    /// would hold its name for the lifetime of the process, and the next attempt to create a
    /// room by that name would fail for a reason nobody could see.
    /// <para>
    /// One second is fine-grained enough: PlayerTtl and EmptyRoomTtl are the timings players
    /// notice, and both are measured in seconds at the least.
    /// </para>
    /// </remarks>
    public async Task RunMaintenanceAsync(CancellationToken ct)
    {
        using var tick = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await tick.WaitForNextTickAsync(ct))
            {
                try
                {
                    var (slots, closed) = await _roomOperations.SweepAsync();
                    if (slots > 0 || closed > 0)
                        _log($"[*] swept {slots} lapsed slot(s), closed {closed} room(s)");
                }
                catch (Exception ex)
                {
                    _log($"[*] sweep failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Game server dispatcher. Rooms live here.</summary>
    public OperationDispatcher Dispatcher => _dispatcher;

    public OperationDispatcher DispatcherFor(ServerRole role) => _byRole[role];

    public RoomRegistry Rooms => _rooms;

    public ServerEndpoints Endpoints => _endpoints;

    /// <summary>Server clock. Photon timestamps are milliseconds since the server started.</summary>
    readonly Stopwatch _clock = Stopwatch.StartNew();

    public int ServerTimestamp => (int)_clock.ElapsedMilliseconds;

    public ValueTask OnConnectedAsync(RawConnection connection, CancellationToken ct)
    {
        var role = _endpoints.RoleFor(connection.LocalPort, connection.Transport);

        var transport = connection.Transport;

        var peer = new PeerState { Role = role, Transport = transport };
        peer.Channel = new ConnectionChannel(connection, peer);
        _peers[connection.Id] = peer;

        if (connection.NegotiatedProtocol is { } negotiated && MessageProtocol.IsSupported(negotiated))
        {
            peer.Protocol = negotiated;
            peer.Messages = MessageProtocol.For(negotiated);
        }

        _log($"[+] {connection}  as {role} over {transport}"
           + $"{(connection.NegotiatedProtocol is { } p ? $" ({p})" : "")}");
        return ValueTask.CompletedTask;
    }

    public async ValueTask OnMessageAsync(
        RawConnection connection, RtsMessageHeader header, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (!_peers.TryGetValue(connection.Id, out var peer)) return;

        switch (header.MessageType)
        {
            case RtsMessageType.Init:
                await HandleInitAsync(connection, peer, body, ct);
                break;

            case RtsMessageType.Operation:
                if (header.IsEncrypted)
                {
                    if (!TryDecrypt(connection, peer, body, out var plaintext)) return;
                    body = plaintext;
                }
                await HandleOperationAsync(connection, peer, body, ct);
                break;

            case RtsMessageType.InternalOperationRequest:
                await HandleInternalOperationAsync(connection, peer, body, ct);
                break;

            default:
                _log($"[?] {connection}  {header.MessageType}"
                   + $"{(header.IsEncrypted ? " (encrypted)" : "")}  {body.Length} bytes"
                   + $"{(peer.InitComplete ? "" : "  <-- before init")}");
                _log(HexDump(body.Span));
                break;
        }
    }

    async ValueTask HandleInitAsync(RawConnection connection, PeerState peer, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (TokenInitRequest.IsTokenInit(body.Span))
        {
            await HandleTokenInitAsync(connection, peer, body, ct);
            return;
        }

        if (!InitRequest.TryParse(body.Span, out var request, out var error))
        {
            _log($"[!] {connection}  bad init: {error}");
            connection.Close();
            return;
        }

        if (!MessageProtocol.IsSupported(request!.Protocol))
        {
            _log($"[!] {connection}  unsupported protocol {request.Protocol}, closing");
            connection.Close();
            return;
        }

        peer.Protocol = request.Protocol;
        peer.Messages = MessageProtocol.For(request.Protocol);
        peer.ApplicationId = request.ApplicationId;
        _log($"[i] {connection}  {request}");

        var message = new byte[InitResponse.Size];
        InitResponse.Write(message);

        await connection.SendMessageAsync(message, ct);
        _log($"[<] {connection}  init response");
    }

    /// <summary>
    /// An init that carries a token: the client's whole authentication on this server.
    /// </summary>
    /// <remarks>
    /// Under AuthOnce the client does not send Authenticate to the master or game server; the
    /// token in this init is all it presents. So this is where the peer becomes who the name
    /// server said it was, and where payload encryption starts if the name server handed out
    /// a secret — the client switches it on as soon as it is connected, with no exchange.
    /// </remarks>
    async ValueTask HandleTokenInitAsync(RawConnection connection, PeerState peer, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (!TokenInitRequest.TryParse(body.Span, out var request, out var error))
        {
            _log($"[!] {connection}  bad token init: {error}");
            connection.Close();
            return;
        }

        if (!MessageProtocol.IsSupported(request!.Protocol))
        {
            _log($"[!] {connection}  unsupported protocol {request.Protocol}, closing");
            connection.Close();
            return;
        }

        peer.Protocol = request.Protocol;
        peer.Messages = MessageProtocol.For(request.Protocol);
        peer.ApplicationId = request.ApplicationId;
        _log($"[i] {connection}  {request}");

        var message = new byte[InitResponse.Size];
        InitResponse.Write(message);
        await connection.SendMessageAsync(message, ct);
        _log($"[<] {connection}  init response");

        if (request.Token is null || peer.Role == ServerRole.NameServer) return;

        var authenticate = new OperationRequest((byte)OperationCode.Authenticate, new Dictionary<byte, object?>
        {
            [ParameterCode.Token] = request.Token,
        });
        var response = await _byRole[peer.Role].DispatchAsync(peer, authenticate);
        await SendMessageAsync(connection, RtsMessageType.OperationResponse,
            OperationDispatcher.SerialiseResponse(response, peer.Messages), ct);
        _log($"[<] {connection}  authenticate (from token init) -> rc {response.ReturnCode}"
           + $"{(response.DebugMessage is null ? "" : $" \"{response.DebugMessage}\"")}");

        if (peer.Authenticated && peer.UserId is { } userId && _secrets.TryGet(userId, out var secret))
        {
            peer.EncryptionKey = secret;
            peer.Crypto = new RijndaelCryptoProvider(secret);
            _log($"    payload encryption on for {userId}");
        }
    }

    async ValueTask HandleOperationAsync(RawConnection connection, PeerState peer, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (!peer.InitComplete)
        {
            _log($"[!] {connection}  operation before init, closing");
            connection.Close();
            return;
        }

        OperationRequest request;
        try
        {
            request = OperationDispatcher.ParseRequest(body, peer.Messages);
        }
        catch (Exception ex)
        {
            var head = body[..Math.Min(body.Length, 192)];

            _log($"[!] {connection}  undecodable operation ({ex.GetType().Name}: {ex.Message}), closing");
            _log($"    {body.Length} byte(s), first {head.Length}: {Convert.ToHexString(head.Span)}");

            connection.Close();
            return;
        }

        var parameters = request.Parameters is null ? "" : $"  params={{{string.Join(", ", request.Parameters.Keys)}}}";
        _log($"[>] {connection}  [{peer.Role}] op {request.OperationCode}{Detail(request)}{parameters}");

        var response = await _byRole[peer.Role].DispatchAsync(peer, request);
        await SendMessageAsync(connection, RtsMessageType.OperationResponse,
            OperationDispatcher.SerialiseResponse(response, peer.Messages), ct);

        _log($"[<] {connection}  op {response.OperationCode} -> rc {response.ReturnCode}"
           + $"{(response.DebugMessage is null ? "" : $" \"{response.DebugMessage}\"")}");

        if (peer.AfterResponse is { } deferred)
        {
            peer.AfterResponse = null;
            await deferred(ct);
        }
    }

    static string Detail(OperationRequest request)
    {
        if (request.OperationCode != (byte)OperationCode.RaiseEvent) return "";
        if (request.Parameters is null) return "";
        if (!request.Parameters.TryGetValue((byte)ParameterCode.Code, out var code)) return "";

        return $" (ev {code})";
    }

    /// <summary>
    /// Decrypts a message body, closing the peer if it cannot be read.
    /// </summary>
    /// <remarks>
    /// A message we cannot decrypt is not recoverable: either the key exchange produced
    /// different secrets on the two sides, or the stream is corrupt. Continuing would mean
    /// acting on plaintext we invented, so the connection ends instead.
    /// </remarks>
    bool TryDecrypt(RawConnection connection, PeerState peer, ReadOnlyMemory<byte> body, out ReadOnlyMemory<byte> plaintext)
    {
        plaintext = default;

        if (peer.Crypto is null)
        {
            _log($"[!] {connection}  encrypted message before key exchange, closing");
            connection.Close();
            return false;
        }

        if (!peer.Crypto.TryDecrypt(body.Span, out var decrypted))
        {
            _log($"[!] {connection}  could not decrypt {body.Length} bytes");
            ReportKeyDerivationCandidates(peer, body.Span);
            connection.Close();
            return false;
        }

        plaintext = decrypted;
        return true;
    }

    /// <summary>
    /// On a decryption failure, reports which key derivation would have worked.
    /// </summary>
    /// <remarks>
    /// Bring-up diagnostic. A mismatched Diffie-Hellman convention is invisible at exchange
    /// time and only shows up here, so trying the alternatives against real ciphertext turns
    /// a guessing game into one observation.
    /// </remarks>
    void ReportKeyDerivationCandidates(PeerState peer, ReadOnlySpan<byte> ciphertext)
    {
        if (peer.Exchange is null || peer.PeerPublicKey is null) return;

        var matches = EncryptionProbe.Search(peer.Exchange, peer.PeerPublicKey, ciphertext);
        if (matches.Count == 0)
        {
            _log("    no key/padding/IV combination decrypts this message");
            return;
        }

        foreach (var (candidate, plaintext) in matches)
        {
            _log($"    MATCH '{candidate.Description}' -> op={plaintext[0]} "
               + $"bytes={Convert.ToHexString(plaintext.AsSpan(0, Math.Min(16, plaintext.Length)))}");
        }
    }

    /// <summary>Internal operation code for Diffie-Hellman key agreement.</summary>
    const byte ExchangeKeysForEncryption = 0;

    /// <summary>Parameter carrying a public key in either direction.</summary>
    const byte KeyExchangeParameter = 1;

    /// <summary>
    /// Handles the key exchange that must complete before a client will authenticate.
    /// </summary>
    /// <remarks>
    /// The client runs this automatically on every transport except secure WebSocket, and
    /// its own state machine only calls Authenticate from the EncryptionEstablished
    /// callback. Until this succeeds a client connects, pings, and does nothing else.
    /// </remarks>
    async ValueTask HandleInternalOperationAsync(
        RawConnection connection, PeerState peer, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        OperationRequest request;
        try
        {
            request = OperationDispatcher.ParseRequest(body, peer.Messages);
        }
        catch (Exception ex)
        {
            _log($"[!] {connection}  undecodable internal operation ({ex.GetType().Name})");
            return;
        }

        if (request.OperationCode == PingOperation)
        {
            await AnswerPingOperationAsync(connection, peer, request, ct);
            return;
        }

        if (request.OperationCode != ExchangeKeysForEncryption)
        {
            _log($"[?] {connection}  internal op {request.OperationCode} not implemented");
            return;
        }

        if (request.Parameters is null
            || !request.Parameters.TryGetValue(KeyExchangeParameter, out var raw)
            || raw is not byte[] clientPublicKey)
        {
            _log($"[!] {connection}  key exchange without a public key");
            return;
        }

        var exchange = new DiffieHellmanKeyExchange();
        peer.Exchange = exchange;
        peer.PeerPublicKey = clientPublicKey;
        try
        {
            peer.SharedSecret = exchange.DeriveSharedSecret(clientPublicKey);
        }
        catch (ArgumentException ex)
        {
            _log($"[!] {connection}  rejected peer key: {ex.Message}");
            connection.Close();
            return;
        }

        peer.EncryptionKey = DiffieHellmanKeyExchange.DeriveEncryptionKey(peer.SharedSecret);
        peer.Crypto = new RijndaelCryptoProvider(peer.EncryptionKey);

        var response = new OperationResponse(ExchangeKeysForEncryption, new Dictionary<byte, object?>
        {
            [KeyExchangeParameter] = exchange.PublicKey,
        });

        await SendMessageAsync(connection, RtsMessageType.InternalOperationResponse,
            OperationDispatcher.SerialiseResponse(response, peer.Messages), ct);

        _log($"[<] {connection}  key exchange complete "
           + $"(secret {peer.SharedSecret.Length}B, key {peer.EncryptionKey.Length}B)");
    }

    /// <summary>Internal operation code for a ping on message-framed transports.</summary>
    const byte PingOperation = 1;

    /// <summary>Parameter carrying the client's timestamp, echoed back.</summary>
    const byte PingClientTime = 1;

    /// <summary>Parameter carrying the server's timestamp.</summary>
    const byte PingServerTime = 2;

    /// <summary>
    /// Answers a ping sent as internal operation 1, which is how WebSocket clients ping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The raw <c>0xF0</c> ping only exists on TCP's byte stream; on WebSocket the client
    /// sends an internal operation instead (<c>TPeer.SendPing</c> when not framing) and reads
    /// the reply's parameter 2 as server time and parameter 1 as its own time echoed
    /// (<c>TPeer.ReadPingResult</c>), both as <c>int</c>.
    /// </para>
    /// <para>
    /// The first reply is also the client's init response. A WebSocket client with no token
    /// sends no init at all; when this reply arrives while it is still connecting it sets
    /// <c>PingUsedAsInit</c> and declares itself connected. Without it the client sits in
    /// Connecting until it times out, never having sent a single operation.
    /// </para>
    /// </remarks>
    async ValueTask AnswerPingOperationAsync(RawConnection connection, PeerState peer, OperationRequest request, CancellationToken ct)
    {
        var clientTime = request.Parameters is not null && request.Parameters.TryGetValue(PingClientTime, out var sent)
            ? sent
            : 0;

        var response = new OperationResponse(PingOperation, new Dictionary<byte, object?>
        {
            [PingClientTime] = clientTime is int ? clientTime : Convert.ToInt32(clientTime),
            [PingServerTime] = ServerTimestamp,
        });

        await SendMessageAsync(connection, RtsMessageType.InternalOperationResponse,
            OperationDispatcher.SerialiseResponse(response, peer.Messages), ct);
    }

    static string HexDump(ReadOnlySpan<byte> data, int maxBytes = 320)
    {
        var sb = new System.Text.StringBuilder();
        var n = Math.Min(data.Length, maxBytes);

        for (var row = 0; row < n; row += 16)
        {
            sb.Append($"      {row:X4}  ");
            for (var i = 0; i < 16; i++)
                sb.Append(row + i < n ? $"{data[row + i]:X2} " : "   ");

            sb.Append(' ');
            for (var i = 0; i < 16 && row + i < n; i++)
            {
                var c = data[row + i];
                sb.Append(c >= 0x20 && c < 0x7F ? (char)c : '.');
            }
            if (row + 16 < n) sb.AppendLine();
        }

        if (data.Length > n) sb.Append($"\n      ... {data.Length - n} more bytes");
        return sb.ToString();
    }

    /// <summary>Wraps a message body in an RTS header and sends it however the transport frames.</summary>
    static ValueTask SendMessageAsync(RawConnection connection, RtsMessageType type, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var message = new byte[RtsFraming.GpBinaryHeaderSize + body.Length];
        RtsFraming.WriteHeader(message, type, encrypted: false);
        body.Span.CopyTo(message.AsSpan(RtsFraming.GpBinaryHeaderSize));

        return connection.SendMessageAsync(message, ct);
    }

    public async ValueTask OnPingAsync(RawConnection connection, uint clientTimestamp, CancellationToken ct)
    {
        var reply = new byte[PhotonTcpFraming.ServerPingSize];
        reply[0] = PhotonTcpFraming.PingMagic;
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(1, 4), (uint)ServerTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(5, 4), clientTimestamp);

        await connection.SendAsync(reply, ct);
        _log($"[~] {connection}  ping {clientTimestamp} -> pong");
    }

    public async ValueTask OnDisconnectedAsync(RawConnection connection, Exception? error, CancellationToken ct)
    {
        if (_peers.TryRemove(connection.Id, out var peer))
        {
            await _roomOperations.RemoveAsync(peer, becomeInactive: true,
                reason: error is null
                    ? Photon.Hive.Plugin.LeaveReason.ClientDisconnect
                    : Photon.Hive.Plugin.LeaveReason.ClientTimeoutDisconnect);

            if (peer.Channel is not null) _lobby.Leave(peer.Channel);
        }

        _log($"[-] {connection}  {(error is null ? "closed" : error.GetType().Name)}");
    }

    /// <summary>
    /// Sends room events to a peer over its raw connection, in that peer's protocol.
    /// </summary>
    /// <remarks>
    /// The protocol is read from the peer at send time rather than captured at construction,
    /// because the channel is created when the connection is accepted and the protocol is
    /// only known once init arrives.
    /// </remarks>
    sealed class ConnectionChannel(RawConnection connection, PeerState peer) : IPeerChannel
    {
        readonly RawConnection _connection = connection;
        readonly PeerState _peer = peer;

        public long Id => _connection.Id;

        public ValueTask SendEventAsync(EventData eventData, CancellationToken ct) =>
            SendMessageAsync(_connection, RtsMessageType.Event, _peer.Messages.WriteEvent(eventData), ct);

        public void Close() => _connection.Close();
    }


    void RegisterOperations()
    {
        _dispatcher.Register(OperationCode.Authenticate, Authenticate);
        _dispatcher.Register(OperationCode.AuthenticateOnce, Authenticate);
    }

    /// <summary>
    /// Establishes who this peer is, consulting a custom authentication service if configured.
    /// </summary>
    /// <remarks>
    /// With no service configured this accepts everyone: a self-hosted preservation server
    /// has no user directory to check against, so the meaningful work is returning the shape
    /// the client expects rather than deciding who may connect. The UserId is echoed when
    /// supplied and invented otherwise, because the client uses it to identify itself in a
    /// room.
    /// </remarks>
    async ValueTask<OperationResponse?> Authenticate(PeerState peer, OperationRequest request)
    {
        if (request.Parameters is not null
            && request.Parameters.TryGetValue(ParameterCode.Token, out var token)
            && _tokens.TryValidate(token, out var tokenUser, out _))
        {
            peer.UserId = tokenUser!;
            peer.Authenticated = true;

            return new OperationResponse(request.OperationCode, new Dictionary<byte, object?>
            {
                [ParameterCode.UserId] = peer.UserId,
            });
        }

        var verdict = await Authentication.AuthenticateAsync(request, CancellationToken.None);

        if (!verdict.Allowed)
        {
            _log($"[!] {peer.UserId ?? "peer"} refused: {verdict.Message}");
            return new OperationResponse(request.OperationCode)
            {
                ReturnCode = ErrorCode.CustomAuthenticationFailed,
                DebugMessage = verdict.Message,
            };
        }

        var userId = verdict.UserId
                     ?? (request.Parameters is not null
                         && request.Parameters.TryGetValue(ParameterCode.UserId, out var supplied)
                         && supplied is string s && !string.IsNullOrWhiteSpace(s)
                             ? s
                             : $"user-{Guid.NewGuid():N}"[..13]);

        peer.UserId = userId;
        peer.Authenticated = true;

        var response = new Dictionary<byte, object?>
        {
            [ParameterCode.UserId] = userId,
            [ParameterCode.Token] = _tokens.Issue(userId),
        };

        if (verdict.Nickname is not null) response[ParameterCode.NickName] = verdict.Nickname;
        if (verdict.Data is not null) response[ParameterCode.Data] = verdict.Data;

        return new OperationResponse(request.OperationCode, response);
    }
}
