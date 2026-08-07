namespace Photon.SocketServer.Rpc.Protocols.GpBinaryV18;

/// <summary>
/// Variable-length integer encoding used throughout GpBinary V18.
/// </summary>
/// <remarks>
/// <para>
/// Seven bits per byte, low group first, with the top bit set on every byte except the last
/// — the usual LEB128 arrangement. Lengths and counts use it directly; signed values are
/// zig-zagged first.
/// </para>
/// <para>
/// Zig-zag matters because a plain varint encodes by magnitude, and two's-complement -1 has
/// every bit set, so it would take ten bytes. Interleaving the sign into the low bit
/// (0, -1, 1, -2 → 0, 1, 2, 3) keeps small negatives small, which is the common case for
/// deltas and coordinates.
/// </para>
/// </remarks>
public static class VarInt
{
    /// <summary>Largest number of bytes a 64-bit value can occupy.</summary>
    public const int MaxLength64 = 10;

    public static void WriteUnsigned(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    public static ulong ReadUnsigned(Stream stream)
    {
        ulong result = 0;
        var shift = 0;

        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0) throw new EndOfStreamException("Truncated varint.");

            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;

            shift += 7;

            if (shift >= MaxLength64 * 7) throw new InvalidDataException("Varint is too long to be a 64-bit value.");
        }
    }

    public static void WriteSigned(Stream stream, long value) => WriteUnsigned(stream, ZigZagEncode(value));

    public static long ReadSigned(Stream stream) => ZigZagDecode(ReadUnsigned(stream));

    public static void WriteSigned(Stream stream, int value) => WriteUnsigned(stream, ZigZagEncode(value));

    public static int ReadSignedInt(Stream stream)
    {
        var value = ZigZagDecode(ReadUnsigned(stream));
        if (value is < int.MinValue or > int.MaxValue)
            throw new InvalidDataException($"Value {value} does not fit in a 32-bit integer.");
        return (int)value;
    }

    /// <summary>Maps signed values onto unsigned so that small magnitudes stay short.</summary>
    public static ulong ZigZagEncode(long value) => (ulong)((value << 1) ^ (value >> 63));

    public static ulong ZigZagEncode(int value) => (ulong)(uint)((value << 1) ^ (value >> 31));

    public static long ZigZagDecode(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    /// <summary>Reads a length or count, rejecting values that cannot be real.</summary>
    public static int ReadCount(Stream stream, string what, int limit = 16 * 1024 * 1024)
    {
        var value = ReadUnsigned(stream);
        if (value > (ulong)limit)
            throw new InvalidDataException($"Declared {what} of {value} exceeds the {limit} limit.");
        return (int)value;
    }
}
