namespace Photon.SocketServer.Rpc.Protocols;

/// <summary>First byte of a Photon message, identifying how the rest is framed.</summary>
public enum RtsMagicByte : byte
{
    /// <summary>0xF3 — standard binary message. Followed by a <see cref="RtsMessageType"/> byte.</summary>
    GpBinaryV2 = 243,

    /// <summary>0xF5 — the newer init handshake.</summary>
    InitV3 = 245,

    /// <summary>'P' (0x50) — HTTP-style init.</summary>
    Initv2Post = 80,

    /// <summary>'G' (0x47) — HTTP-style init.</summary>
    Initv2Get = 71,

    /// <summary>
    /// 0xF0 — ping/timesync. Absent from Photon's own <c>RtsMagicByte</c> enum but present
    /// on the wire; see <see cref="RtsFraming.PingMessageSize"/>.
    /// </summary>
    Ping = 240,
}

/// <summary>Message kind, carried in the byte after <see cref="RtsMagicByte.GpBinaryV2"/>.</summary>
/// <remarks>
/// Only the low 7 bits are the type. The top bit is the encryption flag — see
/// <see cref="RtsFraming.EncryptedFlag"/>.
/// </remarks>
public enum RtsMessageType : byte
{
    Init = 0,
    InitResponse = 1,
    Operation = 2,
    OperationResponse = 3,
    Event = 4,
    DisconnectMessage = 5,
    InternalOperationRequest = 6,
    InternalOperationResponse = 7,
    Message = 8,
    RawMessage = 9,
    Unknown = 255,
}

/// <summary>Parsed form of a Photon message header.</summary>
public struct RtsMessageHeader
{
    public RtsMagicByte MagicByte { get; set; }

    public RtsMessageType MessageType { get; set; }

    /// <summary>True when the payload after the header is encrypted.</summary>
    public bool IsEncrypted { get; set; }

    /// <summary>Bytes consumed by the header itself, so the payload starts at this offset.</summary>
    public byte SizeInBytes { get; set; }
}

/// <summary>
/// Reads and writes Photon's message headers.
/// </summary>
/// <remarks>
/// Confirmed against capture <c>20260804-175710-tcp</c>. Observed on the wire:
/// <c>F3 01</c> InitResponse, <c>F3 03</c> OperationResponse, <c>F3 04</c> Event,
/// <c>F3 07</c> InternalOperationResponse, and <c>F3 83</c> — an encrypted
/// OperationResponse, which is what fixes the encryption flag as the top bit of the type
/// byte rather than a separate field.
/// <para>
/// Note this operates on already-deframed messages. On a TCP stream something must first
/// delimit each message; that outer framing is still unknown and is deliberately not
/// modelled here.
/// </para>
/// </remarks>
public static class RtsFraming
{
    /// <summary>Top bit of the message-type byte: payload is encrypted.</summary>
    public const byte EncryptedFlag = 0x80;

    /// <summary>Low bits of the message-type byte: the actual type.</summary>
    public const byte MessageTypeMask = 0x7F;

    /// <summary>Magic byte plus type byte.</summary>
    public const int GpBinaryHeaderSize = 2;

    /// <summary>Ping frames are a fixed 9 bytes: magic plus two 32-bit fields.</summary>
    public const int PingMessageSize = 9;

    /// <summary>
    /// Reads a message header from the start of <paramref name="message"/>.
    /// </summary>
    /// <returns>False if the buffer is too short or the magic byte is unrecognised.</returns>
    public static bool TryReadHeader(ReadOnlySpan<byte> message, out RtsMessageHeader header)
    {
        header = default;
        if (message.Length < 1) return false;

        var magic = message[0];

        if (magic == (byte)RtsMagicByte.Ping)
        {
            if (message.Length < PingMessageSize) return false;
            header = new RtsMessageHeader
            {
                MagicByte = RtsMagicByte.Ping,
                MessageType = RtsMessageType.Message,
                IsEncrypted = false,
                SizeInBytes = 1,
            };
            return true;
        }

        if (magic != (byte)RtsMagicByte.GpBinaryV2) return false;
        if (message.Length < GpBinaryHeaderSize) return false;

        var typeByte = message[1];
        header = new RtsMessageHeader
        {
            MagicByte = RtsMagicByte.GpBinaryV2,
            MessageType = (RtsMessageType)(typeByte & MessageTypeMask),
            IsEncrypted = (typeByte & EncryptedFlag) != 0,
            SizeInBytes = GpBinaryHeaderSize,
        };
        return true;
    }

    /// <summary>Writes a GpBinary message header. Returns bytes written.</summary>
    public static int WriteHeader(Span<byte> destination, RtsMessageType type, bool encrypted)
    {
        if (destination.Length < GpBinaryHeaderSize)
            throw new ArgumentException($"Need {GpBinaryHeaderSize} bytes for a header.", nameof(destination));

        destination[0] = (byte)RtsMagicByte.GpBinaryV2;
        destination[1] = encrypted ? (byte)((byte)type | EncryptedFlag) : (byte)type;
        return GpBinaryHeaderSize;
    }
}
