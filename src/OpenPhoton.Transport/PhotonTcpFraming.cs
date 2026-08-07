using System.Buffers.Binary;

namespace OpenPhoton.Transport;

/// <summary>What <see cref="PhotonTcpFraming.TryRead"/> found at the head of a buffer.</summary>
public enum FrameKind
{
    /// <summary>Not enough bytes yet; keep buffering and retry.</summary>
    Incomplete,

    /// <summary>A length-framed record. The payload is a Photon message starting with its own magic byte.</summary>
    Message,

    /// <summary>A ping. Carries a timestamp and is not length-framed.</summary>
    Ping,

    /// <summary>Unrecognised leading byte. The stream is desynchronised and cannot be recovered.</summary>
    Invalid,
}

/// <summary>
/// Splits a Photon TCP byte stream into messages.
/// </summary>
/// <remarks>
/// <para>Confirmed by pointing a real Photon client at our own listener and reading what it sent.</para>
/// <para>
/// Records are framed as:
/// </para>
/// <code>
/// FB  00 00 00 30  00 01   F3 00 ...
/// ^   ^            ^       ^
/// |   |            |       message (its own magic byte follows)
/// |   |            two bytes, believed to be a message count
/// |   uint32 big-endian: total record length INCLUDING these 7 header bytes
/// magic
/// </code>
/// <para>
/// <b>Pings do not use this framing at all.</b> A client ping is five raw bytes — 0xF0
/// followed by a big-endian millisecond timestamp — written straight onto the stream with
/// no 0xFB record around it. Observed roughly once a second.
/// </para>
/// <para>
/// That asymmetry is the trap here. A reader that assumes every record begins with 0xFB
/// works perfectly through the handshake and then desynchronises permanently the first
/// time a ping arrives, about one second in. The two shapes must be dispatched on the
/// leading byte, which is why this returns a <see cref="FrameKind"/> rather than just a
/// payload.
/// </para>
/// </remarks>
public static class PhotonTcpFraming
{
    /// <summary>Leading byte of a length-framed record.</summary>
    public const byte RecordMagic = 0xFB;

    /// <summary>Magic (1) + length (4) + trailing pair (2).</summary>
    public const int RecordHeaderSize = 7;

    /// <summary>Offset of the big-endian uint32 length within the record header.</summary>
    public const int RecordLengthOffset = 1;

    /// <summary>Leading byte of a ping.</summary>
    public const byte PingMagic = 0xF0;

    /// <summary>Client to server: magic plus a 32-bit timestamp.</summary>
    public const int ClientPingSize = 5;

    /// <summary>Server to client: magic plus the echoed client timestamp and the server's own.</summary>
    public const int ServerPingSize = 9;

    /// <summary>
    /// Guards against a corrupt length field causing a huge allocation.
    /// </summary>
    /// <remarks>
    /// Matches Photon's own <c>MaxMessageSize</c> of 512000 from <c>PhotonServer.config</c>,
    /// rather than an arbitrary ceiling. A real client never sends more than this, so
    /// anything larger is corruption or an attack, and accepting it would let one peer ask
    /// us to allocate as much memory as its length field claims.
    /// </remarks>
    public const int MaxRecordLength = 512_000;

    /// <summary>
    /// Reads one frame from the head of <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">Bytes received so far, starting at a frame boundary.</param>
    /// <param name="payload">
    /// For <see cref="FrameKind.Message"/>, the message inside the record — its own magic
    /// byte first. For <see cref="FrameKind.Ping"/>, the bytes after the ping magic.
    /// </param>
    /// <param name="consumed">Bytes to discard from the front of the buffer.</param>
    /// <param name="pingSize">
    /// Ping length for the direction being read. Defaults to <see cref="ClientPingSize"/>,
    /// which is what a server reading from a client wants.
    /// </param>
    public static FrameKind TryRead(
        ReadOnlySpan<byte> buffer,
        out ReadOnlySpan<byte> payload,
        out int consumed,
        int pingSize = ClientPingSize)
    {
        payload = default;
        consumed = 0;

        if (buffer.Length == 0) return FrameKind.Incomplete;

        switch (buffer[0])
        {
            case PingMagic:
                if (buffer.Length < pingSize) return FrameKind.Incomplete;
                payload = buffer[1..pingSize];
                consumed = pingSize;
                return FrameKind.Ping;

            case RecordMagic:
                if (buffer.Length < RecordHeaderSize) return FrameKind.Incomplete;

                var declared = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(RecordLengthOffset, 4));

                if (declared <= RecordHeaderSize || declared > MaxRecordLength) return FrameKind.Invalid;
                if (buffer.Length < declared) return FrameKind.Incomplete;

                payload = buffer[RecordHeaderSize..(int)declared];
                consumed = (int)declared;
                return FrameKind.Message;

            default:
                return FrameKind.Invalid;
        }
    }

    /// <summary>Reads the millisecond timestamp from a ping payload (the bytes after the magic).</summary>
    public static uint ReadPingTimestamp(ReadOnlySpan<byte> pingPayload) =>
        BinaryPrimitives.ReadUInt32BigEndian(pingPayload[..4]);

    /// <summary>Wraps a message in a record header. Returns total bytes written.</summary>
    public static int WriteRecord(Span<byte> destination, ReadOnlySpan<byte> message)
    {
        var total = RecordHeaderSize + message.Length;
        if (destination.Length < total)
            throw new ArgumentException($"Need {total} bytes for this record.", nameof(destination));

        destination[0] = RecordMagic;
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(RecordLengthOffset, 4), (uint)total);
        destination[5] = 0x00;
        destination[6] = 0x01;
        message.CopyTo(destination[RecordHeaderSize..]);

        return total;
    }
}
