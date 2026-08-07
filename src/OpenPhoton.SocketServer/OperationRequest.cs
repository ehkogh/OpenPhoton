namespace Photon.SocketServer;

/// <summary>
/// A client-to-server call: one operation code plus a byte-keyed parameter table.
/// </summary>
public sealed class OperationRequest
{
    public OperationRequest() { }

    public OperationRequest(byte operationCode) => OperationCode = operationCode;

    public OperationRequest(byte operationCode, Dictionary<byte, object?> parameters)
    {
        OperationCode = operationCode;
        Parameters = parameters;
    }

    public byte OperationCode { get; set; }

    public Dictionary<byte, object?>? Parameters { get; set; }
}
