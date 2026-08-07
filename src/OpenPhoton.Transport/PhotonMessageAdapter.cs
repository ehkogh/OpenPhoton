using System.Collections.Concurrent;
using Photon.SocketServer.Rpc.Protocols;

namespace OpenPhoton.Transport;

/// <summary>Connection events at message granularity, once framing has been removed.</summary>
public interface IPhotonMessageHandler
{
    ValueTask OnConnectedAsync(RawConnection connection, CancellationToken ct);

    /// <summary>One complete Photon message. <paramref name="body"/> excludes the RTS header.</summary>
    ValueTask OnMessageAsync(RawConnection connection, RtsMessageHeader header, ReadOnlyMemory<byte> body, CancellationToken ct);

    /// <summary>A ping. The client expects its timestamp echoed back promptly or it will disconnect.</summary>
    ValueTask OnPingAsync(RawConnection connection, uint clientTimestamp, CancellationToken ct);

    ValueTask OnDisconnectedAsync(RawConnection connection, Exception? error, CancellationToken ct);
}

/// <summary>
/// Turns the raw byte stream into whole Photon messages.
/// </summary>
/// <remarks>
/// Sits between <see cref="PhotonTcpListener"/> and application code so that nothing above
/// has to reason about TCP chunk boundaries, which are an artefact of the network rather
/// than the protocol: one read can deliver half a message or five of them.
/// </remarks>
public sealed class PhotonMessageAdapter(IPhotonMessageHandler handler, Action<string>? log = null) : IRawConnectionHandler
{
    readonly IPhotonMessageHandler _handler = handler;
    readonly Action<string> _log = log ?? (_ => { });
    readonly ConcurrentDictionary<long, StreamAccumulator> _buffers = new();

    public ValueTask OnConnectedAsync(RawConnection connection, CancellationToken ct)
    {
        _buffers[connection.Id] = new StreamAccumulator();
        return _handler.OnConnectedAsync(connection, ct);
    }

    public async ValueTask OnReceivedAsync(RawConnection connection, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (!_buffers.TryGetValue(connection.Id, out var buffer)) return;

        buffer.Append(bytes.Span);

        while (true)
        {
            var kind = PhotonTcpFraming.TryRead(buffer.Buffered, out var payload, out var consumed);

            if (kind == FrameKind.Incomplete) break;

            if (kind == FrameKind.Invalid)
            {
                _log($"{connection}: unframeable stream, closing");
                connection.Close();
                return;
            }

            var frame = payload.ToArray();
            buffer.Consume(consumed);

            if (kind == FrameKind.Ping)
            {
                await _handler.OnPingAsync(connection, PhotonTcpFraming.ReadPingTimestamp(frame), ct);
                continue;
            }

            if (!RtsFraming.TryReadHeader(frame, out var header))
            {
                _log($"{connection}: record with unrecognised message header, closing");
                connection.Close();
                return;
            }

            await _handler.OnMessageAsync(connection, header, frame.AsMemory(header.SizeInBytes), ct);
        }
    }

    public ValueTask OnDisconnectedAsync(RawConnection connection, Exception? error, CancellationToken ct)
    {
        _buffers.TryRemove(connection.Id, out _);
        return _handler.OnDisconnectedAsync(connection, error, ct);
    }
}

/// <summary>Growable buffer that holds a partially received stream.</summary>
sealed class StreamAccumulator
{
    byte[] _storage = new byte[8192];
    int _start;
    int _end;

    public ReadOnlySpan<byte> Buffered => _storage.AsSpan(_start, _end - _start);

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (_end + bytes.Length > _storage.Length) MakeRoom(bytes.Length);
        bytes.CopyTo(_storage.AsSpan(_end));
        _end += bytes.Length;
    }

    public void Consume(int count)
    {
        _start += count;
        if (_start == _end) _start = _end = 0;
    }

    void MakeRoom(int needed)
    {
        var live = _end - _start;

        if (_start > 0)
        {
            _storage.AsSpan(_start, live).CopyTo(_storage);
            _start = 0;
            _end = live;
            if (live + needed <= _storage.Length) return;
        }

        var capacity = _storage.Length;
        while (capacity < live + needed) capacity *= 2;

        var grown = new byte[capacity];
        _storage.AsSpan(_start, live).CopyTo(grown);
        _storage = grown;
        _start = 0;
        _end = live;
    }
}
