namespace Photon.SocketServer.Rpc.Protocols;

/// <summary>
/// Serialisation formats a peer can negotiate.
/// </summary>
/// <remarks>
/// These are the server-side numbers, taken from Photon's own <c>ProtocolType</c>. Two
/// other numberings exist for the same concepts and none of them agree:
/// <list type="bullet">
/// <item>the client library uses <c>SerializationProtocol { GpBinaryV16 = 0, GpBinaryV18 = 1 }</c></item>
/// <item>the init request encodes V16 as <c>0x06</c> and V18 as <c>0x08</c></item>
/// </list>
/// Conversion between them must go through an explicit mapping — see
/// <see cref="InitRequest"/>. Casting will compile and be wrong.
/// </remarks>
public enum ProtocolType
{
    GpBinaryV16 = 1,
    Json = 3,
    GpBinaryV17 = 4,
    GpBinaryV18 = 5,
}
