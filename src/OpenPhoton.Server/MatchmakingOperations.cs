using System.Collections;
using OpenPhoton.Realtime.Protocol;
using OpenPhoton.Server.Rooms;
using Photon.SocketServer;

namespace OpenPhoton.Server;

/// <summary>
/// NameServer and Master operations: everything that happens before a client reaches a room.
/// </summary>
/// <remarks>
/// <para>
/// Neither role hosts rooms. Their entire job is to answer with an address and let the
/// client move on:
/// </para>
/// <code>
/// NameServer  Authenticate       -> Address = master
/// Master      CreateGame/Join    -> Address = game server
/// GameServer  CreateGame/Join    -> actually enters the room
/// </code>
/// <para>
/// The client disconnects between each step and repeats the same operation on the next
/// server, which is why CreateGame and JoinGame are implemented twice with different
/// meanings. On the master they reserve a name; on the game server they admit an actor.
/// </para>
/// </remarks>
public sealed class MatchmakingOperations(
    RoomRegistry rooms,
    ServerEndpoints endpoints,
    AuthTokenIssuer tokens,
    LobbyService lobby,
    Action<string> log,
    OperationalLimits? limits = null,
    SessionSecrets? secrets = null)
{
    readonly RoomRegistry _rooms = rooms;
    readonly ServerEndpoints _endpoints = endpoints;
    readonly AuthTokenIssuer _tokens = tokens;
    readonly SessionSecrets _secrets = secrets ?? new SessionSecrets();
    readonly LobbyService _lobby = lobby;
    readonly Action<string> _log = log;
    readonly OperationalLimits _limits = limits ?? OperationalLimits.Default;


    public void RegisterNameServer(OperationDispatcher dispatcher)
    {
        dispatcher.Register(OperationCode.Authenticate, AuthenticateOnNameServer);
        dispatcher.Register(OperationCode.AuthenticateOnce, AuthenticateOnNameServer);
        dispatcher.Register(OperationCode.GetRegions, GetRegions);
    }

    /// <summary>
    /// Credential check applied before a client is let past the name server or master.
    /// </summary>
    /// <remarks>
    /// The same instance the game server uses. Checking on only one of the three would let a
    /// client skip that server and arrive unauthenticated at the next one.
    /// </remarks>
    public CustomAuthenticationService Authentication { get; set; } = new(null);

    async ValueTask<OperationResponse?> AuthenticateOnNameServer(PeerState peer, OperationRequest request)
    {
        var verdict = await Authentication.AuthenticateAsync(request, CancellationToken.None);
        if (!verdict.Allowed)
        {
            _log($"    refused at name server: {verdict.Message}");
            return await Fail(request, ErrorCode.CustomAuthenticationFailed,
                              verdict.Message ?? "Authentication failed.");
        }

        peer.UserId = verdict.UserId ?? ResolveUserId(request);
        peer.Authenticated = true;

        var next = NextTransport(peer, request);
        var response = new Dictionary<byte, object?>
        {
            [ParameterCode.Address] = _endpoints.MasterAddressFor(next),
            [ParameterCode.UserId] = peer.UserId,
            [ParameterCode.Token] = IssueToken(peer.UserId!),
        };

        if (EncryptionDataFor(peer, request) is { } encryption) response[ParameterCode.EncryptionData] = encryption;
        if (verdict.Nickname is not null) response[ParameterCode.NickName] = verdict.Nickname;
        if (verdict.Data is not null) response[ParameterCode.Data] = verdict.Data;

        _log($"    master for {peer.UserId}: {response[ParameterCode.Address]} ({next})"
           + $"{(response.ContainsKey(ParameterCode.EncryptionData) ? ", payload encryption secret issued" : "")}");

        return await Ok(request, response);
    }

    /// <summary>
    /// The transport the client will use for the master and game server.
    /// </summary>
    /// <remarks>
    /// Not necessarily the one it is on now. Under <c>AuthMode.AuthOnceWss</c> the client
    /// reaches the name server over secure WebSocket and then switches to its configured
    /// protocol — usually UDP — as soon as this response arrives, and it says which in
    /// <c>ExpectedProtocol</c>. Answering with an address for the current transport sends a
    /// UDP client to a WebSocket port.
    /// </remarks>
    static Photon.SocketServer.NetworkProtocolType NextTransport(PeerState peer, OperationRequest request)
    {
        if (request.Parameters is not null
            && request.Parameters.TryGetValue(ParameterCode.ExpectedProtocol, out var expected)
            && expected is byte value)
        {
            return (Photon.SocketServer.NetworkProtocolType)value;
        }

        return peer.Transport;
    }

    /// <summary>Photon's <c>EncryptionMode.PayloadEncryption</c>.</summary>
    const byte PayloadEncryptionMode = 0;

    Dictionary<byte, object?>? EncryptionDataFor(PeerState peer, OperationRequest request)
    {
        var mode = request.Parameters is not null
                   && request.Parameters.TryGetValue(ParameterCode.EncryptionMode, out var requested)
                   && requested is byte b
            ? b
            : PayloadEncryptionMode;

        if (mode != PayloadEncryptionMode)
            _log($"    client asked for encryption mode {mode}; serving payload encryption (0) instead");

        return new Dictionary<byte, object?>
        {
            [0] = PayloadEncryptionMode,
            [1] = _secrets.Issue(peer.UserId!),
        };
    }

    ValueTask<OperationResponse?> GetRegions(PeerState peer, OperationRequest request)
    {
        var regions = _endpoints.Regions.ToArray();

        var transport = peer.Transport is Photon.SocketServer.NetworkProtocolType.WebSocket
                                       or Photon.SocketServer.NetworkProtocolType.SecureWebSocket
            ? Photon.SocketServer.NetworkProtocolType.Udp
            : peer.Transport;
        var addresses = regions.Select(_ => _endpoints.MasterAddressFor(transport)).ToArray();

        return Ok(request, new Dictionary<byte, object?>
        {
            [ParameterCode.Region] = regions,
            [ParameterCode.Address] = addresses,
        });
    }


    public void RegisterMaster(OperationDispatcher dispatcher)
    {
        dispatcher.Register(OperationCode.Authenticate, AuthenticateOnMaster);
        dispatcher.Register(OperationCode.AuthenticateOnce, AuthenticateOnMaster);
        dispatcher.Register(OperationCode.JoinLobby, JoinLobby);
        dispatcher.Register(OperationCode.LeaveLobby, LeaveLobby);
        dispatcher.Register(OperationCode.CreateGame, CreateGameOnMaster);
        dispatcher.Register(OperationCode.JoinGame, JoinGameOnMaster);
        dispatcher.Register(OperationCode.JoinRandomGame, JoinRandomGameOnMaster);
        dispatcher.Register(OperationCode.GetLobbyStats, GetLobbyStats);
        dispatcher.Register(OperationCode.GetGameList, GetGameList);
    }

    async ValueTask<OperationResponse?> AuthenticateOnMaster(PeerState peer, OperationRequest request)
    {
        peer.UserId = ResolveIdentity(request, out var fromToken);

        if (!fromToken)
        {
            var verdict = await Authentication.AuthenticateAsync(request, CancellationToken.None);
            if (!verdict.Allowed)
            {
                _log($"    refused at master: {verdict.Message}");
                return await Fail(request, ErrorCode.CustomAuthenticationFailed,
                                  verdict.Message ?? "Authentication failed.");
            }

            if (verdict.UserId is not null) peer.UserId = verdict.UserId;
        }

        peer.Authenticated = true;
        _log($"    authenticated {peer.UserId} on master ({(fromToken ? "token" : "claimed id")})");

        return await Ok(request, new Dictionary<byte, object?>
        {
            [ParameterCode.UserId] = peer.UserId,
            [ParameterCode.Token] = IssueToken(peer.UserId!),
        });
    }

    ValueTask<OperationResponse?> JoinLobby(PeerState peer, OperationRequest request)
    {
        peer.InLobby = true;
        if (peer.Channel is not null) _lobby.Enter(peer.Channel);

        var channel = peer.Channel;
        if (channel is not null)
            peer.AfterResponse = ct => _lobby.SendFullListAsync(channel, _rooms, ct);

        return Ok(request);
    }

    ValueTask<OperationResponse?> LeaveLobby(PeerState peer, OperationRequest request)
    {
        peer.InLobby = false;
        if (peer.Channel is not null) _lobby.Leave(peer.Channel);
        return Ok(request);
    }

    ValueTask<OperationResponse?> GetGameList(PeerState peer, OperationRequest request)
    {
        var list = new Hashtable();

        var rooms = _limits.GameListLimit > 0
            ? _rooms.Listable().Take(_limits.GameListLimit)
            : _rooms.Listable();

        foreach (var room in rooms) list[room.Name] = LobbyService.Describe(room);

        return Ok(request, new Dictionary<byte, object?> { [ParameterCode.GameList] = list });
    }

    /// <summary>
    /// Rejects a room name that is missing, oversized, or not a name at all.
    /// </summary>
    /// <remarks>
    /// Names are dictionary keys and travel on the wire, so an unbounded one makes every
    /// later lookup more expensive for everybody. Returning null means the name was fine.
    /// </remarks>
    string? NameProblem(string? name) => name is not null && name.Length > _limits.MaxRoomNameLength
        ? $"Room name exceeds {_limits.MaxRoomNameLength} characters."
        : null;

    ValueTask<OperationResponse?> CreateGameOnMaster(PeerState peer, OperationRequest request)
    {
        var name = GetString(request, ParameterCode.RoomName);
        if (NameProblem(name) is { } problem) return Fail(request, ErrorCode.InvalidOperation, problem);

        if (string.IsNullOrEmpty(name)) name = Guid.NewGuid().ToString("N");

        if (!_rooms.TryCreate(name, out var room))
            return Fail(request, ErrorCode.GameIdAlreadyExists, $"Room '{name}' already exists.");

        room.Reserve();
        _log($"    reserved '{name}' for {peer.UserId} -> {_endpoints.GameServerAddressFor(peer.Transport)}");

        return Ok(request, HandoffTo(name, peer));
    }

    ValueTask<OperationResponse?> JoinGameOnMaster(PeerState peer, OperationRequest request)
    {
        var name = GetString(request, ParameterCode.RoomName);
        if (string.IsNullOrEmpty(name))
            return Fail(request, ErrorCode.InvalidOperation, "JoinGame requires a room name.");

        if (NameProblem(name) is { } problem) return Fail(request, ErrorCode.InvalidOperation, problem);

        if (!_rooms.TryGet(name, out _))
        {
            if (JoinModeOf(request) != JoinMode.CreateIfNotExists)
                return Fail(request, ErrorCode.GameDoesNotExist, $"Room '{name}' does not exist.");

            _rooms.TryCreate(name, out var created);
            created.Reserve();
            _log($"    reserved '{name}' (join-or-create) for {peer.UserId} -> {_endpoints.GameServerAddressFor(peer.Transport)}");
        }

        return Ok(request, HandoffTo(name, peer));
    }

    static JoinMode JoinModeOf(OperationRequest request) =>
        request.Parameters is not null
        && request.Parameters.TryGetValue(ParameterCode.JoinMode, out var mode)
        && mode is byte value
            ? (JoinMode)value
            : JoinMode.Default;

    ValueTask<OperationResponse?> JoinRandomGameOnMaster(PeerState peer, OperationRequest request)
    {
        var candidate = _rooms.FindJoinable();
        if (candidate is null)
            return Fail(request, ErrorCode.NoRandomMatchFound, "No joinable room.");

        return Ok(request, HandoffTo(candidate.Name, peer));
    }

    ValueTask<OperationResponse?> GetLobbyStats(PeerState peer, OperationRequest request) =>
        Ok(request, new Dictionary<byte, object?>
        {
            [ParameterCode.LobbyName] = new[] { string.Empty },
            [ParameterCode.PeerCount] = new[] { _rooms.TotalActors },
            [ParameterCode.GameCount] = new[] { _rooms.Count },
        });

    /// <summary>Response telling the client which game server to continue on.</summary>
    /// <remarks>
    /// The address is chosen for the peer's own transport. The client does not reinterpret
    /// what it is given — it dials this exact host and port on the protocol it is already
    /// using — so handing a UDP client the TCP port sends it somewhere that will accept
    /// nothing it says.
    /// </remarks>
    Dictionary<byte, object?> HandoffTo(string roomName, PeerState peer) => new()
    {
        [ParameterCode.Address] = _endpoints.GameServerAddressFor(peer.Transport),
        [ParameterCode.RoomName] = roomName,
        [ParameterCode.Token] = IssueToken(peer.UserId ?? "anonymous"),
    };


    string IssueToken(string userId) => _tokens.Issue(userId);

    /// <summary>
    /// Establishes who a peer is: a valid token wins, otherwise the claimed user id.
    /// </summary>
    /// <remarks>
    /// The token is the whole point of AuthOnce. When the name server has already vouched
    /// for someone, the master and game server take the identity from the signed token
    /// rather than from whatever the client now claims — otherwise a peer could authenticate
    /// as itself and then present someone else's user id at the next hop.
    /// </remarks>
    string ResolveIdentity(OperationRequest request, out bool fromToken)
    {
        var presented = request.Parameters is not null
                        && request.Parameters.TryGetValue(ParameterCode.Token, out var token)
            ? token
            : null;

        if (presented is not null)
        {
            if (_tokens.TryValidate(presented, out var tokenUser, out var error))
            {
                fromToken = true;
                return tokenUser!;
            }

            _log($"    rejected token: {error}");
        }

        fromToken = false;
        return ResolveUserId(request);
    }

    static string ResolveUserId(OperationRequest request)
    {
        var supplied = GetString(request, ParameterCode.UserId);
        return string.IsNullOrWhiteSpace(supplied) ? $"user-{Guid.NewGuid():N}"[..13] : supplied;
    }

    static ValueTask<OperationResponse?> Ok(OperationRequest request, Dictionary<byte, object?>? parameters = null) =>
        ValueTask.FromResult<OperationResponse?>(parameters is null
            ? new OperationResponse(request.OperationCode)
            : new OperationResponse(request.OperationCode, parameters));

    static ValueTask<OperationResponse?> Fail(OperationRequest request, short code, string message) =>
        ValueTask.FromResult<OperationResponse?>(new OperationResponse(request.OperationCode)
        {
            ReturnCode = code,
            DebugMessage = message,
        });

    static string? GetString(OperationRequest request, byte key) =>
        request.Parameters is not null && request.Parameters.TryGetValue(key, out var value) ? value as string : null;
}
