namespace Photon.SocketServer;

/// <summary>
/// The server's reply to an <see cref="OperationRequest"/>.
/// </summary>
/// <remarks>
/// <see cref="ReturnCode"/> 0 means success; non-zero values are operation specific.
/// <see cref="DebugMessage"/> is serialised inline between the return code and the
/// parameter table, and is null on the wire far more often than not.
/// </remarks>
public sealed class OperationResponse
{
    public OperationResponse() { }

    public OperationResponse(byte operationCode) => OperationCode = operationCode;

    public OperationResponse(byte operationCode, Dictionary<byte, object?> parameters)
    {
        OperationCode = operationCode;
        Parameters = parameters;
    }

    public byte OperationCode { get; set; }

    public short ReturnCode { get; set; }

    public string? DebugMessage { get; set; }

    public Dictionary<byte, object?>? Parameters { get; set; }
}
