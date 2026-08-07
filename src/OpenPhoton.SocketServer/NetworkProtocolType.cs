namespace Photon.SocketServer;

/// <summary>
/// Transport a peer is connected with.
/// </summary>
/// <remarks>
/// Values match Photon's server-side <c>NetworkProtocolType</c>, and happen to agree with
/// the client's <c>ConnectionProtocol</c> — which is worth noting only because the delivery
/// and serialisation enums on those two sides do <em>not</em> agree. See ARCHITECTURE.md.
/// </remarks>
public enum NetworkProtocolType
{
    Unknown = -1,
    Udp = 0,
    Tcp = 1,
    ReliableHttp = 3,
    WebSocket = 4,
    SecureWebSocket = 5,
    RawHttp = 6,
    WebRTC = 7,
}
