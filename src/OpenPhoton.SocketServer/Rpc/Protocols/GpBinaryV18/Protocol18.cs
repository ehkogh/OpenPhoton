using System.Buffers.Binary;
using System.Collections;
using System.Text;

namespace Photon.SocketServer.Rpc.Protocols.GpBinaryV18;

/// <summary>
/// GpBinary V18 reader/writer.
/// </summary>
/// <remarks>
/// <para>
/// A different format from V16 rather than a revision of it. The rules, established from
/// captured traffic and cross-checked against the client's published type table:
/// </para>
/// <list type="bullet">
/// <item>integers are zig-zag varints, not fixed-width big-endian</item>
/// <item>floats and doubles are <b>little</b>-endian, the opposite of V16</item>
/// <item>shorts are fixed two-byte little-endian, not varints</item>
/// <item>string lengths are varint byte counts, not 16-bit prefixes</item>
/// <item>arrays are the element tag with <see cref="GpType18.Array"/> (0x40) OR'd in</item>
/// <item>booleans and zeroes are encoded by their tag alone, with no payload</item>
/// <item>null parameters are omitted from a parameter table entirely</item>
/// </list>
/// <para>
/// Reading accepts every width-specialised integer tag the format defines; writing always
/// emits the varint forms. Tags are self-describing, so a peer decodes either without
/// caring, and one canonical encoder is far easier to keep correct than four.
/// </para>
/// </remarks>
public static class Protocol18
{
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Writes a tagged value.</summary>
    public static void Serialize(Stream stream, object? value, CustomTypeRegistry? customTypes = null)
    {
        var registry = customTypes ?? CustomTypeRegistry.Default;

        value = PhotonValue.Normalise(value);

        switch (value)
        {
            case null: stream.WriteByte((byte)GpType18.Null); return;

            case bool b:
                stream.WriteByte((byte)(b ? GpType18.BooleanTrue : GpType18.BooleanFalse));
                return;

            case byte v when v == 0: stream.WriteByte((byte)GpType18.ByteZero); return;
            case byte v: stream.WriteByte((byte)GpType18.Byte); stream.WriteByte(v); return;

            case short v when v == 0: stream.WriteByte((byte)GpType18.ShortZero); return;
            case short v: stream.WriteByte((byte)GpType18.Short); WriteShort(stream, v); return;

            case int v when v == 0: stream.WriteByte((byte)GpType18.IntZero); return;
            case int v: stream.WriteByte((byte)GpType18.CompressedInt); VarInt.WriteSigned(stream, v); return;

            case long v when v == 0: stream.WriteByte((byte)GpType18.LongZero); return;
            case long v: stream.WriteByte((byte)GpType18.CompressedLong); VarInt.WriteSigned(stream, v); return;

            case float v when BitConverter.SingleToInt32Bits(v) == 0:
                stream.WriteByte((byte)GpType18.FloatZero); return;
            case float v: stream.WriteByte((byte)GpType18.Float); WriteFloat(stream, v); return;

            case double v when BitConverter.DoubleToInt64Bits(v) == 0:
                stream.WriteByte((byte)GpType18.DoubleZero); return;
            case double v: stream.WriteByte((byte)GpType18.Double); WriteDouble(stream, v); return;

            case string v: stream.WriteByte((byte)GpType18.String); WriteString(stream, v); return;

            case byte[] v: stream.WriteByte((byte)GpType18.ByteArray); WriteByteArray(stream, v); return;
            case bool[] v: stream.WriteByte((byte)GpType18.BooleanArray); WriteBoolArray(stream, v); return;
            case short[] v: stream.WriteByte((byte)GpType18.ShortArray); WriteArray(stream, v, WriteShort); return;
            case int[] v: stream.WriteByte((byte)GpType18.CompressedIntArray); WriteArray(stream, v, (s, x) => VarInt.WriteSigned(s, x)); return;
            case long[] v: stream.WriteByte((byte)GpType18.CompressedLongArray); WriteArray(stream, v, (s, x) => VarInt.WriteSigned(s, x)); return;
            case float[] v: stream.WriteByte((byte)GpType18.FloatArray); WriteArray(stream, v, WriteFloat); return;
            case double[] v: stream.WriteByte((byte)GpType18.DoubleArray); WriteArray(stream, v, WriteDouble); return;
            case string[] v: stream.WriteByte((byte)GpType18.StringArray); WriteArray(stream, v, WriteString); return;

            case Array v when v.GetType().GetElementType()!.IsArray:
                stream.WriteByte((byte)GpType18.Array);
                VarInt.WriteUnsigned(stream, (ulong)v.Length);
                foreach (var item in v) Serialize(stream, item, registry);
                return;

            case object?[] v:
                stream.WriteByte((byte)GpType18.ObjectArray);
                VarInt.WriteUnsigned(stream, (ulong)v.Length);
                foreach (var item in v) Serialize(stream, item, registry);
                return;

            case Hashtable v: stream.WriteByte((byte)GpType18.Hashtable); WriteHashtable(stream, v, registry); return;

            case EventData v: stream.WriteByte((byte)GpType18.EventData); WriteEventData(stream, v, registry); return;
            case OperationRequest v: stream.WriteByte((byte)GpType18.OperationRequest); WriteOperationRequest(stream, v, registry); return;
            case OperationResponse v: stream.WriteByte((byte)GpType18.OperationResponse); WriteOperationResponse(stream, v, registry); return;

            case UnknownCustomType v:
                stream.WriteByte((byte)GpType18.Custom);
                stream.WriteByte(v.TypeCode);
                VarInt.WriteUnsigned(stream, (ulong)v.Data.Length);
                stream.Write(v.Data, 0, v.Data.Length);
                return;

            case IDictionary v: stream.WriteByte((byte)GpType18.Dictionary); WriteDictionary(stream, v, registry); return;

            default:
                if (registry.TryGetByType(value.GetType(), out var entry) && entry is not null)
                {
                    var data = entry.Serialize(value);
                    stream.WriteByte((byte)GpType18.Custom);
                    stream.WriteByte(entry.Code);
                    VarInt.WriteUnsigned(stream, (ulong)data.Length);
                    stream.Write(data, 0, data.Length);
                    return;
                }

                throw new NotSupportedException($"No GpBinary V18 mapping for {value.GetType()}.");
        }
    }

