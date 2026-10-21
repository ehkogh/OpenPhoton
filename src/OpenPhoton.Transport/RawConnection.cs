using System.Net;
using System.Net.Sockets;
using Photon.SocketServer;
using Photon.SocketServer.Rpc.Protocols;
using ProtocolType = Photon.SocketServer.Rpc.Protocols.ProtocolType;

namespace OpenPhoton.Transport;

/// <summary>How whole messages are delimited on a connection.</summary>
public enum MessageFraming
{
    /// <summary>Photon's 0xFB record header, needed because TCP has no message boundaries.</summary>
    TcpRecord,

    /// <summary>None. A datagram is already a boundary, so a length prefix would be redundant.</summary>
    Datagram,
}

/// <summary>One client connection, before any Photon framing is applied.</summary>
/// <remarks>
/// Backed by either a TCP socket or a UDP peer. Everything above this class is written
/// against the connection rather than the socket, which is what lets the same server serve
/// both transports without a second copy of itself.
/// </remarks>
public sealed class RawConnection
{
    readonly Socket? _socket;
    readonly NetworkStream? _stream;
    readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? _send;
    readonly Action? _close;
    readonly Func<bool>? _isConnected;

    /// <summary>A TCP connection.</summary>
    public RawConnection(long id, Socket socket, NetworkStream stream)
    {
        Id = id;
        _socket = socket;
        _stream = stream;
        RemoteEndPoint = socket.RemoteEndPoint;
        LocalEndPoint = socket.LocalEndPoint;
        Framing = MessageFraming.TcpRecord;
        Transport = NetworkProtocolType.Tcp;
    }

    /// <summary>A datagram-backed connection, where sending and closing belong to the peer.</summary>
    public RawConnection(long id, EndPoint remote, EndPoint local,
                         Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send,
                         Action close, Func<bool> isConnected)
    {
        Id = id;
        RemoteEndPoint = remote;
        LocalEndPoint = local;
        _send = send;
        _close = close;
        _isConnected = isConnected;
        Framing = MessageFraming.Datagram;
        Transport = NetworkProtocolType.Udp;
    }

    public long Id { get; }

    public EndPoint? RemoteEndPoint { get; }

    public EndPoint? LocalEndPoint { get; }

    public MessageFraming Framing { get; }

    /// <summary>The transport the client is using, as Photon names it.</summary>
    /// <remarks>
    /// Not derivable from <see cref="Framing"/>: a WebSocket is message-framed like a
    /// datagram, but it must be told about WebSocket addresses and does not run the key
    /// exchange. Set by whichever listener built the connection.
    /// </remarks>
    public NetworkProtocolType Transport { get; init; }

    /// <summary>Serialisation agreed before any Photon message, if the transport negotiates one.</summary>
    /// <remarks>
    /// WebSocket clients name it in <c>Sec-WebSocket-Protocol</c> and then send no binary
    /// init at all, so for them this is the only place the protocol is ever stated.
    /// </remarks>
    public ProtocolType? NegotiatedProtocol { get; init; }

    /// <summary>Port the client connected to. Photon assigns roles by port, so this identifies the server role.</summary>
    public int LocalPort => (LocalEndPoint as IPEndPoint)?.Port ?? 0;

    public bool IsConnected => _isConnected?.Invoke() ?? _socket?.Connected ?? false;

    readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>
    /// Writes one complete message to the connection.
    /// </summary>
    /// <remarks>
    /// Serialised, because sends genuinely do arrive from several threads at once: every
    /// actor in a room broadcasts to every other from its own connection's thread, and a
    /// plugin may send from a timer of its own. <see cref="NetworkStream"/> does not
    /// serialise writers, so two concurrent sends can interleave their bytes mid-message.
    /// The client would then read a length prefix followed by somebody else's payload and
    /// desynchronise permanently — the same failure as getting the framing wrong, but
    /// intermittent and load-dependent, which makes it far harder to recognise.
    /// </remarks>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        if (_send is not null)
        {
            await _send(bytes, ct);
            return;
        }

        await _writeGate.WaitAsync(ct);
        try
        {
            await _stream!.WriteAsync(bytes, ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Sends a complete RTS message, applying whatever framing this transport needs.</summary>
    /// <remarks>
    /// The one place the transports differ above the socket. Keeping the decision here rather
    /// than at each call site means a new transport cannot be half-wired: there is exactly
    /// one thing to get right, and it is named after the thing it varies by.
    /// </remarks>
    public ValueTask SendMessageAsync(ReadOnlyMemory<byte> message, CancellationToken ct = default)
    {
        if (Framing == MessageFraming.Datagram) return SendAsync(message, ct);

        var record = new byte[PhotonTcpFraming.RecordHeaderSize + message.Length];
        PhotonTcpFraming.WriteRecord(record, message.Span);
        return SendAsync(record, ct);
    }

    public void Close()
    {
        if (_close is not null)
        {
            _close();
            return;
        }

        try { _socket!.Shutdown(SocketShutdown.Both); }
        catch (SocketException) { /* already gone */ }
        catch (ObjectDisposedException) { /* already closed */ }

        _stream!.Dispose();
        _socket!.Dispose();
    }

    public override string ToString() => $"#{Id} {RemoteEndPoint} -> :{LocalPort}";
}

/// <summary>
/// Receives raw connection events. Deliberately knows nothing about message boundaries.
/// </summary>
/// <remarks>
/// TCP is a byte stream with no inherent framing, and Photon's stream framing is not yet
/// known to us. Handing the handler whatever arrived — rather than guessing at message
/// boundaries here — keeps that unknown in one place, and means this class does not have to
/// change once the framing is understood.
/// </remarks>
public interface IRawConnectionHandler
{
    ValueTask OnConnectedAsync(RawConnection connection, CancellationToken ct);

    /// <summary>
    /// Called with each chunk as it arrives. Chunk boundaries are an artefact of the
    /// network, not of the protocol: one chunk may hold several messages, or part of one.
    /// </summary>
    ValueTask OnReceivedAsync(RawConnection connection, ReadOnlyMemory<byte> bytes, CancellationToken ct);

    ValueTask OnDisconnectedAsync(RawConnection connection, Exception? error, CancellationToken ct);
}
