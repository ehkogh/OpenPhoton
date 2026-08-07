using System.Buffers.Binary;
using System.Collections;
using System.Text;

namespace Photon.SocketServer.Rpc.Protocols.GpBinaryV16;

/// <summary>
/// GpBinary V16 reader/writer — the default Photon serialisation format.
/// </summary>
/// <remarks>
/// <para>
/// Every multi-byte integer is big-endian. Values are written as a one-byte
/// <see cref="GpType"/> tag followed by a payload; some containers hoist the tag out of
/// their elements and declare it once in the header, which is the main source of
/// asymmetry in the format.
/// </para>
/// <para>
/// Length prefixes are inconsistent by design and must be matched exactly: byte arrays
/// and int arrays use a 32-bit count, while strings, object arrays, hashtables and
/// dictionaries use a 16-bit one.
/// </para>
/// </remarks>
public static class Protocol16
{
    /// <summary>Strings carry a 16-bit byte count, so this is a hard format limit.</summary>
    public const int MaxStringByteLength = short.MaxValue;

    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Writes a tagged value: one <see cref="GpType"/> byte followed by its payload.</summary>
    public static void Serialize(Stream stream, object? value, CustomTypeRegistry? customTypes = null)
    {
        value = PhotonValue.Normalise(value);

        var type = GetTypeCode(value, customTypes ?? CustomTypeRegistry.Default);
        stream.WriteByte((byte)type);
        WritePayload(stream, value, type, customTypes ?? CustomTypeRegistry.Default);
    }

    /// <summary>Writes a value's payload only; the caller has already written the tag.</summary>
    static void WritePayload(Stream s, object? value, GpType type, CustomTypeRegistry ct)
    {
        switch (type)
        {
            case GpType.Null:
                break;
            case GpType.Boolean:
                s.WriteByte((bool)value! ? (byte)1 : (byte)0);
                break;
            case GpType.Byte:
                s.WriteByte((byte)value!);
                break;
            case GpType.Short:
                WriteInt16(s, (short)value!);
                break;
            case GpType.Integer:
                WriteInt32(s, (int)value!);
                break;
            case GpType.Long:
                WriteInt64(s, (long)value!);
                break;
            case GpType.Float:
                WriteFloat(s, (float)value!);
                break;
            case GpType.Double:
                WriteDouble(s, (double)value!);
                break;
            case GpType.String:
                WriteString(s, (string)value!);
                break;
            case GpType.ByteArray:
                WriteByteArray(s, (byte[])value!);
                break;
            case GpType.IntegerArray:
                WriteIntArray(s, (int[])value!);
                break;
            case GpType.StringArray:
                WriteStringArray(s, (string[])value!);
                break;
            case GpType.ObjectArray:
                WriteObjectArray(s, (object?[])value!, ct);
                break;
            case GpType.Array:
                WriteTypedArray(s, (Array)value!, ct);
                break;
            case GpType.Hashtable:
                WriteHashtable(s, (IDictionary)value!, ct);
                break;
            case GpType.Dictionary:
                WriteDictionary(s, (IDictionary)value!, ct);
                break;
            case GpType.EventData:
                WriteEventData(s, (EventData)value!, ct);
                break;
            case GpType.OperationRequest:
                WriteOperationRequest(s, (OperationRequest)value!, ct);
                break;
            case GpType.OperationResponse:
                WriteOperationResponse(s, (OperationResponse)value!, ct);
                break;
            case GpType.Custom:
                WriteCustom(s, value!, ct);
                break;
            default:
                throw new NotSupportedException($"Cannot serialise GpType {type}.");
        }
    }

    static void WriteInt16(Stream s, short v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(b, v);
        s.Write(b);
    }

