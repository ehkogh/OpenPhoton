namespace Photon.SocketServer.Rpc.Protocols.GpBinaryV18;

/// <summary>
/// Type codes for GpBinary V18.
/// </summary>
/// <remarks>
/// <para>
/// Read from the <c>Protocol18.GpType</c> enum published by the Photon client library, and
/// identical between the 5.1.17 SDK and Photon 4.1.6.12.
/// </para>
/// <para>
/// The numbering is dense rather than mnemonic, unlike V16's ASCII-letter codes, and much of
/// it exists to make common values small: booleans and zeroes are encoded entirely by their
/// tag, and small integers get width-specialised tags so a value that fits in one byte costs
/// one byte.
/// </para>
/// </remarks>
public enum GpType18 : byte
{
    Unknown = 0,
    Boolean = 2,
    Byte = 3,
    Short = 4,
    Float = 5,
    Double = 6,
    String = 7,
    Null = 8,

    /// <summary>Zig-zag varint.</summary>
    CompressedInt = 9,

    /// <summary>Zig-zag varint.</summary>
    CompressedLong = 10,

    /// <summary>Non-negative int in one byte. The trailing underscore marks the negative form.</summary>
    Int1 = 11,
    Int1_ = 12,
    Int2 = 13,
    Int2_ = 14,
    L1 = 15,
    L1_ = 16,
    L2 = 17,
    L2_ = 18,

    Custom = 19,
    Dictionary = 20,
    Hashtable = 21,
    ObjectArray = 23,
    OperationRequest = 24,
    OperationResponse = 25,
    EventData = 26,

    /// <summary>The tag is the value; no payload follows.</summary>
    BooleanFalse = 27,

    /// <inheritdoc cref="BooleanFalse"/>
    BooleanTrue = 28,

    /// <summary>Zero, encoded by tag alone.</summary>
    ShortZero = 29,
    IntZero = 30,
    LongZero = 31,
    FloatZero = 32,
    DoubleZero = 33,
    ByteZero = 34,

    /// <summary>Flag OR'd onto an element type to make its array type.</summary>
    Array = 64,

    BooleanArray = 66,
    ByteArray = 67,
    ShortArray = 68,
    FloatArray = 69,
    DoubleArray = 70,
    StringArray = 71,
    CompressedIntArray = 73,
    CompressedLongArray = 74,
    CustomTypeArray = 83,
    DictionaryArray = 84,
    HashtableArray = 85,

    /// <summary>Custom type whose code is carried in the tag rather than a following byte.</summary>
    CustomTypeSlim = 128,
}
