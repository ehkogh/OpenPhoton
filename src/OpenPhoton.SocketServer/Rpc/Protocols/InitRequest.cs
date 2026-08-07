using System.Text;

namespace Photon.SocketServer.Rpc.Protocols;

/// <summary>
/// The first message a client sends: which serialisation it wants and which app it wants.
/// </summary>
/// <remarks>
/// Layout, taken from a real client's init recorded by our own listener. Offsets are within
/// the message body, i.e. after the two-byte RTS header:
/// <code>
/// offset 0   0x01        constant in every observed init
/// offset 1   protocol    0x06 = GpBinaryV16, 0x08 = GpBinaryV18
/// offset 2-6 0x1E 0x51 0x11 0x00 0x00   meaning not established
/// offset 7+  32 bytes    application id, zero padded (NOT length prefixed)
/// </code>
/// <para>
/// Bytes 2-6 are deliberately <em>not</em> interpreted. The obvious reading is a client
/// version, but the library that sent them is 5.1.17 and they decode as 30.81.17 — the
/// revision matches and nothing else does, so the obvious reading is wrong. They were
/// byte-identical across captures that differed in serialisation, so they carry nothing we
/// currently need. Exposed raw rather than given a name that would assert more than we know.
/// </para>
/// </remarks>
public sealed class InitRequest
{
    /// <summary>Fixed width of the application id field.</summary>
    public const int ApplicationIdLength = 32;

    /// <summary>Body length: 7 bytes of preamble plus the app id.</summary>
    public const int BodyLength = 7 + ApplicationIdLength;

    const int ProtocolOffset = 1;
    const int UnidentifiedOffset = 2;
    const int UnidentifiedLength = 5;
    const int ApplicationIdOffset = 7;

    /// <summary>
    /// Wire values of the serialisation field.
    /// </summary>
    /// <remarks>
    /// Established by capturing two inits that differed only in
    /// <c>SerializationProtocolType</c>: exactly one byte changed, 0x06 to 0x08. The rule
    /// mapping these numbers to the protocol is not known — only the mapping itself, which
    /// is all a server needs in order to answer correctly.
    /// </remarks>
    public const byte ProtocolGpBinaryV16 = 0x06;

    /// <inheritdoc cref="ProtocolGpBinaryV16"/>
    public const byte ProtocolGpBinaryV18 = 0x08;

    public ProtocolType Protocol { get; init; }

    /// <summary>Raw serialisation byte, retained so unexpected values can be logged rather than lost.</summary>
    public byte ProtocolByte { get; init; }

    /// <summary>Bytes 2-6, whose meaning is not established. See the type remarks.</summary>
    public byte[] Unidentified { get; init; } = [];

    public string ApplicationId { get; init; } = string.Empty;

    /// <summary>Parses an init body (the bytes after the RTS header).</summary>
    public static bool TryParse(ReadOnlySpan<byte> body, out InitRequest? request, out string? error)
    {
        request = null;
        error = null;

        if (body.Length < BodyLength)
        {
            error = $"Init body is {body.Length} bytes; expected at least {BodyLength}.";
            return false;
        }

        var protocolByte = body[ProtocolOffset];
        ProtocolType protocol;

        switch (protocolByte)
        {
            case ProtocolGpBinaryV16: protocol = ProtocolType.GpBinaryV16; break;
            case ProtocolGpBinaryV18: protocol = ProtocolType.GpBinaryV18; break;
            default:
                error = $"Unsupported serialisation byte 0x{protocolByte:X2}.";
                return false;
        }

        var idBytes = body.Slice(ApplicationIdOffset, ApplicationIdLength);
        var idLength = idBytes.IndexOf((byte)0);
        if (idLength < 0) idLength = ApplicationIdLength;

        request = new InitRequest
        {
            Protocol = protocol,
            ProtocolByte = protocolByte,
            Unidentified = body.Slice(UnidentifiedOffset, UnidentifiedLength).ToArray(),
            ApplicationId = Encoding.UTF8.GetString(idBytes[..idLength]),
        };
        return true;
    }

    public override string ToString() =>
        $"Init(app='{ApplicationId}', protocol={Protocol} [0x{ProtocolByte:X2}], "
        + $"unidentified=[{Convert.ToHexString(Unidentified)}])";
}

/// <summary>
/// The server's reply to <see cref="InitRequest"/>.
/// </summary>
/// <remarks>
/// Three bytes in total, matching what a real Photon server sent: <c>F3 01 00</c> — magic,
/// <see cref="RtsMessageType.InitResponse"/>, then a zero status byte.
/// </remarks>
public static class InitResponse
{
    public const int Size = 3;

    /// <summary>Writes the init response. Returns bytes written.</summary>
    public static int Write(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"Need {Size} bytes.", nameof(destination));

        RtsFraming.WriteHeader(destination, RtsMessageType.InitResponse, encrypted: false);
        destination[2] = 0x00;
        return Size;
    }
}
