using System.Collections;
using System.Collections.Concurrent;
using OpenPhoton.Realtime.Protocol;
using Photon.SocketServer;

namespace OpenPhoton.Server.Rooms;

/// <summary>
/// Publishes the room list to clients sitting in a lobby.
/// </summary>
/// <remarks>
/// <para>
/// A lobby client learns about rooms through two events carrying the same shape: an initial
/// <see cref="EventCode.GameList"/> on entry, then <see cref="EventCode.GameListUpdate"/>
/// as things change. Both hold a hashtable keyed by room name whose values are that room's
/// advertised properties, so a client can merge updates into what it already has.
/// </para>
/// <para>
/// A removal cannot be expressed by omission — an update is a merge, not a replacement — so
/// a vanished room is announced explicitly with <see cref="GamePropertyKey.Removed"/>.
/// Without that, closed rooms would linger in every lobby client's list forever.
/// </para>
/// </remarks>
public sealed class LobbyService(Action<string>? log = null)
{
    readonly ConcurrentDictionary<long, IPeerChannel> _inLobby = new();
    readonly Action<string> _log = log ?? (_ => { });

    public int Count => _inLobby.Count;

    public void Enter(IPeerChannel channel) => _inLobby[channel.Id] = channel;

    public void Leave(IPeerChannel channel) => _inLobby.TryRemove(channel.Id, out _);

    /// <summary>Sends the full room list to one client that has just entered a lobby.</summary>
    public async ValueTask SendFullListAsync(IPeerChannel channel, RoomRegistry rooms, CancellationToken ct)
    {
        var list = new Hashtable();
        foreach (var room in rooms.Listable()) list[room.Name] = Describe(room);

        await channel.SendEventAsync(new EventData(EventCode.GameList, new Dictionary<byte, object?>
        {
            [ParameterCode.GameList] = list,
        }), ct);
    }

    /// <summary>Tells every lobby client that one room changed or disappeared.</summary>
    public async ValueTask PublishAsync(Room room, bool removed, CancellationToken ct)
    {
        if (_inLobby.IsEmpty) return;

        if (!removed && !room.IsListable) return;

        var entry = removed
            ? new Hashtable { [GamePropertyKey.Removed] = true }
            : Describe(room);

        var payload = new EventData(EventCode.GameListUpdate, new Dictionary<byte, object?>
        {
            [ParameterCode.GameList] = new Hashtable { [room.Name] = entry },
        });

        foreach (var channel in _inLobby.Values.ToArray())
        {
            try
            {
                await channel.SendEventAsync(payload, ct);
            }
            catch (Exception ex)
            {
                _log($"    lobby update to #{channel.Id} failed: {ex.GetType().Name}");
                _inLobby.TryRemove(channel.Id, out _);
            }
        }
    }

    /// <summary>
    /// The properties a room advertises to a lobby.
    /// </summary>
    /// <remarks>
    /// Only the well-known keys plus whatever the room nominated through
    /// <see cref="GamePropertyKey.PropsListedInLobby"/>. Rooms routinely hold state that is
    /// none of the lobby's business, so exposing the whole property bag would leak it to
    /// every client and bloat an event sent to everyone on every change.
    /// </remarks>
    public static Hashtable Describe(Room room)
    {
        var described = new Hashtable
        {
            [GamePropertyKey.PlayerCount] = room.Actors.Count,
            [GamePropertyKey.MaxPlayers] = (byte)Math.Min(room.MaxPlayers, byte.MaxValue),
            [GamePropertyKey.IsOpen] = room.IsOpen,
            [GamePropertyKey.IsVisible] = room.IsVisible,
        };

        if (room.Properties[GamePropertyKey.PropsListedInLobby] is not string[] listed) return described;

        foreach (var key in listed)
            if (room.Properties.ContainsKey(key))
                described[key] = room.Properties[key];

        return described;
    }
}
