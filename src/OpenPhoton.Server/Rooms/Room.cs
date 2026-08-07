using System.Collections;
using Photon.SocketServer;

namespace OpenPhoton.Server.Rooms;

/// <summary>How the server reaches one connected peer.</summary>
/// <remarks>
/// Rooms push events to peers, but must not depend on the transport to do it. This is the
/// whole surface a room needs, which keeps the room model testable without sockets.
/// </remarks>
public interface IPeerChannel
{
    long Id { get; }

    ValueTask SendEventAsync(EventData eventData, CancellationToken ct);

    /// <summary>
    /// Drops the connection.
    /// </summary>
    /// <remarks>
    /// Needed so a plugin can eject an actor. Routing a kick through a normal disconnect
    /// means the actor is removed and everyone told by the same path as any other
    /// departure, rather than through a second removal mechanism that could drift from it.
    /// </remarks>
    void Close();
}

/// <summary>One participant in a room.</summary>
public sealed class Actor(int actorNumber, string userId, IPeerChannel channel)
{
    /// <summary>1-based and never reused within a room's lifetime, matching Photon.</summary>
    public int ActorNumber { get; } = actorNumber;

    public string UserId { get; } = userId;

    public IPeerChannel Channel { get; private set; } = channel;

    /// <summary>
    /// True while the actor's slot is held open for them to come back to.
    /// </summary>
    /// <remarks>
    /// An inactive actor still occupies its slot and keeps its properties and actor number,
    /// which is the entire point: a client that drops and reconnects returns as the same
    /// player rather than as a stranger who happens to share a name.
    /// </remarks>
    public bool IsInactive { get; private set; }

    /// <summary>When the slot stops being held. Null while active.</summary>
    public DateTime? ReclaimableUntil { get; private set; }

    public bool ReclaimWindowExpired => IsInactive && ReclaimableUntil <= DateTime.UtcNow;

    public void Deactivate(TimeSpan window)
    {
        IsInactive = true;
        ReclaimableUntil = DateTime.UtcNow + window;
    }

    /// <summary>Rebinds the slot to a returning client's new connection.</summary>
    public void Reactivate(IPeerChannel channel)
    {
        Channel = channel;
        IsInactive = false;
        ReclaimableUntil = null;
    }

    /// <summary>Per-player properties. Keys may be well-known bytes or arbitrary strings.</summary>
    public Hashtable Properties { get; } = [];

    /// <summary>
    /// Interest groups this actor listens to, beyond group 0.
    /// </summary>
    /// <remarks>
    /// Group 0 always reaches everyone and is never stored here. Any other group is opt-in:
    /// an event addressed to group 3 reaches only actors that asked for 3, which is how a
    /// room keeps distant players' traffic off a client that cannot see them.
    /// </remarks>
    public HashSet<byte> InterestGroups { get; } = [];

    /// <summary>True when an event for <paramref name="group"/> should reach this actor.</summary>
    public bool ListensTo(byte group) => group == 0 || InterestGroups.Contains(group);

    public override string ToString() => $"actor {ActorNumber} ({UserId})";
}

/// <summary>Outcome of a hook that can veto an operation.</summary>
public readonly record struct Decision(bool Allowed, short ErrorCode, string? Message)
{
    public static Decision Allow { get; } = new(true, 0, null);

    public static Decision Deny(short errorCode, string message) => new(false, errorCode, message);
}

/// <summary>
/// Extension points on the room lifecycle.
/// </summary>
/// <remarks>
/// These deliberately mirror the shape of Photon's plugin callbacks — a veto-capable
/// "before" phase and a notification "on" phase — so that the eventual
/// <c>PhotonHivePlugin</c> surface can be layered on top rather than forcing the room model
/// to be rewritten around it. No plugin loading exists yet; this is only the seam.
/// </remarks>
public abstract class RoomHooks
{
    public virtual Decision BeforeJoin(Room room, string userId) => Decision.Allow;

    public virtual void OnJoin(Room room, Actor actor) { }

    public virtual void OnLeave(Room room, Actor actor) { }

    public virtual Decision BeforeRaiseEvent(Room room, Actor sender, byte eventCode, object? data) => Decision.Allow;

    public virtual Decision BeforeSetProperties(Room room, Actor actor, Hashtable properties) => Decision.Allow;

    public virtual void OnClose(Room room) { }
}

/// <summary>A game room and the actors in it.</summary>
/// <remarks>
/// Not thread safe by itself; callers serialise access through <see cref="RoomRegistry"/>.
/// </remarks>
public sealed class Room(string name, RoomHooks? hooks = null)
{
    readonly List<Actor> _actors = [];
    readonly RoomHooks _hooks = hooks ?? NullHooks.Instance;

    int _nextActorNumber = 1;

    public string Name { get; } = name;

