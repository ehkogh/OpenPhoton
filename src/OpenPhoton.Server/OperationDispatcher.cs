using OpenPhoton.Realtime.Protocol;
using Photon.SocketServer;
using Photon.SocketServer.Rpc.Protocols;

namespace OpenPhoton.Server;

/// <summary>
/// Handles one operation code.
/// </summary>
/// <remarks>
/// Asynchronous because most room operations have to push events to <em>other</em> peers
/// before replying — a join is not complete until everyone already present has been told.
/// Firing those sends without awaiting them would let a reply overtake the notifications
/// it implies.
/// </remarks>
public delegate ValueTask<OperationResponse?> OperationHandler(PeerState peer, OperationRequest request);

/// <summary>
/// Routes operation requests to handlers and produces responses.
/// </summary>
/// <remarks>
/// An unhandled operation gets an explicit failure rather than silence. Photon clients wait
/// on a response for most operations, so dropping one leaves the client hanging until it
/// times out — a failure that looks like a network problem instead of a missing feature.
/// </remarks>
public sealed class OperationDispatcher
{
    readonly Dictionary<byte, OperationHandler> _handlers = [];

    public void Register(byte operationCode, OperationHandler handler) => _handlers[operationCode] = handler;

    public bool IsRegistered(byte operationCode) => _handlers.ContainsKey(operationCode);

    public async ValueTask<OperationResponse> DispatchAsync(PeerState peer, OperationRequest request)
    {
        if (!_handlers.TryGetValue(request.OperationCode, out var handler))
        {
            return new OperationResponse(request.OperationCode)
            {
                ReturnCode = ErrorCode.InvalidOperation,
                DebugMessage = $"Operation {request.OperationCode} is not implemented.",
            };
        }

        try
        {
            return await handler(peer, request) ?? new OperationResponse(request.OperationCode);
        }
        catch (Exception ex)
        {
            return new OperationResponse(request.OperationCode)
            {
                ReturnCode = ErrorCode.InternalServerError,
                DebugMessage = ex.Message,
            };
        }
    }

    /// <summary>Reads an operation request from an RTS message body in the peer's protocol.</summary>
    public static OperationRequest ParseRequest(ReadOnlyMemory<byte> body, IMessageProtocol? protocol = null) =>
        (protocol ?? MessageProtocol.V16).ReadOperationRequest(body.Span);

    /// <summary>Serialises a response as an RTS message body in the peer's protocol.</summary>
    public static byte[] SerialiseResponse(OperationResponse response, IMessageProtocol? protocol = null) =>
        (protocol ?? MessageProtocol.V16).WriteOperationResponse(response);
}
