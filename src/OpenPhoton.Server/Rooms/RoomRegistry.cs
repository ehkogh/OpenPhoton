namespace OpenPhoton.Server.Rooms;

/// <summary>
/// Owns every room and serialises access to them.
/// </summary>
/// <remarks>
/// Room operations arrive on connection threads, so create/join/leave races are the normal
/// case rather than an edge case: two clients calling CreateGame with the same name at the
/// same instant must not both succeed. A single lock around the whole registry is coarse,
/// but room operations are infrequent compared with event relay - which does not take this
/// lock - so it is not the hot path. Per-room locking can come later if it ever matters.
/// </remarks>
public sealed class RoomRegistry(Func<string, RoomHooks?>? hooksFactory = null)
{
    readonly Dictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    readonly Func<string, RoomHooks?> _hooksFactory = hooksFactory ?? (_ => null);
    readonly object _gate = new();

    /// <summary>
    /// Builds the plugin for a newly created room, if one is configured.
    /// </summary>
    /// <remarks>
    /// Attached at creation rather than on first use: a plugin's OnCreateGame has to run
    /// before the creating actor is admitted, so the room cannot exist plugin-less even
    /// briefly.
    /// </remarks>
    public Func<Room, Plugins.PluginBridge?>? PluginFactory { get; set; }

    Room NewRoom(string name)
    {
        var room = new Room(name, _hooksFactory(name));
        room.Plugin = PluginFactory?.Invoke(room);
        return room;
    }

    public int Count { get { lock (_gate) return _rooms.Count; } }

    public IReadOnlyList<string> Names { get { lock (_gate) return [.. _rooms.Keys]; } }

    /// <summary>Creates a room, or fails if the name is taken.</summary>
    public bool TryCreate(string name, out Room room)
    {
        lock (_gate)
        {
            if (_rooms.TryGetValue(name, out var existing))
            {
                room = existing;
                return false;
            }

            room = NewRoom(name);
            _rooms[name] = room;
            return true;
        }
    }

    public bool TryGet(string name, out Room? room)
    {
        lock (_gate) return _rooms.TryGetValue(name, out room);
    }

    /// <summary>Gets an existing room or creates it, reporting which happened.</summary>
    public Room GetOrCreate(string name, out bool created)
    {
        lock (_gate)
        {
            if (_rooms.TryGetValue(name, out var existing))
            {
                created = false;
                return existing;
            }

            var room = NewRoom(name);
            _rooms[name] = room;
            created = true;
            return room;
        }
    }

    /// <summary>
    /// Removes an actor from its room, disposing the room once nothing holds it open.
    /// </summary>
    /// <returns>True when this departure closed the room, so its plugin can be told.</returns>
    public bool Leave(Room room, Actor actor, bool becomeInactive = false)
    {
        lock (_gate)
        {
            room.Leave(actor, becomeInactive);

            if (room.IsEmpty
                && !room.IsReserved
                && !room.HasReclaimableSlots
                && room.EmptyRoomTtl <= TimeSpan.Zero)
            {
                return _rooms.Remove(room.Name);
            }

            return false;
        }
    }

    /// <summary>
    /// Releases lapsed slots and drops rooms past their grace period.
    /// </summary>
    /// <remarks>
    /// Held slots and empty-room grace both expire by the clock rather than by an event, so
    /// something has to come looking. Without this a room whose players all dropped would
    /// hold its name forever.
    /// </remarks>
    public (int SlotsReleased, int RoomsClosed) SweepExpired() => SweepExpired(out _);

    /// <summary>As <see cref="SweepExpired()"/>, also handing back the rooms that closed.</summary>
    /// <remarks>
    /// The sweep runs on a timer with no peer attached, so a room can close with nobody
    /// around to notice. Returning the rooms is what lets the caller still run their plugins'
    /// close callbacks — otherwise a room that emptied and timed out would be collected
    /// silently and its plugin would never learn the game ended.
    /// </remarks>
    public (int SlotsReleased, int RoomsClosed) SweepExpired(out IReadOnlyList<Room> closedRooms)
    {
        lock (_gate)
        {
            var slots = 0;
            foreach (var room in _rooms.Values.ToList()) slots += room.ReleaseExpiredSlots().Count;

            var closed = _rooms.Values
                .Where(r => r.HasOutlivedItsGracePeriod || (r.IsEmpty && !r.IsReserved && !r.HasReclaimableSlots && r.EmptySince is null))
                .ToList();

            foreach (var room in closed) _rooms.Remove(room.Name);

            closedRooms = closed;
            return (slots, closed.Count);
        }
    }

    /// <summary>Total actors across every room. Used for lobby statistics.</summary>
    public int TotalActors
    {
        get { lock (_gate) return _rooms.Values.Sum(r => r.Actors.Count); }
    }

    /// <summary>Rooms currently worth showing in a lobby.</summary>
    public IReadOnlyList<Room> Listable()
    {
        lock (_gate) return [.. _rooms.Values.Where(r => r.IsListable)];
    }

    /// <summary>Picks a room for JoinRandomGame, or null when none will take a player.</summary>
    public Room? FindJoinable()
    {
        lock (_gate)
        {
            SweepExpiredReservations();

            return _rooms.Values
                .Where(r => r.IsJoinable && !r.IsEmpty)
                .OrderByDescending(r => r.Actors.Count)
                .FirstOrDefault();
        }
    }

    /// <summary>Drops empty rooms whose reservation lapsed without the client arriving.</summary>
    public int SweepExpiredReservations()
    {
        lock (_gate)
        {
            var stale = _rooms.Values
                .Where(r => r.IsEmpty && !r.IsReserved && r.ReservedUntil is not null)
                .Select(r => r.Name)
                .ToList();

            foreach (var name in stale) _rooms.Remove(name);
            return stale.Count;
        }
    }

    /// <summary>Drops a room if nobody is in it. Used to undo a creation nobody could enter.</summary>
    public bool RemoveIfEmpty(Room room)
    {
        lock (_gate)
        {
            if (!room.IsEmpty) return false;
            return _rooms.Remove(room.Name);
        }
    }

    /// <summary>Runs an action holding the registry lock, for multi-step room mutations.</summary>
    public T WithLock<T>(Func<T> action)
    {
        lock (_gate) return action();
    }
}