    /// <summary>
    /// The room's plugin, if one is loaded. Null means no plugin, not a no-op plugin.
    /// </summary>
    /// <remarks>
    /// Deliberately typed as the bridge rather than <c>IGamePlugin</c>: room code never
    /// touches a plugin type directly, which is what keeps the plugin contract on one side
    /// of a single seam.
    /// </remarks>
    public Plugins.PluginBridge? Plugin { get; set; }

    public Hashtable Properties { get; } = [];

    /// <summary>Everyone in the room, including slots held for absent players.</summary>
    public IReadOnlyList<Actor> Actors => _actors;

    /// <summary>Only actors currently connected. Events go to these.</summary>
    public IEnumerable<Actor> ActiveActors => _actors.Where(a => !a.IsInactive);

    /// <summary>
    /// True when nobody is connected. Held slots do not count.
    /// </summary>
    /// <remarks>
    /// A room whose players have all dropped is empty for the purpose of shutting it down,
    /// but its held slots still need to survive long enough to be reclaimed — that tension
    /// is what <see cref="EmptyRoomTtl"/> resolves.
    /// </remarks>
    public bool IsEmpty => !_actors.Any(a => !a.IsInactive);

    public bool HasReclaimableSlots => _actors.Any(a => a.IsInactive && !a.ReclaimWindowExpired);

    /// <summary>Lowest connected actor number. Photon promotes the next lowest when one leaves.</summary>
    public int MasterClientId
    {
        get
        {
            var active = ActiveActors.ToList();
            return active.Count == 0 ? 0 : active.Min(a => a.ActorNumber);
        }
    }

    /// <summary>
    /// How long a departing player's slot is held. Zero means slots are never held.
    /// </summary>
    public TimeSpan PlayerTtl { get; set; }

    /// <summary>How long the room survives with nobody connected.</summary>
    public TimeSpan EmptyRoomTtl { get; set; }

    /// <summary>Set when the room became empty, so its grace period can be measured.</summary>
    public DateTime? EmptySince { get; set; }

    public int MaxPlayers { get; set; }

    public bool IsOpen { get; set; } = true;

    /// <summary>
    /// While set, an empty room is kept alive waiting for the client the master sent here.
    /// </summary>
    /// <remarks>
    /// The master reserves a name and then the client disconnects and reconnects to the
    /// game server, so the room is legitimately empty for a moment. Without a reservation
    /// it would be swept before its creator arrived; without an expiry it would leak every
    /// time a client was told to create a room and then never showed up.
    /// </remarks>
    public DateTime? ReservedUntil { get; private set; }

    public bool IsReserved => ReservedUntil > DateTime.UtcNow;

    /// <summary>
    /// Whether any actor has ever entered. False for a room that only exists as a
    /// name reservation.
    /// </summary>
    /// <remarks>
    /// This is what identifies the creating actor, and it is not the same question
    /// as whether the room object was just constructed. The master reserves a room
    /// by name before sending the client to the game server, so by the time the
    /// first client arrives the room already exists — and a server that equates
    /// "room is new" with "actor is the creator" then treats everybody as a joiner
    /// and never runs a plugin's OnCreateGame at all.
    /// </remarks>
    public bool HasBeenEntered { get; private set; }

    public void Reserve(TimeSpan? window = null) =>
        ReservedUntil = DateTime.UtcNow + (window ?? TimeSpan.FromSeconds(30));

    public void ClearReservation() => ReservedUntil = null;

    /// <summary>Whether the room appears in lobby listings. Hidden rooms are still joinable by name.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>True when a random-match search should consider this room.</summary>
    public bool IsJoinable => IsOpen && (MaxPlayers == 0 || _actors.Count < MaxPlayers);

    /// <summary>True when the room should be advertised to lobby clients.</summary>
    public bool IsListable => IsVisible && !IsEmpty;

    public Actor? Find(int actorNumber) => _actors.FirstOrDefault(a => a.ActorNumber == actorNumber);

    public Actor? FindByChannel(long channelId) => _actors.FirstOrDefault(a => a.Channel.Id == channelId);

    /// <summary>Actor numbers reported to clients, held slots included so returning players keep their place.</summary>
    public int[] ActorNumbers => [.. _actors.Select(a => a.ActorNumber)];

    /// <summary>Validates a join, then admits the actor. Returns null when the decision denies it.</summary>
    public Actor? TryJoin(string userId, IPeerChannel channel, Hashtable? properties, out Decision decision)
    {
        if (!IsOpen)
        {
            decision = Decision.Deny(Realtime.Protocol.ErrorCode.GameClosed, $"Room '{Name}' is closed.");
            return null;
        }

        if (MaxPlayers > 0 && _actors.Count >= MaxPlayers)
        {
            decision = Decision.Deny(Realtime.Protocol.ErrorCode.GameFull, $"Room '{Name}' is full.");
            return null;
        }

        decision = _hooks.BeforeJoin(this, userId);
        if (!decision.Allowed) return null;

        ClearReservation();

        var actor = new Actor(_nextActorNumber++, userId, channel);
        if (properties is not null)
            foreach (DictionaryEntry entry in properties)
                actor.Properties[entry.Key] = entry.Value;

        _actors.Add(actor);
        HasBeenEntered = true;
        _hooks.OnJoin(this, actor);

        decision = Decision.Allow;
        return actor;
    }

