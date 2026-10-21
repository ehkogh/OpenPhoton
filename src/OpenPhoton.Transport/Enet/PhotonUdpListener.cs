using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Photon.SocketServer.Rpc.Protocols;

namespace OpenPhoton.Transport.Enet;

/// <summary>
/// Serves Photon's reliable-UDP transport, handing whole messages to the same handler TCP uses.
/// </summary>
/// <remarks>
/// <para>
/// This is a transport and nothing more. Once a datagram's commands have been unpacked, a
/// <c>SendReliable</c> payload is an RTS message byte-for-byte identical to what arrives over
/// TCP minus the record header — so everything above this point is code that already exists
/// and does not know which transport it is serving.
/// </para>
/// <para>
/// UDP has no connections, so they have to be invented. A peer is created when its Connect
/// arrives and destroyed when it disconnects or goes quiet, and the surrounding server is
/// told about both through the same callbacks a TCP accept and close produce.
/// </para>
/// </remarks>
public sealed class PhotonUdpListener(IPhotonMessageHandler handler, Action<string>? log = null)
{
    readonly IPhotonMessageHandler _handler = handler;
    readonly Action<string> _log = log ?? (_ => { });
    readonly ConcurrentDictionary<string, Connection> _peers = new();

    long _nextConnectionId;
    int _nextPeerId;

    /// <summary>How often to retransmit and expire. Photon's own client pings once a second.</summary>
    public TimeSpan ServiceInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Logs every datagram's commands. For diagnosing the transport itself.</summary>
    /// <remarks>
    /// Off by default because it is one line per ping per peer per second, which buries
    /// everything else. It is kept rather than deleted because the questions it answers —
    /// which side stopped sending, and what it last sent — cannot be answered any other way
    /// once a client is only saying "timeout".
    /// </remarks>
    public bool TraceDatagrams { get; init; }
        = Environment.GetEnvironmentVariable("OPENPHOTON_TRACE_UDP") == "1";

    /// <summary>A peer, its invented connection, and the socket it lives on.</summary>
    sealed record Connection(EnetPeer Peer, RawConnection Raw, UdpClient Socket);

    public int PeerCount => _peers.Count;

    public async Task RunAsync(IEnumerable<int> ports, CancellationToken ct)
    {
        var running = new List<Task>();

        foreach (var port in ports)
        {
            UdpClient socket;
            try
            {
                socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            }
            catch (SocketException ex)
            {
                _log($"udp {port}: bind failed ({ex.SocketErrorCode})");
                continue;
            }

            try { socket.Client.IOControl(SioUdpConnectionReset, [0, 0, 0, 0], null); }
            catch (SocketException) { /* not Windows */ }
            catch (PlatformNotSupportedException) { /* not Windows */ }

            _log($"listening on udp :{port}");
            running.Add(ReceiveLoopAsync(socket, port, ct));
        }

        if (running.Count == 0) throw new InvalidOperationException("No UDP ports could be bound.");

        running.Add(ServiceLoopAsync(ct));
        await Task.WhenAll(running);
    }

    const int SioUdpConnectionReset = -1744830452;

    async Task ReceiveLoopAsync(UdpClient socket, int port, CancellationToken ct)
    {
        using (socket)
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await socket.ReceiveAsync(ct);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException ex)
                {
                    _log($"udp {port}: receive failed ({ex.SocketErrorCode})");
                    continue;
                }

