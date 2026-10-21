namespace OpenPhoton.Server;

/// <summary>
/// The three roles a Photon deployment presents, distinguished by listening port.
/// </summary>
/// <remarks>
/// A client visits all three in turn. This is not an implementation detail we could skip:
/// the client's own state machine refuses to send matchmaking operations to a game server
/// or room operations to a master, so a server that answers everything on one port cannot
/// drive a real <c>RealtimeClient</c> through a session.
/// </remarks>
public enum ServerRole
{
    /// <summary>Directs clients to a regional master. Optional when clients are pointed straight at a master.</summary>
    NameServer,

    /// <summary>Authentication, lobby and matchmaking. Hands out game server addresses; hosts no rooms.</summary>
    Master,

    /// <summary>Hosts the rooms. Everything in-room happens here.</summary>
    GameServer,
}

/// <summary>
/// Ports this deployment listens on, and the addresses it hands out.
/// </summary>
/// <remarks>
/// The advertised host matters: the client takes whatever string we return and connects to
/// it, so an address that resolves on the server but not on the client leaves the client
/// stranded mid-handoff with no useful error. It defaults to loopback for local bring-up
/// and must be set to something the client can actually reach for anything else.
/// </remarks>
public sealed class ServerEndpoints
{
    public string PublicHost { get; init; } = "127.0.0.1";

    public int NameServerPort { get; init; } = Realtime.Protocol.DefaultPorts.Tcp.NameServer;

    public int MasterPort { get; init; } = Realtime.Protocol.DefaultPorts.Tcp.MasterServer;

    public int GameServerPort { get; init; } = Realtime.Protocol.DefaultPorts.Tcp.GameServer;

    public IReadOnlyList<string> Regions { get; init; } = ["us", "usw", "eu", "jp"];

    /// <summary>Region reported when a single name is needed.</summary>
    public string Region => Regions.Count > 0 ? Regions[0] : "local";

    /// <summary>Master ports per transport, mirroring the name server's own configuration.</summary>
    /// <remarks>
    /// Photon's <c>Nameserver.xml.config</c> lists a master port per protocol
    /// (<c>MasterServerPortUdp</c> 5055, <c>...Tcp</c> 4530, <c>...WebSocket</c> 9090,
    /// <c>...SecureWebSocket</c> 19090). The name server must answer with the port matching
    /// the transport the client is actually using, not a single fixed one, or the client
    /// walks straight into a port that does not speak its protocol.
    /// </remarks>
    public int MasterPortUdp { get; init; } = Realtime.Protocol.DefaultPorts.UdpLegacy.MasterServer;

    /// <summary>Name server over secure WebSocket: the client's built-in default.</summary>
    public int NameServerPortSecureWebSocket { get; init; } = 443;

    public int MasterPortWebSocket { get; init; } = 9090;

    public int MasterPortSecureWebSocket { get; init; } = 19090;

    public string MasterAddress => $"{PublicHost}:{MasterPort}";

    public string GameServerAddress => $"{PublicHost}:{GameServerPort}";

    /// <summary>Master address for the transport a peer connected with.</summary>
    public string MasterAddressFor(Photon.SocketServer.NetworkProtocolType protocol) => protocol switch
    {
        Photon.SocketServer.NetworkProtocolType.Udp => $"{PublicHost}:{MasterPortUdp}",
        Photon.SocketServer.NetworkProtocolType.WebSocket => $"{PublicHost}:{MasterPortWebSocket}",
        Photon.SocketServer.NetworkProtocolType.SecureWebSocket => $"{PublicHost}:{MasterPortSecureWebSocket}",
        _ => MasterAddress,
    };

    public IEnumerable<int> Ports => [NameServerPort, MasterPort, GameServerPort];

    public int NameServerPortUdp { get; init; } = Realtime.Protocol.DefaultPorts.UdpLegacy.NameServer;

    public int GameServerPortUdp { get; init; } = Realtime.Protocol.DefaultPorts.UdpLegacy.GameServer;

    /// <summary>
    /// Every UDP port served, across both port generations.
    /// </summary>
    /// <remarks>
    /// Photon moved its UDP ports from 5055–5058 to 27000–27002 in 2022, and a client's
    /// <c>ProtocolPorts</c> table <em>overrides</em> whatever port the server hands out —
    /// so a modern client told to use 5056 goes to 27002 anyway, and a period client told
    /// 27002 goes to 5056. The handoff port is advisory on UDP, which means the only way to
    /// serve both is to be listening on both. They are the same server either way.
    /// </remarks>
    public IEnumerable<int> UdpPorts =>
    [
        NameServerPortUdp, MasterPortUdp, GameServerPortUdp,
        Realtime.Protocol.DefaultPorts.Udp.NameServer,
        Realtime.Protocol.DefaultPorts.Udp.MasterServer,
        Realtime.Protocol.DefaultPorts.Udp.GameServer,
    ];

    public string GameServerAddressUdp => $"{PublicHost}:{GameServerPortUdp}";

    /// <summary>Game server address for the transport a peer connected with.</summary>
    /// <remarks>
    /// The handoff hands the client an address it then dials itself, so it has to name a
    /// port speaking the client's own protocol. Sending a UDP client to the TCP game port
    /// produces a connection that is accepted and then silently understood by nobody.
    /// </remarks>
    public string GameServerAddressFor(Photon.SocketServer.NetworkProtocolType protocol) => protocol switch
    {
        Photon.SocketServer.NetworkProtocolType.Udp => GameServerAddressUdp,
        _ => GameServerAddress,
    };

    /// <summary>Maps an accepted connection's local port to the role it should be served as.</summary>
    /// <remarks>
    /// Both UDP port generations map to the same roles, because both are bound — see
    /// <see cref="UdpPorts"/> for why.
    /// </remarks>
    public ServerRole RoleFor(int localPort, Photon.SocketServer.NetworkProtocolType transport)
    {
        if (transport is Photon.SocketServer.NetworkProtocolType.WebSocket
                      or Photon.SocketServer.NetworkProtocolType.SecureWebSocket)
        {
            return ServerRole.NameServer;
        }

        return RoleFor(localPort);
    }

    public ServerRole RoleFor(int localPort)
    {
        if (localPort == NameServerPort
            || localPort == NameServerPortUdp
            || localPort == Realtime.Protocol.DefaultPorts.Udp.NameServer)
        {
            return ServerRole.NameServer;
        }

        if (localPort == GameServerPort
            || localPort == GameServerPortUdp
            || localPort == Realtime.Protocol.DefaultPorts.Udp.GameServer)
        {
            return ServerRole.GameServer;
        }

        return ServerRole.Master;
    }
}
