using System.Buffers.Binary;

namespace OpenPhoton.Transport.Enet;

/// <summary>
/// Photon's reliable-UDP command types.
/// </summary>
/// <remarks>
/// Photon's UDP layer is derived from ENet but is not ENet: the command set overlaps, the
/// header layout does not. These values are the ones observed on the wire from a real client;
/// see <c>spec/enet-findings.md</c> for the captures they come from.
/// </remarks>
public enum EnetCommandType : byte
{
    None = 0,
    Acknowledge = 1,
    Connect = 2,
    VerifyConnect = 3,
    Disconnect = 4,
    Ping = 5,
    SendReliable = 6,
    SendUnreliable = 7,
    SendReliableFragment = 8,
    SendUnsequenced = 11,
    ServerTime = 12,

    /// <summary>An unreliable payload the peer has already processed. Not sent to a server.</summary>
    SendUnreliableProcessed = 13,

    /// <summary>
    /// A payload that must arrive, but not in order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client uses this for game traffic — clients send their <c>RaiseEvent</c> operations
    /// this way — so a server that does not implement it looks perfectly healthy right up to
    /// the moment a player finishes joining, and then goes deaf.
    /// </para>
    /// <para>
    /// It must be answered with <see cref="AcknowledgeUnsequenced"/> rather than
    /// <see cref="Acknowledge"/>. The client keeps these in a separate per-channel table and
    /// does not consult it when an ordinary acknowledgement arrives, so acknowledging one of
    /// these the ordinary way is indistinguishable from never acknowledging it at all.
    /// </para>
    /// </remarks>
    SendReliableUnsequenced = 14,

    /// <summary>One fragment of an oversized <see cref="SendReliableUnsequenced"/>.</summary>
    SendFragmentUnsequenced = 15,

    /// <summary>Acknowledges a reliable-unsequenced command.</summary>
    AcknowledgeUnsequenced = 16,
}

/// <summary>
/// The delivery mode in a command's fourth-from-first header byte.
/// </summary>
/// <remarks>
/// These are values rather than independent bits — the client names them
/// <c>FV_UNRELIABLE</c>, <c>FV_RELIABLE</c>, <c>FV_UNRELIABLE_UNSEQUENCED</c> and
/// <c>FV_RELIBALE_UNSEQUENCED</c> (its spelling), matching its public
/// <c>DeliveryMode</c> enum exactly. Testing <c>&amp; Reliable</c> still answers "does this
/// need acknowledging", because both modes that do have bit 0 set, but nothing else about
/// them should be read as a bitmask.
/// </remarks>
public static class EnetCommandFlags
{
    public const byte Unreliable = 0;

    /// <summary>The peer must acknowledge this command.</summary>
    public const byte Reliable = 1;

    public const byte Unsequenced = 2;

    /// <summary>Must arrive, but may arrive out of order. Acknowledged differently.</summary>
    public const byte ReliableUnsequenced = 3;
}

/// <summary>
/// The 12-byte header every Photon UDP datagram starts with.
/// </summary>
/// <remarks>
/// <para>
/// Everything is big-endian, matching the rest of Photon's wire formats.
/// </para>
/// <para>
/// <c>PeerId</c> is the server's handle for the connection and is echoed by the client on
/// every datagram after the handshake. It is not a substitute for the source address: a peer
/// behind a NAT can change port mid-session, and the id is what identifies it across that.
/// </para>
/// </remarks>
public readonly record struct EnetDatagramHeader(ushort PeerId, byte Flags, byte CommandCount, int Timestamp, int Challenge)
{
    public const int Size = 12;

    /// <summary>Set when a 4-byte CRC follows the header and covers the datagram.</summary>
    public const byte CrcEnabledFlag = 0xCC;

    public bool IsCrcEnabled => Flags == CrcEnabledFlag;

    public static bool TryRead(ReadOnlySpan<byte> bytes, out EnetDatagramHeader header)
    {
        header = default;
        if (bytes.Length < Size) return false;

        header = new EnetDatagramHeader(
            BinaryPrimitives.ReadUInt16BigEndian(bytes),
            bytes[2],
            bytes[3],
            BinaryPrimitives.ReadInt32BigEndian(bytes[4..]),
            BinaryPrimitives.ReadInt32BigEndian(bytes[8..]));

        return true;
    }

    public void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, PeerId);
        destination[2] = Flags;
        destination[3] = CommandCount;
        BinaryPrimitives.WriteInt32BigEndian(destination[4..], Timestamp);
        BinaryPrimitives.WriteInt32BigEndian(destination[8..], Challenge);
    }
}