    static void WriteShort(Stream s, short v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(b, v);
        s.Write(b);
    }

    static void WriteFloat(Stream s, float v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, v);
        s.Write(b);
    }

    static void WriteDouble(Stream s, double v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(b, v);
        s.Write(b);
    }

    static void WriteString(Stream s, string v)
    {
        var count = Utf8.GetByteCount(v);
        VarInt.WriteUnsigned(s, (ulong)count);
        var buffer = new byte[count];
        Utf8.GetBytes(v, buffer);
        s.Write(buffer, 0, count);
    }

    static void WriteByteArray(Stream s, byte[] v)
    {
        VarInt.WriteUnsigned(s, (ulong)v.Length);
        s.Write(v, 0, v.Length);
    }

    static void WriteBoolArray(Stream s, bool[] v)
    {
        VarInt.WriteUnsigned(s, (ulong)v.Length);
        foreach (var item in v) s.WriteByte(item ? (byte)1 : (byte)0);
    }

    static void WriteArray<T>(Stream s, T[] values, Action<Stream, T> writeOne)
    {
        VarInt.WriteUnsigned(s, (ulong)values.Length);
        foreach (var value in values) writeOne(s, value);
    }

    static void WriteHashtable(Stream s, IDictionary v, CustomTypeRegistry ct)
    {
        VarInt.WriteUnsigned(s, (ulong)v.Count);
        foreach (DictionaryEntry entry in v)
        {
            Serialize(s, entry.Key, ct);
            Serialize(s, entry.Value, ct);
        }
    }

    static void WriteDictionary(Stream s, IDictionary v, CustomTypeRegistry ct)
    {
        var (keyType, valueType) = DictionaryTypes(v.GetType());
        s.WriteByte((byte)keyType);
        s.WriteByte((byte)valueType);
        VarInt.WriteUnsigned(s, (ulong)v.Count);

        foreach (DictionaryEntry entry in v)
        {
            if (keyType == GpType18.Unknown) Serialize(s, entry.Key, ct);
            else WriteUntagged(s, entry.Key, keyType, ct);

            if (valueType == GpType18.Unknown) Serialize(s, entry.Value, ct);
            else WriteUntagged(s, entry.Value, valueType, ct);
        }
    }

    /// <summary>Writes a value without its tag, for containers that declared the type once.</summary>
    static void WriteUntagged(Stream s, object? value, GpType18 type, CustomTypeRegistry ct)
    {
        switch (type)
        {
            case GpType18.Boolean: s.WriteByte((bool)value! ? (byte)1 : (byte)0); return;
            case GpType18.Byte: s.WriteByte((byte)value!); return;
            case GpType18.Short: WriteShort(s, (short)value!); return;
            case GpType18.CompressedInt: VarInt.WriteSigned(s, (int)value!); return;
            case GpType18.CompressedLong: VarInt.WriteSigned(s, (long)value!); return;
            case GpType18.Float: WriteFloat(s, (float)value!); return;
            case GpType18.Double: WriteDouble(s, (double)value!); return;
            case GpType18.String: WriteString(s, (string)value!); return;
            default: Serialize(s, value, ct); return;
        }
    }

    static void WriteParameterTable(Stream s, Dictionary<byte, object?>? parameters, CustomTypeRegistry ct)
    {
        if (parameters is null) { s.WriteByte(0); return; }

        var present = parameters.Where(p => p.Value is not null).ToList();
        s.WriteByte((byte)present.Count);

        foreach (var kv in present)
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
        WriteShort(s, v.ReturnCode);
        Serialize(s, v.DebugMessage, ct);
        WriteParameterTable(s, v.Parameters, ct);
    }

    public static void SerializeOperationRequestBody(Stream stream, OperationRequest request, CustomTypeRegistry? ct = null)
        => WriteOperationRequest(stream, request, ct ?? CustomTypeRegistry.Default);

    public static OperationRequest DeserializeOperationRequestBody(Stream stream, CustomTypeRegistry? ct = null)
        => ReadOperationRequest(stream, ct ?? CustomTypeRegistry.Default);

    public static void SerializeOperationResponseBody(Stream stream, OperationResponse response, CustomTypeRegistry? ct = null)
        => WriteOperationResponse(stream, response, ct ?? CustomTypeRegistry.Default);

    public static OperationResponse DeserializeOperationResponseBody(Stream stream, CustomTypeRegistry? ct = null)
        => ReadOperationResponse(stream, ct ?? CustomTypeRegistry.Default);

    public static void SerializeEventBody(Stream stream, EventData eventData, CustomTypeRegistry? ct = null)
        => WriteEventData(stream, eventData, ct ?? CustomTypeRegistry.Default);

    public static EventData DeserializeEventBody(Stream stream, CustomTypeRegistry? ct = null)
        => ReadEventData(stream, ct ?? CustomTypeRegistry.Default);

    public static object? Deserialize(Stream stream, CustomTypeRegistry? customTypes = null)
    {
        var tag = stream.ReadByte();
        if (tag < 0) throw new EndOfStreamException("Expected a GpType18 tag.");
        return ReadValue(stream, (GpType18)tag, customTypes ?? CustomTypeRegistry.Default);
    }

    static object? ReadValue(Stream s, GpType18 type, CustomTypeRegistry ct)
    {
        if ((byte)type >= (byte)GpType18.CustomTypeSlim)
            return ReadCustomBody(s, (byte)((byte)type - (byte)GpType18.CustomTypeSlim), ct);

        return type switch
        {
            GpType18.Null => null,
            GpType18.BooleanTrue => true,
            GpType18.BooleanFalse => false,
            GpType18.Boolean => ReadByteChecked(s) != 0,

            GpType18.ByteZero => (byte)0,
            GpType18.Byte => ReadByteChecked(s),

            GpType18.ShortZero => (short)0,
            GpType18.Short => ReadShort(s),

            GpType18.IntZero => 0,
            GpType18.CompressedInt => VarInt.ReadSignedInt(s),
            GpType18.Int1 => (int)ReadByteChecked(s),
            GpType18.Int1_ => -(int)ReadByteChecked(s),
            GpType18.Int2 => (int)ReadUInt16(s),
            GpType18.Int2_ => -(int)ReadUInt16(s),

            GpType18.LongZero => 0L,
            GpType18.CompressedLong => VarInt.ReadSigned(s),
            GpType18.L1 => (long)ReadByteChecked(s),
            GpType18.L1_ => -(long)ReadByteChecked(s),
            GpType18.L2 => (long)ReadUInt16(s),
            GpType18.L2_ => -(long)ReadUInt16(s),

            GpType18.FloatZero => 0f,
            GpType18.Float => ReadFloat(s),
            GpType18.DoubleZero => 0d,
            GpType18.Double => ReadDouble(s),

            GpType18.String => ReadString(s),

            GpType18.ByteArray => ReadByteArray(s),
            GpType18.BooleanArray => ReadArray(s, _ => ReadByteChecked(s) != 0),
            GpType18.ShortArray => ReadArray(s, _ => ReadShort(s)),
            GpType18.CompressedIntArray => ReadArray(s, _ => VarInt.ReadSignedInt(s)),
            GpType18.CompressedLongArray => ReadArray(s, _ => VarInt.ReadSigned(s)),
            GpType18.FloatArray => ReadArray(s, _ => ReadFloat(s)),
            GpType18.DoubleArray => ReadArray(s, _ => ReadDouble(s)),
            GpType18.StringArray => ReadArray(s, _ => ReadString(s)),
            GpType18.Array => ReadJaggedArray(s, ct),
            GpType18.ObjectArray => ReadArray(s, _ => Deserialize(s, ct)),
            GpType18.HashtableArray => ReadArray(s, _ => ReadHashtable(s, ct)),

            GpType18.Hashtable => ReadHashtable(s, ct),
            GpType18.Dictionary => ReadDictionary(s, ct),

            GpType18.EventData => ReadEventData(s, ct),
            GpType18.OperationRequest => ReadOperationRequest(s, ct),
            GpType18.OperationResponse => ReadOperationResponse(s, ct),

            GpType18.Custom => ReadCustomBody(s, ReadByteChecked(s), ct),

            _ => throw new NotSupportedException($"Unknown GpType18 {(byte)type}."),
        };
    }

    static byte ReadByteChecked(Stream s)
    {
        var b = s.ReadByte();
        if (b < 0) throw new EndOfStreamException();
        return (byte)b;
    }

    static short ReadShort(Stream s)
    {
        Span<byte> b = stackalloc byte[2];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadInt16LittleEndian(b);
    }

    static ushort ReadUInt16(Stream s)
    {
        Span<byte> b = stackalloc byte[2];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadUInt16LittleEndian(b);
    }

    static float ReadFloat(Stream s)
    {
        Span<byte> b = stackalloc byte[4];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadSingleLittleEndian(b);
    }

    static double ReadDouble(Stream s)
    {
        Span<byte> b = stackalloc byte[8];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadDoubleLittleEndian(b);
    }

    static string ReadString(Stream s)
    {
        var length = VarInt.ReadCount(s, "string");
        if (length == 0) return string.Empty;
        var buffer = new byte[length];
        s.ReadExactly(buffer);
        return Utf8.GetString(buffer);
    }

    static byte[] ReadByteArray(Stream s)
    {
        var length = VarInt.ReadCount(s, "byte array");
        var buffer = new byte[length];
        s.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>
    /// Reads an array whose elements are themselves arrays.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tag is <see cref="GpType18.Array"/> with no element type OR'd into it, which is
    /// the format's way of saying the element type is not one of the specialised ones. The
    /// elements are written fully self-describing, so the bytes are shaped exactly like an
    /// <see cref="GpType18.ObjectArray"/>; only the type rebuilt from them differs. The
    /// client calls this case <c>ReadArrayInArray</c>.
    /// </para>
    /// <para>
    /// Producing the real element type is the entire point. One client answers a moderation
    /// request with <c>string[][]</c> — its blocked and muted lists — and hands that to code
    /// that expects a jagged array of strings, not <c>object[]</c>.
    /// </para>
    /// </remarks>
    static Array ReadJaggedArray(Stream s, CustomTypeRegistry ct)
    {
        var length = VarInt.ReadCount(s, "array");
        var items = new object?[length];
        for (var i = 0; i < length; i++) items[i] = Deserialize(s, ct);

        Type? element = null;

        foreach (var item in items)
        {
            if (item is null) continue;

            var type = item.GetType();
            if (element is null) element = type;
            else if (element != type) { element = null; break; }
        }

        var result = Array.CreateInstance(element ?? typeof(object), length);
        for (var i = 0; i < length; i++) result.SetValue(items[i], i);
        return result;
    }

    static T[] ReadArray<T>(Stream s, Func<int, T> readOne)
    {
        var length = VarInt.ReadCount(s, "array");
        var result = new T[length];
        for (var i = 0; i < length; i++) result[i] = readOne(i);
        return result;
    }

    static Hashtable ReadHashtable(Stream s, CustomTypeRegistry ct)
    {
        var count = VarInt.ReadCount(s, "hashtable");
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
        var keyType = (GpType18)ReadByteChecked(s);
        var valueType = (GpType18)ReadByteChecked(s);
        var count = VarInt.ReadCount(s, "dictionary");

        var dictionary = (IDictionary)Activator.CreateInstance(
            typeof(Dictionary<,>).MakeGenericType(ClrTypeOf(keyType), ClrTypeOf(valueType)))!;

        for (var i = 0; i < count; i++)
        {
            var key = keyType == GpType18.Unknown ? Deserialize(s, ct) : ReadValue(s, keyType, ct);
            var value = valueType == GpType18.Unknown ? Deserialize(s, ct) : ReadValue(s, valueType, ct);
            if (key is not null) dictionary[key] = value;
        }
        return dictionary;
    }

    static Dictionary<byte, object?> ReadParameterTable(Stream s, CustomTypeRegistry ct)
    {
        var count = ReadByteChecked(s);
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
        var operationCode = ReadByteChecked(s);
        var returnCode = ReadShort(s);
        var debugMessage = Deserialize(s, ct) as string;
        return new OperationResponse
        {
            OperationCode = operationCode,
            ReturnCode = returnCode,
            DebugMessage = debugMessage,
            Parameters = ReadParameterTable(s, ct),
        };
    }

    static object ReadCustomBody(Stream s, byte code, CustomTypeRegistry ct)
    {
        var length = VarInt.ReadCount(s, "custom type");
        var data = new byte[length];
        s.ReadExactly(data);

        return ct.TryGetByCode(code, out var entry) && entry is not null
            ? entry.Deserialize(data)
            : new UnknownCustomType(code, data);
    }

    static (GpType18 Key, GpType18 Value) DictionaryTypes(Type dictionaryType)
    {
        if (!dictionaryType.IsGenericType || dictionaryType.GetGenericTypeDefinition() != typeof(Dictionary<,>))
            return (GpType18.Unknown, GpType18.Unknown);

        var args = dictionaryType.GetGenericArguments();
        return (DeclaredType(args[0]), DeclaredType(args[1]));
    }

    static GpType18 DeclaredType(Type t)
    {
        if (t == typeof(object)) return GpType18.Unknown;
        if (t == typeof(bool)) return GpType18.Boolean;
        if (t == typeof(byte)) return GpType18.Byte;
        if (t == typeof(short)) return GpType18.Short;
        if (t == typeof(int)) return GpType18.CompressedInt;
        if (t == typeof(long)) return GpType18.CompressedLong;
        if (t == typeof(float)) return GpType18.Float;
        if (t == typeof(double)) return GpType18.Double;
        if (t == typeof(string)) return GpType18.String;
        if (t == typeof(Hashtable)) return GpType18.Hashtable;
        return GpType18.Unknown;
    }

    static Type ClrTypeOf(GpType18 type) => type switch
    {
        GpType18.Boolean => typeof(bool),
        GpType18.Byte => typeof(byte),
        GpType18.Short => typeof(short),
        GpType18.CompressedInt or GpType18.Int1 or GpType18.Int2 => typeof(int),
        GpType18.CompressedLong or GpType18.L1 or GpType18.L2 => typeof(long),
        GpType18.Float => typeof(float),
        GpType18.Double => typeof(double),
        GpType18.String => typeof(string),
        GpType18.Hashtable => typeof(Hashtable),
        _ => typeof(object),
    };
}
