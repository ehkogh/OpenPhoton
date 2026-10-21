using Photon.SocketServer.Rpc.Protocols;

namespace OpenPhoton.Transport;

/// <summary>Reads one whole Photon message, whatever transport delivered it.</summary>
public static class PhotonMessages
{
    /// <summary>
    /// Splits a complete message into its RTS header and body.
    /// </summary>
    /// <remarks>
    /// Recognises the token init as well as ordinary <c>0xF3</c> messages. A token init has no
    /// RTS header — it starts with <c>POST /?</c> (<see cref="RtsMagicByte.Initv2Post"/>) — so
    /// a reader that only knew <c>0xF3</c> dropped it, and every connection after the name
    /// server under AuthOnce went unanswered. It is reported as an <see cref="RtsMessageType.Init"/>
    /// with the whole payload as its body.
    /// </remarks>
    public static bool TryRead(ReadOnlyMemory<byte> message, out RtsMessageHeader header, out ReadOnlyMemory<byte> body)
    {
        if (TokenInitRequest.IsTokenInit(message.Span))
        {
            header = new RtsMessageHeader
            {
                MagicByte = RtsMagicByte.Initv2Post,
                MessageType = RtsMessageType.Init,
                IsEncrypted = false,
                SizeInBytes = 0,
            };
            body = message;
            return true;
        }

        if (RtsFraming.TryReadHeader(message.Span, out header))
        {
            body = message[header.SizeInBytes..];
            return true;
        }

        body = default;
        return false;
    }
}
