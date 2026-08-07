namespace Photon.SocketServer.Rpc.Protocols;

/// <summary>
/// A custom-typed value whose type code has no registered handler.
/// </summary>
/// <remarks>
/// Preserving the raw payload rather than throwing means an unregistered custom type
/// survives a decode/encode round trip byte-for-byte. That matters for a relay: the
/// server routinely forwards payloads it has no reason to understand, and refusing to
/// parse one client's Vector3 would break relaying between clients that both understand it.
/// </remarks>
public sealed class UnknownCustomType(byte typeCode, byte[] data)
{
    public byte TypeCode { get; } = typeCode;

    public byte[] Data { get; } = data;
}

/// <summary>
/// Maps custom type codes to CLR types and their byte-array encodings.
/// </summary>
public sealed class CustomTypeRegistry
{
    public sealed record Entry(byte Code, Type Type, Func<object, byte[]> Serialize, Func<byte[], object> Deserialize);

    readonly Dictionary<byte, Entry> _byCode = [];
    readonly Dictionary<Type, Entry> _byType = [];

    /// <summary>Registry used when a peer has no protocol context of its own.</summary>
    public static CustomTypeRegistry Default { get; } = new();

    public void Register(byte code, Type type, Func<object, byte[]> serialize, Func<byte[], object> deserialize)
    {
        var entry = new Entry(code, type, serialize, deserialize);
        _byCode[code] = entry;
        _byType[type] = entry;
    }

    public bool TryGetByCode(byte code, out Entry? entry) => _byCode.TryGetValue(code, out entry);

    public bool TryGetByType(Type type, out Entry? entry) => _byType.TryGetValue(type, out entry);
}