/// <summary>
/// One command inside a datagram.
/// </summary>
/// <remarks>
/// <c>Length</c> counts the command header as well as its payload, so it is the stride to the
/// next command rather than the size of the data. Reading it as a payload length walks the
/// parser 12 bytes short on every command and desynchronises within one datagram.
/// </remarks>
public readonly record struct EnetCommandHeader(
    EnetCommandType Type, byte Channel, byte Flags, byte Reserved, int Length, int ReliableSequenceNumber)
{
    public const int Size = 12;

    /// <summary>
    /// The constant the client puts in the fourth header byte.
    /// </summary>
    /// <remarks>
    /// Every command a real client sends carries 4 here, whatever its type — Connect,
    /// Acknowledge and SendReliable alike. Its meaning is not documented anywhere available
    /// to this project, so it is mirrored rather than interpreted: sending back what the
    /// client sends cannot be wrong, and guessing at a meaning could be.
    /// </remarks>
    public const byte ClientReservedByte = 4;

    public int PayloadLength => Length - Size;

    public static bool TryRead(ReadOnlySpan<byte> bytes, out EnetCommandHeader header)
    {
        header = default;
        if (bytes.Length < Size) return false;

        header = new EnetCommandHeader(
            (EnetCommandType)bytes[0],
            bytes[1],
            bytes[2],
            bytes[3],
            BinaryPrimitives.ReadInt32BigEndian(bytes[4..]),
            BinaryPrimitives.ReadInt32BigEndian(bytes[8..]));

        return true;
    }

    public void Write(Span<byte> destination)
    {
        destination[0] = (byte)Type;
        destination[1] = Channel;
        destination[2] = Flags;
        destination[3] = Reserved;
        BinaryPrimitives.WriteInt32BigEndian(destination[4..], Length);
        BinaryPrimitives.WriteInt32BigEndian(destination[8..], ReliableSequenceNumber);
    }
}

/// <summary>A command as read off the wire, with its payload.</summary>
public readonly record struct EnetCommand(EnetCommandHeader Header, ReadOnlyMemory<byte> Payload)
{
    public EnetCommandType Type => Header.Type;

    public override string ToString() =>
        $"{Header.Type} ch={Header.Channel} flags=0x{Header.Flags:X2} "
      + $"seq={Header.ReliableSequenceNumber} len={Header.Length} payload={Payload.Length}";
}

/// <summary>Splits a datagram into its commands.</summary>
public static class EnetDatagram
{
    /// <summary>
    /// Reads a datagram's header and commands.
    /// </summary>
    /// <remarks>
    /// Returns false rather than throwing on anything malformed. A UDP listener is reachable
    /// by anyone who can send a packet, so a parser that throws on bad input hands the
    /// process's liveness to whoever sends the first random datagram.
    /// </remarks>
    public static bool TryParse(ReadOnlyMemory<byte> datagram, out EnetDatagramHeader header,
                                out List<EnetCommand> commands)
    {
        commands = [];

        var span = datagram.Span;
        if (!EnetDatagramHeader.TryRead(span, out header)) return false;

        var offset = EnetDatagramHeader.Size;

        if (header.IsCrcEnabled)
        {
            if (datagram.Length < offset + 4) return false;
            offset += 4;
        }

        for (var i = 0; i < header.CommandCount; i++)
        {
            if (offset + EnetCommandHeader.Size > datagram.Length) return false;
            if (!EnetCommandHeader.TryRead(span[offset..], out var command)) return false;

            if (command.Length < EnetCommandHeader.Size) return false;
            if (offset + command.Length > datagram.Length) return false;

            commands.Add(new EnetCommand(
                command,
                datagram[(offset + EnetCommandHeader.Size)..(offset + command.Length)]));

            offset += command.Length;
        }

        return true;
    }

    /// <summary>Photon's CRC32, over the datagram with the CRC field zeroed.</summary>
    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(crc & 1)));
        }

        return ~crc;
    }
}
