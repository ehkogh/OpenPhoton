using System.Buffers;
using System.Net;
using System.Net.Sockets;

namespace OpenPhoton.Transport;

/// <summary>
/// Accepts TCP connections on one or more ports and streams the bytes to a handler.
/// </summary>
/// <remarks>
/// Photon distinguishes server roles by port (NameServer / Master / GameServer), and a
/// client walks between them during a session. Binding several ports in one host means a
/// client can complete that walk against us without needing three processes.
/// </remarks>
public sealed class PhotonTcpListener(IRawConnectionHandler handler, Action<string>? log = null)
{
    readonly IRawConnectionHandler _handler = handler;
    readonly Action<string> _log = log ?? (_ => { });
    readonly List<TcpListener> _listeners = [];

    long _nextConnectionId;

    /// <summary>Bytes requested per read. Large enough that a whole Photon message normally lands in one chunk.</summary>
    public int ReceiveBufferSize { get; init; } = 64 * 1024;

    /// <summary>Binds every port and serves until <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(IEnumerable<int> ports, CancellationToken ct)
    {
        var accepts = new List<Task>();

        foreach (var port in ports)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            try
            {
                listener.Start();
            }
            catch (SocketException ex)
            {
                _log($"port {port}: bind failed ({ex.SocketErrorCode})");
                continue;
            }

            _listeners.Add(listener);
            _log($"listening on :{port}");
            accepts.Add(AcceptLoopAsync(listener, ct));
        }

        if (accepts.Count == 0) throw new InvalidOperationException("No ports could be bound.");

        try
        {
            await Task.WhenAll(accepts);
        }
        finally
        {
            foreach (var l in _listeners) l.Stop();
        }
    }

    async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException ex)
            {
                _log($"accept failed: {ex.SocketErrorCode}");
                continue;
            }

            socket.NoDelay = true;

            _ = ServeAsync(socket, ct);
        }
    }

    async Task ServeAsync(Socket socket, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextConnectionId);
        var stream = new NetworkStream(socket, ownsSocket: false);
        var connection = new RawConnection(id, socket, stream);

        Exception? failure = null;
        try
        {
            await _handler.OnConnectedAsync(connection, ct);

            var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer, ct);
                    if (read == 0) break;
                    await _handler.OnReceivedAsync(connection, buffer.AsMemory(0, read), ct);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            try { await _handler.OnDisconnectedAsync(connection, failure, CancellationToken.None); }
            catch (Exception ex) { _log($"{connection}: disconnect handler threw: {ex.Message}"); }

            connection.Close();
        }
    }
}