                try
                {
                    await HandleAsync(socket, port, received, ct);
                }
                catch (Exception ex)
                {
                    _log($"udp {port}: {received.RemoteEndPoint}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    async ValueTask HandleAsync(UdpClient socket, int port, UdpReceiveResult received, CancellationToken ct)
    {
        if (!EnetDatagram.TryParse(received.Buffer, out var header, out var commands)) return;

        if (TraceDatagrams)
        {
            _log($"[udp>] {received.RemoteEndPoint} peer={header.PeerId} t={header.Timestamp} f=0x{header.Flags:X2} "
               + string.Join(" ", commands.Select(c => $"{c.Type}/ch{c.Header.Channel}/seq{c.Header.ReliableSequenceNumber}")));
        }

        if (commands.Any(c => !IsKnown(c.Type)))
        {
            _log($"[udp!] {received.RemoteEndPoint} unrecognised command; {received.Buffer.Length} raw bytes:");
            _log("       " + Convert.ToHexString(received.Buffer));
        }

        var key = $"{port}/{received.RemoteEndPoint}";
        var isConnect = commands.Any(c => c.Type == EnetCommandType.Connect);

        if (!_peers.TryGetValue(key, out var connection))
        {
            if (!isConnect)
            {
                _log($"[?] udp {received.RemoteEndPoint}: {commands.FirstOrDefault().Type} from an unknown peer, ignored");
                return;
            }

            connection = Open(socket, port, received.RemoteEndPoint, header.Challenge);
            _peers[key] = connection;

            _log($"[+] {connection.Raw}  udp");
            await _handler.OnConnectedAsync(connection.Raw, ct);
        }

        var result = connection.Peer.Receive(header, commands, received.RemoteEndPoint);

        foreach (var message in result.Messages)
        {
            if (!PhotonMessages.TryRead(message, out var rtsHeader, out var body))
            {
                _log($"[!] {connection.Raw}: payload with no RTS header, dropped");
                continue;
            }

            await _handler.OnMessageAsync(connection.Raw, rtsHeader, body, ct);
        }

        await FlushAsync(connection, ct);

        if (result.Disconnected) await CloseAsync(key, connection, null, ct);
    }

    /// <summary>Whether a command type is one this transport knows how to read.</summary>
    /// <remarks>
    /// A switch rather than <c>Enum.IsDefined</c>, which reflects over the enum's fields on
    /// every call and would be doing that once per command per datagram.
    /// </remarks>
    static bool IsKnown(EnetCommandType type) => type switch
    {
        EnetCommandType.Acknowledge or EnetCommandType.Connect or EnetCommandType.VerifyConnect
            or EnetCommandType.Disconnect or EnetCommandType.Ping or EnetCommandType.SendReliable
            or EnetCommandType.SendUnreliable or EnetCommandType.SendReliableFragment
            or EnetCommandType.SendUnsequenced or EnetCommandType.ServerTime
            or EnetCommandType.SendUnreliableProcessed or EnetCommandType.SendReliableUnsequenced
            or EnetCommandType.SendFragmentUnsequenced or EnetCommandType.AcknowledgeUnsequenced => true,
        _ => false,
    };

    Connection Open(UdpClient socket, int port, IPEndPoint remote, int challenge)
    {
        var next = Interlocked.Increment(ref _nextPeerId);
        var peerId = (ushort)(next % (ushort.MaxValue - 1) + 1);

        var peer = new EnetPeer(peerId, remote, challenge, _log);

        var id = Interlocked.Increment(ref _nextConnectionId);
        var local = new IPEndPoint(IPAddress.Any, port);

        RawConnection raw = null!;
        raw = new RawConnection(
            id, remote, local,
            send: async (bytes, token) =>
            {
                peer.Send(bytes);
                await FlushPeerAsync(peer, socket, token);
            },
            close: () => peer.Disconnect(),
            isConnected: () => !peer.IsDisconnected);

        return new Connection(peer, raw, socket);
    }

    ValueTask FlushAsync(Connection connection, CancellationToken ct) =>
        FlushPeerAsync(connection.Peer, connection.Socket, ct);

    async ValueTask FlushPeerAsync(EnetPeer peer, UdpClient socket, CancellationToken ct)
    {
        foreach (var datagram in peer.Drain())
        {
            if (TraceDatagrams && EnetDatagram.TryParse(datagram, out var header, out var commands))
            {
                _log($"[udp<] {peer.EndPoint} peer={header.PeerId} t={header.Timestamp} "
                   + string.Join(" ", commands.Select(c => $"{c.Type}/ch{c.Header.Channel}/seq{c.Header.ReliableSequenceNumber}")));
            }

            try
            {
                await socket.SendAsync(datagram, datagram.Length, peer.EndPoint);
            }
            catch (SocketException) { /* the peer will be expired by the service loop */ }
            catch (ObjectDisposedException) { return; }
        }
    }

    /// <summary>
    /// Retransmits unacknowledged commands and expires peers that have gone silent.
    /// </summary>
    /// <remarks>
    /// Both only happen on a clock. Nothing arrives from a peer that has stopped responding,
    /// so a purely reactive server would keep a dead connection — and its actor in a room —
    /// for as long as the process lived.
    /// </remarks>
    async Task ServiceLoopAsync(CancellationToken ct)
    {
        using var tick = new PeriodicTimer(ServiceInterval);

        try
        {
            while (await tick.WaitForNextTickAsync(ct))
            {
                foreach (var (key, connection) in _peers.ToArray())
                {
                    if (connection.Peer.IsDisconnected || connection.Peer.HasTimedOut)
                    {
                        await CloseAsync(key, connection,
                            connection.Peer.HasTimedOut ? new TimeoutException("Peer stopped responding.") : null, ct);
                        continue;
                    }

                    await FlushAsync(connection, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    async ValueTask CloseAsync(string key, Connection connection, Exception? error, CancellationToken ct)
    {
        if (!_peers.TryRemove(key, out _)) return;

        connection.Peer.Disconnect();
        await FlushAsync(connection, ct);

        _log($"[-] {connection.Raw}  udp {(error is null ? "closed" : error.Message)}");

        try
        {
            await _handler.OnDisconnectedAsync(connection.Raw, error, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log($"{connection.Raw}: disconnect handler threw: {ex.Message}");
        }
    }
}
