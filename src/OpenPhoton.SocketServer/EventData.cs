namespace Photon.SocketServer;

/// <summary>
/// A server-to-client push: one event code plus a byte-keyed parameter table.
/// </summary>
public sealed class EventData
{
    public EventData() { }

    public EventData(byte eventCode) => Code = eventCode;

    public EventData(byte eventCode, Dictionary<byte, object?> parameters)
    {
        Code = eventCode;
        Parameters = parameters;
    }

    public byte Code { get; set; }

    public Dictionary<byte, object?>? Parameters { get; set; }
}
