namespace Photon.SocketServer.Rpc.Protocols;

/// <summary>
/// Type codes used by the GpBinary V16 wire format.
/// </summary>
/// <remarks>
/// Every code is the ASCII value of a mnemonic letter, which is why the numbering looks
/// arbitrary: 'd' for double, 'i' for integer, '*' for null. Values are protocol facts —
/// they are fixed by the format and a compatible implementation has no freedom here.
/// </remarks>
public enum GpType : byte
{
    /// <summary>No type. In a <see cref="Dictionary"/> header this means "elements carry their own type code".</summary>
    Unknown = 0,

    /// <summary>'*' — null reference.</summary>
    Null = 42,

    /// <summary>'D' — dictionary with a declared key type and value type.</summary>
    Dictionary = 68,

    /// <summary>'a' — string[].</summary>
    StringArray = 97,

    /// <summary>'b' — 8-bit unsigned.</summary>
    Byte = 98,

    /// <summary>'c' — custom type, resolved through the custom type registry by code.</summary>
    Custom = 99,

    /// <summary>'d' — IEEE-754 binary64.</summary>
    Double = 100,

    /// <summary>'e' — <see cref="EventData"/>.</summary>
    EventData = 101,

    /// <summary>'f' — IEEE-754 binary32.</summary>
    Float = 102,

    /// <summary>'h' — hashtable; every key and value carries its own type code.</summary>
    Hashtable = 104,

    /// <summary>'i' — 32-bit signed.</summary>
    Integer = 105,

    /// <summary>'k' — 16-bit signed.</summary>
    Short = 107,

    /// <summary>'l' — 64-bit signed.</summary>
    Long = 108,

    /// <summary>'n' — int[].</summary>
    IntegerArray = 110,

    /// <summary>'o' — boolean, one byte.</summary>
    Boolean = 111,

    /// <summary>'p' — <see cref="OperationResponse"/>.</summary>
    OperationResponse = 112,

    /// <summary>'q' — <see cref="OperationRequest"/>.</summary>
    OperationRequest = 113,

    /// <summary>'s' — UTF-8 string with a 16-bit length prefix.</summary>
    String = 115,

    /// <summary>'x' — byte[] with a 32-bit length prefix.</summary>
    ByteArray = 120,

    /// <summary>'y' — array whose elements all share one type code, declared once.</summary>
    Array = 121,

    /// <summary>'z' — object[]; every element carries its own type code.</summary>
    ObjectArray = 122,
}