    static void WriteInt32(Stream s, int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, v);
        s.Write(b);
    }

    static void WriteInt64(Stream s, long v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, v);
        s.Write(b);
    }

    static void WriteFloat(Stream s, float v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(b, v);
        s.Write(b);
    }

    static void WriteDouble(Stream s, double v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(b, v);
        s.Write(b);
    }

    static void WriteString(Stream s, string v)
    {
        var count = Utf8.GetByteCount(v);
        if (count > MaxStringByteLength)
            throw new ArgumentException($"String is {count} UTF-8 bytes; GpBinary V16 allows at most {MaxStringByteLength}.", nameof(v));

        WriteInt16(s, (short)count);
        var buf = new byte[count];
        Utf8.GetBytes(v, buf);
        s.Write(buf, 0, count);
    }

    static void WriteByteArray(Stream s, byte[] v)
    {
        WriteInt32(s, v.Length);
        s.Write(v, 0, v.Length);
    }

    static void WriteIntArray(Stream s, int[] v)
    {
        WriteInt32(s, v.Length);
        foreach (var i in v) WriteInt32(s, i);
    }

    static void WriteStringArray(Stream s, string[] v)
    {
        WriteInt16(s, checked((short)v.Length));
        foreach (var str in v) WriteString(s, str);
    }

    static void WriteObjectArray(Stream s, object?[] v, CustomTypeRegistry ct)
    {
        WriteInt16(s, checked((short)v.Length));
        foreach (var o in v) Serialize(s, o, ct);
    }

    /// <summary>Array whose element type is declared once in the header.</summary>
    static void WriteTypedArray(Stream s, Array v, CustomTypeRegistry ct)
    {
        var elementType = GetElementTypeCode(v.GetType().GetElementType()!, ct);
        WriteInt16(s, checked((short)v.Length));
        s.WriteByte((byte)elementType);
        foreach (var item in v) WritePayload(s, item, elementType, ct);
    }

    static void WriteHashtable(Stream s, IDictionary v, CustomTypeRegistry ct)
    {
        WriteInt16(s, checked((short)v.Count));
        foreach (DictionaryEntry e in v)
        {
            Serialize(s, e.Key, ct);
            Serialize(s, e.Value, ct);
        }
    }

    /// <summary>
    /// Dictionary with declared key and value types. A declared type of
    /// <see cref="GpType.Unknown"/> means that side is heterogeneous and each element
    /// carries its own tag instead.
    /// </summary>
    static void WriteDictionary(Stream s, IDictionary v, CustomTypeRegistry ct)
    {
        var (keyType, valueType) = GetDictionaryTypeCodes(v.GetType(), ct);

        s.WriteByte((byte)keyType);
        s.WriteByte((byte)valueType);
        WriteInt16(s, checked((short)v.Count));

        foreach (DictionaryEntry e in v)
        {
            if (keyType == GpType.Unknown) Serialize(s, e.Key, ct);
            else WritePayload(s, e.Key, keyType, ct);

            if (valueType == GpType.Unknown) Serialize(s, e.Value, ct);
            else WritePayload(s, e.Value, valueType, ct);
        }
    }

    static void WriteParameterTable(Stream s, Dictionary<byte, object?>? parameters, CustomTypeRegistry ct)
    {
        if (parameters is null)
        {
            WriteInt16(s, 0);
            return;
        }

        WriteInt16(s, checked((short)parameters.Count));
        foreach (var kv in parameters)
        {
            s.WriteByte(kv.Key);
            Serialize(s, kv.Value, ct);
        }
    }

    static void WriteEventData(Stream s, EventData v, CustomTypeRegistry ct)
    {
        s.WriteByte(v.Code);
        WriteParameterTable(s, v.Parameters, ct);
    }

    static void WriteOperationRequest(Stream s, OperationRequest v, CustomTypeRegistry ct)
    {
        s.WriteByte(v.OperationCode);
        WriteParameterTable(s, v.Parameters, ct);
    }

    static void WriteOperationResponse(Stream s, OperationResponse v, CustomTypeRegistry ct)
    {
        s.WriteByte(v.OperationCode);
        WriteInt16(s, v.ReturnCode);
        Serialize(s, v.DebugMessage, ct);
        WriteParameterTable(s, v.Parameters, ct);
    }

    static void WriteCustom(Stream s, object value, CustomTypeRegistry ct)
    {
        if (value is UnknownCustomType raw)
        {
            s.WriteByte(raw.TypeCode);
            WriteInt16(s, checked((short)raw.Data.Length));
            s.Write(raw.Data, 0, raw.Data.Length);
            return;
        }

        if (!ct.TryGetByType(value.GetType(), out var entry) || entry is null)
            throw new NotSupportedException($"No custom type registered for {value.GetType()}.");

        var data = entry.Serialize(value);
        s.WriteByte(entry.Code);
        WriteInt16(s, checked((short)data.Length));
        s.Write(data, 0, data.Length);
    }

    /// <summary>Writes an operation request body for an <see cref="RtsMessageType.Operation"/> message.</summary>
    public static void SerializeOperationRequestBody(Stream stream, OperationRequest request, CustomTypeRegistry? customTypes = null)
        => WriteOperationRequest(stream, request, customTypes ?? CustomTypeRegistry.Default);

    /// <summary>Reads an operation request body.</summary>
    public static OperationRequest DeserializeOperationRequestBody(Stream stream, CustomTypeRegistry? customTypes = null)
        => ReadOperationRequest(stream, customTypes ?? CustomTypeRegistry.Default);

    /// <summary>Writes an operation response body for an <see cref="RtsMessageType.OperationResponse"/> message.</summary>
    public static void SerializeOperationResponseBody(Stream stream, OperationResponse response, CustomTypeRegistry? customTypes = null)
        => WriteOperationResponse(stream, response, customTypes ?? CustomTypeRegistry.Default);

    /// <summary>Reads an operation response body.</summary>
    public static OperationResponse DeserializeOperationResponseBody(Stream stream, CustomTypeRegistry? customTypes = null)
        => ReadOperationResponse(stream, customTypes ?? CustomTypeRegistry.Default);

    /// <summary>Writes an event body for an <see cref="RtsMessageType.Event"/> message.</summary>
    public static void SerializeEventBody(Stream stream, EventData eventData, CustomTypeRegistry? customTypes = null)
        => WriteEventData(stream, eventData, customTypes ?? CustomTypeRegistry.Default);

    /// <summary>Reads an event body.</summary>
    public static EventData DeserializeEventBody(Stream stream, CustomTypeRegistry? customTypes = null)
        => ReadEventData(stream, customTypes ?? CustomTypeRegistry.Default);

    /// <summary>Reads a tagged value written by <see cref="Serialize"/>.</summary>
    public static object? Deserialize(Stream stream, CustomTypeRegistry? customTypes = null)
    {
        var tag = stream.ReadByte();
        if (tag < 0) throw new EndOfStreamException("Expected a GpType tag.");
        return ReadPayload(stream, (GpType)tag, customTypes ?? CustomTypeRegistry.Default);
    }

    static object? ReadPayload(Stream s, GpType type, CustomTypeRegistry ct) => type switch
    {
        GpType.Null => null,
        GpType.Boolean => ReadByteChecked(s) != 0,
        GpType.Byte => ReadByteChecked(s),
        GpType.Short => ReadInt16(s),
        GpType.Integer => ReadInt32(s),
        GpType.Long => ReadInt64(s),
        GpType.Float => ReadFloat(s),
        GpType.Double => ReadDouble(s),
        GpType.String => ReadString(s),
        GpType.ByteArray => ReadByteArray(s),
        GpType.IntegerArray => ReadIntArray(s),
        GpType.StringArray => ReadStringArray(s),
        GpType.ObjectArray => ReadObjectArray(s, ct),
        GpType.Array => ReadTypedArray(s, ct),
        GpType.Hashtable => ReadHashtable(s, ct),
        GpType.Dictionary => ReadDictionary(s, ct),
        GpType.EventData => ReadEventData(s, ct),
        GpType.OperationRequest => ReadOperationRequest(s, ct),
        GpType.OperationResponse => ReadOperationResponse(s, ct),
        GpType.Custom => ReadCustom(s, ct),
        _ => throw new NotSupportedException($"Unknown GpType {(byte)type}."),
    };

    static byte ReadByteChecked(Stream s)
    {
        var b = s.ReadByte();
        if (b < 0) throw new EndOfStreamException();
        return (byte)b;
    }

    static short ReadInt16(Stream s)
    {
        Span<byte> b = stackalloc byte[2];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadInt16BigEndian(b);
    }

    static int ReadInt32(Stream s)
    {
        Span<byte> b = stackalloc byte[4];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadInt32BigEndian(b);
    }

    static long ReadInt64(Stream s)
    {
        Span<byte> b = stackalloc byte[8];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadInt64BigEndian(b);
    }

    static float ReadFloat(Stream s)
    {
        Span<byte> b = stackalloc byte[4];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadSingleBigEndian(b);
    }

    static double ReadDouble(Stream s)
    {
        Span<byte> b = stackalloc byte[8];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadDoubleBigEndian(b);
    }

    /// <summary>Validates a length prefix before it is used to size an allocation.</summary>
    static int CheckLength(int length, string what)
    {
        if (length < 0) throw new InvalidDataException($"Negative {what} length {length}.");
        return length;
    }

    static string ReadString(Stream s)
    {
        var len = CheckLength(ReadInt16(s), "string");
        if (len == 0) return string.Empty;
        var buf = new byte[len];
        s.ReadExactly(buf);
        return Utf8.GetString(buf);
    }

    static byte[] ReadByteArray(Stream s)
    {
        var len = CheckLength(ReadInt32(s), "byte array");
        var buf = new byte[len];
        s.ReadExactly(buf);
        return buf;
    }

    static int[] ReadIntArray(Stream s)
    {
        var len = CheckLength(ReadInt32(s), "int array");
        var arr = new int[len];
        for (var i = 0; i < len; i++) arr[i] = ReadInt32(s);
        return arr;
    }

    static string[] ReadStringArray(Stream s)
    {
        var len = CheckLength(ReadInt16(s), "string array");
        var arr = new string[len];
        for (var i = 0; i < len; i++) arr[i] = ReadString(s);
        return arr;
    }

    static object?[] ReadObjectArray(Stream s, CustomTypeRegistry ct)
    {
        var len = CheckLength(ReadInt16(s), "object array");
        var arr = new object?[len];
        for (var i = 0; i < len; i++) arr[i] = Deserialize(s, ct);
        return arr;
    }

    static Array ReadTypedArray(Stream s, CustomTypeRegistry ct)
    {
        var len = CheckLength(ReadInt16(s), "array");
        var elementType = (GpType)ReadByteChecked(s);
        var clrType = GetClrType(elementType, ct);

        var arr = Array.CreateInstance(clrType, len);
        for (var i = 0; i < len; i++) arr.SetValue(ReadPayload(s, elementType, ct), i);
        return arr;
    }

    static Hashtable ReadHashtable(Stream s, CustomTypeRegistry ct)
    {
        var count = CheckLength(ReadInt16(s), "hashtable");
        var table = new Hashtable(count);
        for (var i = 0; i < count; i++)
        {
            var key = Deserialize(s, ct);
            var value = Deserialize(s, ct);
            if (key is not null) table[key] = value;
        }
        return table;
    }

    static IDictionary ReadDictionary(Stream s, CustomTypeRegistry ct)
    {
        var keyType = (GpType)ReadByteChecked(s);
        var valueType = (GpType)ReadByteChecked(s);
        var count = CheckLength(ReadInt16(s), "dictionary");

        var clrKey = GetClrType(keyType, ct);
        var clrValue = GetClrType(valueType, ct);

        var dict = (IDictionary)Activator.CreateInstance(
            typeof(Dictionary<,>).MakeGenericType(clrKey, clrValue))!;

        for (var i = 0; i < count; i++)
        {
            var key = keyType == GpType.Unknown ? Deserialize(s, ct) : ReadPayload(s, keyType, ct);
            var value = valueType == GpType.Unknown ? Deserialize(s, ct) : ReadPayload(s, valueType, ct);
            if (key is not null) dict[key] = value;
        }
        return dict;
    }

    static Dictionary<byte, object?> ReadParameterTable(Stream s, CustomTypeRegistry ct)
    {
        var count = CheckLength(ReadInt16(s), "parameter table");
        var table = new Dictionary<byte, object?>(count);
        for (var i = 0; i < count; i++)
        {
            var key = ReadByteChecked(s);
            table[key] = Deserialize(s, ct);
        }
        return table;
    }

    static EventData ReadEventData(Stream s, CustomTypeRegistry ct) => new()
    {
        Code = ReadByteChecked(s),
        Parameters = ReadParameterTable(s, ct),
    };

    static OperationRequest ReadOperationRequest(Stream s, CustomTypeRegistry ct) => new()
    {
        OperationCode = ReadByteChecked(s),
        Parameters = ReadParameterTable(s, ct),
    };

    static OperationResponse ReadOperationResponse(Stream s, CustomTypeRegistry ct)
    {
        var opCode = ReadByteChecked(s);
        var returnCode = ReadInt16(s);
        var debugMessage = Deserialize(s, ct) as string;
        return new OperationResponse
        {
            OperationCode = opCode,
            ReturnCode = returnCode,
            DebugMessage = debugMessage,
            Parameters = ReadParameterTable(s, ct),
        };
    }

    static object ReadCustom(Stream s, CustomTypeRegistry ct)
    {
        var code = ReadByteChecked(s);
        var len = CheckLength(ReadInt16(s), "custom type");
        var data = new byte[len];
        s.ReadExactly(data);

        return ct.TryGetByCode(code, out var entry) && entry is not null
            ? entry.Deserialize(data)
            : new UnknownCustomType(code, data);
    }

    static GpType GetTypeCode(object? value, CustomTypeRegistry ct) => value switch
    {
        null => GpType.Null,
        bool => GpType.Boolean,
        byte => GpType.Byte,
        short => GpType.Short,
        int => GpType.Integer,
        long => GpType.Long,
        float => GpType.Float,
        double => GpType.Double,
        string => GpType.String,
        byte[] => GpType.ByteArray,
        int[] => GpType.IntegerArray,
        string[] => GpType.StringArray,
        object[] => GpType.ObjectArray,
        Hashtable => GpType.Hashtable,
        EventData => GpType.EventData,
        OperationRequest => GpType.OperationRequest,
        OperationResponse => GpType.OperationResponse,
        UnknownCustomType => GpType.Custom,
        Array => GpType.Array,
        IDictionary => GpType.Dictionary,
        _ => ct.TryGetByType(value.GetType(), out _)
            ? GpType.Custom
            : throw new NotSupportedException($"No GpBinary V16 mapping for {value.GetType()}."),
    };

    /// <summary>Type code for the declared element type of a typed array.</summary>
    static GpType GetElementTypeCode(Type elementType, CustomTypeRegistry ct)
    {
        if (elementType == typeof(bool)) return GpType.Boolean;
        if (elementType == typeof(byte)) return GpType.Byte;
        if (elementType == typeof(short)) return GpType.Short;
        if (elementType == typeof(int)) return GpType.Integer;
        if (elementType == typeof(long)) return GpType.Long;
        if (elementType == typeof(float)) return GpType.Float;
        if (elementType == typeof(double)) return GpType.Double;
        if (elementType == typeof(string)) return GpType.String;
        if (elementType == typeof(byte[])) return GpType.ByteArray;
        if (elementType == typeof(int[])) return GpType.IntegerArray;
        if (elementType == typeof(string[])) return GpType.StringArray;
        if (elementType == typeof(Hashtable)) return GpType.Hashtable;
        if (elementType == typeof(object)) return GpType.ObjectArray;
        if (ct.TryGetByType(elementType, out _)) return GpType.Custom;
        if (elementType.IsArray) return GpType.Array;

        throw new NotSupportedException($"No GpBinary V16 array element mapping for {elementType}.");
    }

    static (GpType Key, GpType Value) GetDictionaryTypeCodes(Type dictType, CustomTypeRegistry ct)
    {
        if (!dictType.IsGenericType || dictType.GetGenericTypeDefinition() != typeof(Dictionary<,>))
            return (GpType.Unknown, GpType.Unknown);

        var args = dictType.GetGenericArguments();
        return (DeclaredCode(args[0]), DeclaredCode(args[1]));

        GpType DeclaredCode(Type t) => t == typeof(object) ? GpType.Unknown : GetElementTypeCode(t, ct);
    }

    static Type GetClrType(GpType type, CustomTypeRegistry ct) => type switch
    {
        GpType.Unknown => typeof(object),
        GpType.Null => typeof(object),
        GpType.Boolean => typeof(bool),
        GpType.Byte => typeof(byte),
        GpType.Short => typeof(short),
        GpType.Integer => typeof(int),
        GpType.Long => typeof(long),
        GpType.Float => typeof(float),
        GpType.Double => typeof(double),
        GpType.String => typeof(string),
        GpType.ByteArray => typeof(byte[]),
        GpType.IntegerArray => typeof(int[]),
        GpType.StringArray => typeof(string[]),
        GpType.ObjectArray => typeof(object[]),
        GpType.Hashtable => typeof(Hashtable),
        GpType.Dictionary => typeof(IDictionary),
        GpType.EventData => typeof(EventData),
        GpType.OperationRequest => typeof(OperationRequest),
        GpType.OperationResponse => typeof(OperationResponse),
        GpType.Array => typeof(Array),
        GpType.Custom => typeof(object),
        _ => throw new NotSupportedException($"No CLR type for GpType {(byte)type}."),
    };
}
