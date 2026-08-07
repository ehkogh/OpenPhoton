using Photon.SocketServer.Rpc.Protocols.GpBinaryV16;
using Photon.SocketServer.Rpc.Protocols.GpBinaryV18;

namespace Photon.SocketServer.Rpc.Protocols;

/// <summary>
/// Reads and writes RTS message bodies in whichever serialisation a peer negotiated.
/// </summary>
/// <remarks>
/// Every peer announces its format in the init request and nothing above the transport
/// should have to care which it picked. Both formats are live targets rather than a legacy
/// and a current one: Photon 4.1.x ships both, and which a given build negotiates
/// is its choice, not ours.
/// </remarks>
public interface IMessageProtocol
{
    ProtocolType ProtocolType { get; }

    OperationRequest ReadOperationRequest(ReadOnlySpan<byte> body, CustomTypeRegistry? customTypes = null);

    byte[] WriteOperationResponse(OperationResponse response, CustomTypeRegistry? customTypes = null);

    byte[] WriteEvent(EventData eventData, CustomTypeRegistry? customTypes = null);
}

/// <summary>Selects a protocol implementation for a negotiated <see cref="ProtocolType"/>.</summary>
public static class MessageProtocol
{
    public static IMessageProtocol V16 { get; } = new Protocol16Messages();

    public static IMessageProtocol V18 { get; } = new Protocol18Messages();

    /// <summary>Returns the implementation for a negotiated protocol.</summary>
    /// <exception cref="NotSupportedException">The protocol has no implementation.</exception>
    public static IMessageProtocol For(ProtocolType protocol) => protocol switch
    {
        ProtocolType.GpBinaryV16 => V16,
        ProtocolType.GpBinaryV18 => V18,
        _ => throw new NotSupportedException($"No implementation for {protocol}."),
    };

    public static bool IsSupported(ProtocolType protocol) =>
        protocol is ProtocolType.GpBinaryV16 or ProtocolType.GpBinaryV18;

    sealed class Protocol16Messages : IMessageProtocol
    {
        public ProtocolType ProtocolType => ProtocolType.GpBinaryV16;

        public OperationRequest ReadOperationRequest(ReadOnlySpan<byte> body, CustomTypeRegistry? customTypes = null)
        {
            using var stream = new MemoryStream(body.ToArray(), writable: false);
            return Protocol16.DeserializeOperationRequestBody(stream, customTypes);
        }

        public byte[] WriteOperationResponse(OperationResponse response, CustomTypeRegistry? customTypes = null)
        {
            using var stream = new MemoryStream();
            Protocol16.SerializeOperationResponseBody(stream, response, customTypes);
            return stream.ToArray();
        }

        public byte[] WriteEvent(EventData eventData, CustomTypeRegistry? customTypes = null)
        {
            using var stream = new MemoryStream();
            Protocol16.SerializeEventBody(stream, eventData, customTypes);
            return stream.ToArray();
        }
    }

    sealed class Protocol18Messages : IMessageProtocol
    {
        public ProtocolType ProtocolType => ProtocolType.GpBinaryV18;

        public OperationRequest ReadOperationRequest(ReadOnlySpan<byte> body, CustomTypeRegistry? customTypes = null)
        {
            using var stream = new MemoryStream(body.ToArray(), writable: false);
            return Protocol18.DeserializeOperationRequestBody(stream, customTypes);
        }

        public byte[] WriteOperationResponse(OperationResponse response, CustomTypeRegistry? customTypes = null)
        {
            using var stream = new MemoryStream();
            Protocol18.SerializeOperationResponseBody(stream, response, customTypes);
            return stream.ToArray();
        }

        public byte[] WriteEvent(EventData eventData, CustomTypeRegistry? customTypes = null)
        {
            using var stream = new MemoryStream();
            Protocol18.SerializeEventBody(stream, eventData, customTypes);
            return stream.ToArray();
        }
    }
}