    /// <summary>Finds a held slot this user may reclaim.</summary>
    public Actor? FindReclaimable(string userId) =>
        _actors.FirstOrDefault(a => a.IsInactive && !a.ReclaimWindowExpired && a.UserId == userId);

    /// <summary>Returns a player to their existing slot, keeping their actor number and properties.</summary>
    public Actor Reclaim(Actor slot, IPeerChannel channel)
    {
        slot.Reactivate(channel);
        EmptySince = null;
        _hooks.OnJoin(this, slot);
        return slot;
    }

    /// <summary>
    /// Removes an actor, or holds their slot open when the room keeps slots.
    /// </summary>
    /// <param name="becomeInactive">
    /// Whether the client asked to be able to come back. A client that leaves deliberately
    /// usually does not, while a dropped connection always should.
    /// </param>
    public void Leave(Actor actor, bool becomeInactive = false)
    {
        if (!_actors.Contains(actor)) return;

        if (becomeInactive && PlayerTtl > TimeSpan.Zero)
        {
            actor.Deactivate(PlayerTtl);
        }
        else
        {
            _actors.Remove(actor);
        }

        _hooks.OnLeave(this, actor);

        if (IsEmpty)
        {
            EmptySince = DateTime.UtcNow;
            if (!HasReclaimableSlots) _hooks.OnClose(this);
        }
    }

    /// <summary>Drops held slots whose reclaim window has passed. Returns those released.</summary>
    public IReadOnlyList<Actor> ReleaseExpiredSlots()
    {
        var expired = _actors.Where(a => a.ReclaimWindowExpired).ToList();
        foreach (var actor in expired) _actors.Remove(actor);
        return expired;
    }

    /// <summary>True when the room has been empty longer than it is allowed to linger.</summary>
    public bool HasOutlivedItsGracePeriod =>
        IsEmpty && !HasReclaimableSlots && EmptySince is { } since && DateTime.UtcNow - since > EmptyRoomTtl;

    public Decision CheckRaiseEvent(Actor sender, byte eventCode, object? data) =>
        _hooks.BeforeRaiseEvent(this, sender, eventCode, data);

    readonly List<CachedEvent> _cache = [];

    /// <summary>An event replayed to clients that join later.</summary>
    public sealed record CachedEvent(int ActorNumber, byte EventCode, object? Data);

    /// <summary>Cached events, in the order they were raised.</summary>
    /// <remarks>
    /// Order is the point. The cache reconstructs history for a late joiner, and replaying
    /// a spawn after the move that followed it leaves that client disagreeing with everyone
    /// else about where things are.
    /// </remarks>
    public IReadOnlyList<CachedEvent> CachedEvents => _cache;

    public void CacheEvent(int actorNumber, byte eventCode, object? data) =>
        _cache.Add(new CachedEvent(actorNumber, eventCode, data));

    /// <summary>Removes cached events with this code raised by this actor.</summary>
    public int RemoveCached(int actorNumber, byte eventCode) =>
        _cache.RemoveAll(e => e.EventCode == eventCode && e.ActorNumber == actorNumber);

    /// <summary>Drops everything cached by actors no longer present, keeping actor 0's globals.</summary>
    public int RemoveCachedForAbsentActors() =>
        _cache.RemoveAll(e => e.ActorNumber != 0 && Find(e.ActorNumber) is null);

    /// <summary>Drops everything this actor cached. Used when they leave.</summary>
    public int RemoveCachedFor(int actorNumber) => _cache.RemoveAll(e => e.ActorNumber == actorNumber);

    public Decision CheckSetProperties(Actor actor, Hashtable properties) =>
        _hooks.BeforeSetProperties(this, actor, properties);

    /// <summary>
    /// Whether an actor's cached events are discarded when they leave.
    /// </summary>
    /// <remarks>
    /// Defaults on, matching Photon: an actor's cached events usually describe things they
    /// own, and replaying them to a later joiner would resurrect objects belonging to
    /// somebody who is no longer here. Rooms wanting persistent history clear this.
    /// </remarks>
    public bool CleanupCacheOnLeave { get; set; } = true;

    /// <summary>Applies a property update. A null value removes the key, as Photon does.</summary>
    public static void Merge(Hashtable target, Hashtable update)
    {
        foreach (DictionaryEntry entry in update)
        {
            if (entry.Value is null) target.Remove(entry.Key);
            else target[entry.Key] = entry.Value;
        }
    }

    sealed class NullHooks : RoomHooks
    {
        public static NullHooks Instance { get; } = new();
    }
}
