using System.Collections;
using OpenPhoton.Realtime.Protocol;
using OpenPhoton.Server.Plugins;
using OpenPhoton.Server.Rooms;
using Photon.SocketServer;

namespace OpenPhoton.Server;

/// <summary>
/// Room lifecycle operations: create, join, leave, raise event, properties.
/// </summary>
/// <remarks>
/// Each handler runs the same three phases in order â€” validate, mutate, notify. Keeping
/// them separate is what makes the eventual plugin callbacks droppable in: a plugin's
/// "before" hook belongs at the end of validate, its "on" hook at the start of notify.
/// Interleaving them would make that retrofit a rewrite.
/// </remarks>
public sealed class RoomOperations(RoomRegistry rooms, LobbyService lobby, Action<string> log,
                                   OperationalLimits? limits = null)
{
    readonly RoomRegistry _rooms = rooms;
    readonly LobbyService _lobby = lobby;
    readonly Action<string> _log = log;
    readonly OperationalLimits _limits = limits ?? OperationalLimits.Default;

    public void Register(OperationDispatcher dispatcher)
    {
        dispatcher.Register(OperationCode.CreateGame, CreateGameAsync);
        dispatcher.Register(OperationCode.JoinGame, JoinGameAsync);
        dispatcher.Register(OperationCode.Leave, LeaveAsync);
        dispatcher.Register(OperationCode.RaiseEvent, RaiseEventAsync);
        dispatcher.Register(OperationCode.SetProperties, SetPropertiesAsync);
        dispatcher.Register(OperationCode.GetProperties, GetPropertiesAsync);
        dispatcher.Register(OperationCode.ChangeGroups, ChangeGroupsAsync);
    }

    /// <summary>
    /// Updates which interest groups an actor listens to.
    /// </summary>
    /// <remarks>
    /// The two parameters follow Photon's null-versus-empty convention, which is easy to get
    /// backwards: an <em>absent</em> array means "change nothing", while an <em>empty</em>
    /// array means "all groups". So an empty remove-list unsubscribes from everything, and
    /// an empty add-list subscribes to everything.
    /// </remarks>
    ValueTask<OperationResponse?> ChangeGroupsAsync(PeerState peer, OperationRequest request)
    {
        var actor = peer.CurrentActor;
        if (actor is null)
            return ValueTask.FromResult<OperationResponse?>(
                Fail(request, ErrorCode.OperationNotAllowedInCurrentState, "Not in a room."));

        if (Get(request, ParameterCode.Remove) is byte[] remove)
        {
            if (remove.Length == 0) actor.InterestGroups.Clear();
            else foreach (var group in remove) actor.InterestGroups.Remove(group);
        }

        if (Get(request, ParameterCode.Add) is byte[] add)
        {
            if (add.Length == 0)
            {
                for (var group = 1; group <= byte.MaxValue; group++) actor.InterestGroups.Add((byte)group);
            }
            else
            {
                foreach (var group in add) if (group != 0) actor.InterestGroups.Add(group);
            }
        }

        _log($"    {actor} listens to {actor.InterestGroups.Count} group(s)");
        return ValueTask.FromResult<OperationResponse?>(OperationDispatcher.NoResponse);
    }

    /// <summary>
    /// CreateGame on the game server: enter the room, creating it only if needed.
    /// </summary>
    /// <remarks>
    /// The name was already claimed on the master, which reserved an empty room and sent
    /// the client here to repeat the same operation. So unlike the master's version this
    /// must not treat an existing room as a conflict â€” the room it finds is almost always
    /// the one reserved moments ago for this very client. Rejecting it with
    /// GameIdAlreadyExists strands every client that follows the normal handoff.
    /// </remarks>
    async ValueTask<OperationResponse?> CreateGameAsync(PeerState peer, OperationRequest request)
    {
        var name = GetString(request, ParameterCode.RoomName) ?? Guid.NewGuid().ToString("N");

        var room = _rooms.GetOrCreate(name, out var created);
        return await EnterAsync(peer, request, room, created);
    }

    async ValueTask<OperationResponse?> JoinGameAsync(PeerState peer, OperationRequest request)
    {
        var name = GetString(request, ParameterCode.RoomName);
        if (string.IsNullOrEmpty(name))
            return Fail(request, ErrorCode.InvalidOperation, "JoinGame requires a room name.");

        if (_rooms.TryGet(name, out var existing) && existing is not null)
            return await EnterAsync(peer, request, existing, created: false);

        if (JoinModeOf(request) != JoinMode.CreateIfNotExists)
            return Fail(request, ErrorCode.GameDoesNotExist, $"Room '{name}' does not exist.");

        var room = _rooms.GetOrCreate(name, out var created);
        return await EnterAsync(peer, request, room, created);
    }

    static JoinMode JoinModeOf(OperationRequest request) =>
        request.Parameters is not null
        && request.Parameters.TryGetValue(ParameterCode.JoinMode, out var mode)
        && mode is byte value
            ? (JoinMode)value
            : JoinMode.Default;

    /// <summary>
    /// Whether a room is a client-side local instance rather than a hosted one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Clients name these <c>local:error_&lt;n&gt;</c> and similar. They are the client's own
    /// offline worlds â€” most importantly the error world it falls back to when a real world
    /// fails to load â€” and the client still opens a Photon connection for them.
    /// </para>
    /// <para>
    /// A local instance has no instance token by definition: nothing issued one, because the
    /// API was never asked to place the player anywhere. Running the plugin's authorisation
    /// over it therefore always fails, and the failure replaces whatever the original problem
    /// was with "Invalid JWT presented" â€” so the player is told their credentials are bad
    /// when in fact a world would not download. Worse, the client cannot even reach the error
    /// world to display that, so it retries and eventually gives up.
    /// </para>
    /// <para>
    /// These rooms are still served normally; only the plugin gate is skipped.
    /// </para>
    /// </remarks>
    static bool IsLocalInstance(string roomName) =>
        roomName.StartsWith("local:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The plugin that should see this room's traffic, or null where it must not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every plugin callback goes through here rather than reading <see cref="Room.Plugin"/>
    /// and testing the room name alongside it. Written out at each site, the test was applied
    /// to joining, leaving and closing but not to raising events or setting properties — and
    /// a partial bypass is worse than none at all. The plugin never saw the actor arrive, so
    /// the first event it was asked about named an actor it had no record of; the reference plugin
    /// answers that with an exception, the operation fails, and the player is ejected from
    /// the error world they were already in.
    /// </para>
    /// <para>
    /// One accessor means adding a callback cannot reintroduce that: there is nowhere to
    /// forget the rule.
    /// </para>
    /// </remarks>
    static PluginBridge? ActivePlugin(Room room) =>
        IsLocalInstance(room.Name) ? null : room.Plugin;

    async ValueTask<OperationResponse?> EnterAsync(PeerState peer, OperationRequest request, Room room, bool created)
    {
        if (peer.Channel is null)
            return Fail(request, ErrorCode.InternalServerError, "Peer has no channel.");

        if (peer.CurrentRoom is not null)
            return Fail(request, ErrorCode.OperationNotAllowedInCurrentState, "Already in a room.");

        var properties = GetHashtable(request, ParameterCode.PlayerProperties);
        var userId = peer.UserId ?? "anonymous";
        var joinMode = JoinModeOf(request);

        var decision = Decision.Allow;
        var rejoined = false;

        var isCreator = false;

        var actor = _rooms.WithLock(() =>
        {
            isCreator = !room.HasBeenEntered;
            room.ReleaseExpiredSlots();

            if (isCreator && joinMode != JoinMode.RejoinOnly)
            {
                var admitted = room.TryJoin(userId, peer.Channel, properties, out var creatorWhy);
                decision = creatorWhy;

                if (admitted is not null
                    && (joinMode == JoinMode.CreateIfNotExists
                        || request.OperationCode == OperationCode.CreateGame))
                {
                    ApplyRoomOptions(room, request);
                }

                return admitted;
            }

            if (joinMode is JoinMode.JoinOrRejoin or JoinMode.RejoinOnly
                && room.FindReclaimable(userId) is { } slot)
            {
                rejoined = true;
                return room.Reclaim(slot, peer.Channel);
            }

            if (joinMode == JoinMode.RejoinOnly)
            {
                decision = Decision.Deny(ErrorCode.JoinFailedWithRejoinerNotFound,
                    $"No slot is being held for '{userId}' in '{room.Name}'.");
                return null;
            }

            var joined = room.TryJoin(userId, peer.Channel, properties, out var why);
            decision = why;
            return joined;
        });

        _rooms.WithLock<object?>(() => { SyncMasterClient(room); return null; });

        if (actor is null)
        {
            if (created) _rooms.RemoveIfEmpty(room);

            return Fail(request, decision.ErrorCode, decision.Message ?? $"Could not join '{room.Name}'.");
        }

        var publishJoinEvents = true;
        var publishCache = true;

        if (ActivePlugin(room) is { } gate)
        {
            gate.Hold();

            PluginBridge.Verdict verdict;

            if (isCreator)
            {
                verdict = gate.OnCreateGame(request, room.Name, userId, userId,
                                            isJoin: request.OperationCode == OperationCode.JoinGame);
            }
            else
            {
                verdict = gate.BeforeJoin(request, room.Name, userId, userId);

                if (verdict.Allowed)
                {
                    verdict = gate.OnJoin(request, room.Name, userId, userId, actor.ActorNumber,
                                          out publishJoinEvents, out publishCache);
                }
            }

            if (!verdict.Allowed)
            {
                gate.Release();
                await gate.FlushAsync(CancellationToken.None);

                _rooms.WithLock<object?>(() => { room.Leave(actor); return null; });
                if (created) _rooms.RemoveIfEmpty(room);

                peer.CurrentRoom = null;
                peer.CurrentActor = null;

                return Fail(request,
                    verdict.ErrorCode == 0 ? ErrorCode.PluginReportedError : verdict.ErrorCode,
                    verdict.Message ?? "Rejected by plugin.");
            }

            if (GetHashtable(request, ParameterCode.PlayerProperties) is { } rewritten)
            {
                actor.Properties.Clear();
                foreach (DictionaryEntry entry in rewritten) actor.Properties[entry.Key] = entry.Value;
            }
        }

        peer.CurrentRoom = room;
        peer.CurrentActor = actor;
        _log($"    {actor} {(rejoined ? "rejoined" : isCreator ? "created" : "joined")} "
           + $"'{room.Name}' ({room.ActiveActors.Count()} present)");

        var joinEvent = new EventData(EventCode.Join, new Dictionary<byte, object?>
        {
            [ParameterCode.ActorNr] = actor.ActorNumber,
            [ParameterCode.PlayerProperties] = actor.Properties,
            [ParameterCode.ActorList] = room.ActorNumbers,
        });

        peer.AfterResponse = async ct =>
        {
            if (publishJoinEvents) await BroadcastAsync(room, except: null, joinEvent);

            if (ActivePlugin(room) is { } joined)
            {
                joined.Release();
                await joined.FlushAsync(ct);
            }

            if (publishCache)
            {
                foreach (var cached in room.CachedEvents.ToArray())
                {
                    await actor.Channel.SendEventAsync(new EventData(cached.EventCode, new Dictionary<byte, object?>
                    {
                        [ParameterCode.Data] = cached.Data,
                        [ParameterCode.ActorNr] = cached.ActorNumber,
                    }), ct);
                }
            }

            await _lobby.PublishAsync(room, removed: false, ct);
        };

        return new OperationResponse(request.OperationCode, new Dictionary<byte, object?>
        {
            [ParameterCode.ActorNr] = actor.ActorNumber,
            [ParameterCode.GameProperties] = room.Properties,
            [ParameterCode.PlayerProperties] = PlayerPropertiesOf(room),
            [ParameterCode.ActorList] = room.ActorNumbers,
        });
    }

    void ApplyRoomOptions(Room room, OperationRequest request)
    {
        var properties = GetHashtable(request, ParameterCode.GameProperties);
        if (properties is not null) Room.Merge(room.Properties, properties);

        if (GetInt(request, ParameterCode.PlayerTTL) is { } playerTtl)
            room.Properties[GamePropertyKey.PlayerTtl] = playerTtl;

        if (GetInt(request, ParameterCode.EmptyRoomTTL) is { } emptyTtl)
            room.Properties[GamePropertyKey.EmptyRoomTtl] = emptyTtl;

        if (Get(request, ParameterCode.CleanupCacheOnLeave) is bool cleanup)
            room.Properties[GamePropertyKey.CleanupCacheOnLeave] = cleanup;

        RefreshRoomOptions(room);
    }

    /// <summary>Re-reads the well-known room properties that have behaviour attached.</summary>
    /// <remarks>
    /// Capacity, visibility and the TTLs all live in the property bag, so a client can
    /// change them with SetProperties rather than only at creation. Re-deriving after any
    /// property write keeps the room's behaviour and its advertised properties from
    /// drifting apart.
    /// </remarks>
    void RefreshRoomOptions(Room room)
    {
        if (room.Properties[GamePropertyKey.MaxPlayers] is byte maxByte) room.MaxPlayers = maxByte;
        if (room.Properties[GamePropertyKey.MaxPlayersInt] is int maxInt) room.MaxPlayers = maxInt;
        if (room.Properties[GamePropertyKey.IsOpen] is bool isOpen) room.IsOpen = isOpen;
        if (room.Properties[GamePropertyKey.IsVisible] is bool isVisible) room.IsVisible = isVisible;
        if (room.Properties[GamePropertyKey.CleanupCacheOnLeave] is bool cleanup) room.CleanupCacheOnLeave = cleanup;

        if (room.Properties[GamePropertyKey.PlayerTtl] is int playerTtl)
        {
            room.PlayerTtl = _limits.ClampPlayerTtl(
                playerTtl < 0 ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(playerTtl));
        }

        if (room.Properties[GamePropertyKey.EmptyRoomTtl] is int emptyTtl)
        {
            room.EmptyRoomTtl = _limits.ClampEmptyRoomTtl(
                emptyTtl < 0 ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(emptyTtl));
        }
    }

    /// <summary>Every actor's properties, keyed by actor number, as the client expects on entry.</summary>
    static Hashtable PlayerPropertiesOf(Room room)
    {
        var all = new Hashtable();
        foreach (var a in room.Actors) all[a.ActorNumber] = a.Properties;
        return all;
    }

    async ValueTask<OperationResponse?> LeaveAsync(PeerState peer, OperationRequest request)
    {
        var room = peer.CurrentRoom;
        var actor = peer.CurrentActor;
        if (room is null || actor is null)
            return Fail(request, ErrorCode.OperationNotAllowedInCurrentState, "Not in a room.");

        var becomeInactive = Get(request, ParameterCode.IsInactive) is true;

        await RemoveAsync(peer, becomeInactive, Photon.Hive.Plugin.LeaveReason.LeaveRequest, request);
        return new OperationResponse(request.OperationCode);
    }

    /// <summary>
    /// Removes a peer from its room and tells the remaining actors. Safe to call twice.
    /// </summary>
    /// <param name="becomeInactive">
    /// Hold the slot for a later rejoin. A dropped connection passes true: the player did
    /// not choose to leave, and a room with PlayerTtl set expects them back.
    /// </param>
    /// <param name="reason">
    /// A <c>LeaveReason</c> value. Plugins branch on it â€” a disconnect and a deliberate exit
    /// mean different things to a game that tracks who rage-quit.
    /// </param>
    /// <param name="request">
    /// The originating Leave operation, when there was one. A departure caused by a dropped
    /// connection has no request, so the plugin is handed a synthetic one.
    /// </param>
    public async ValueTask RemoveAsync(PeerState peer, bool becomeInactive = false,
                                       byte reason = Photon.Hive.Plugin.LeaveReason.ClientDisconnect,
                                       OperationRequest? request = null)
    {
        var room = peer.CurrentRoom;
        var actor = peer.CurrentActor;
        if (room is null || actor is null) return;

        peer.CurrentRoom = null;
        peer.CurrentActor = null;

        var holdingSlot = becomeInactive && room.PlayerTtl > TimeSpan.Zero;

        if (ActivePlugin(room) is { } plugin)
        {
            plugin.OnLeave(request ?? new OperationRequest(OperationCode.Leave),
                           actor.UserId, actor.UserId, actor.ActorNumber, holdingSlot, reason);
            await plugin.FlushAsync(CancellationToken.None);
        }

        if (room.CleanupCacheOnLeave && !holdingSlot)
        {
            var dropped = room.RemoveCachedFor(actor.ActorNumber);
            if (dropped > 0) _log($"    dropped {dropped} cached event(s) owned by {actor}");
        }

        var closed = _rooms.Leave(room, actor, becomeInactive);
        _log($"    {actor} {(holdingSlot ? "went inactive in" : "left")} "
           + $"'{room.Name}' ({room.ActiveActors.Count()} remain)");

        var promoted = _rooms.WithLock(() => SyncMasterClient(room));

        await BroadcastAsync(room, except: null, new EventData(EventCode.Leave, new Dictionary<byte, object?>
        {
            [ParameterCode.ActorNr] = actor.ActorNumber,
            [ParameterCode.ActorList] = room.ActorNumbers,
            [ParameterCode.IsInactive] = holdingSlot,
            [ParameterCode.MasterClientId] = room.MasterClientId,
        }));

        if (promoted && !room.IsEmpty)
        {
            await BroadcastAsync(room, except: null, new EventData(EventCode.PropertiesChanged,
                new Dictionary<byte, object?>
                {
                    [ParameterCode.Properties] = new Hashtable
                    {
                        [GamePropertyKey.MasterClientId] = room.MasterClientId,
                    },
                    [ParameterCode.TargetActorNr] = 0,
                }));
        }

        await _lobby.PublishAsync(room, removed: room.IsEmpty, CancellationToken.None);

        if (closed) await CloseAsync(room);
    }

    /// <summary>
    /// Tells a room's plugin the game is over.
    /// </summary>
    /// <remarks>
    /// Both callbacks run and the verdict of neither is honoured, because by this point the
    /// room is already out of the registry: there is no longer anything for a plugin to
    /// refuse. They are still both invoked in order, since a plugin that persists state does
    /// it in <c>BeforeCloseGame</c> and expects <c>OnCloseGame</c> to follow.
    /// </remarks>
    public async ValueTask CloseAsync(Room room)
    {
        if (room.Plugin is not { } plugin) return;

        if (IsLocalInstance(room.Name))
        {
            room.Plugin = null;
            await plugin.DisposeAsync();
            return;
        }

        var actorCount = room.Actors.Count;

        plugin.BeforeCloseGame();
        await plugin.FlushAsync(CancellationToken.None);

        plugin.OnCloseGame(actorCount);
        await plugin.FlushAsync(CancellationToken.None);

        room.Plugin = null;
        await plugin.DisposeAsync();

        _log($"    closed '{room.Name}'");
    }

    /// <summary>Runs the registry sweep and closes the plugins of any rooms it collected.</summary>
    /// <remarks>
    /// Wrapped rather than left to callers: a room that times out empty closes with no peer
    /// involved, so nothing else is positioned to notice it needs its plugin told.
    /// </remarks>
    public async ValueTask<(int SlotsReleased, int RoomsClosed)> SweepAsync()
    {
        var result = _rooms.SweepExpired(out var closedRooms);
        foreach (var room in closedRooms) await CloseAsync(room);
        return result;
    }

    async ValueTask<OperationResponse?> RaiseEventAsync(PeerState peer, OperationRequest request)
    {
        var room = peer.CurrentRoom;
        var sender = peer.CurrentActor;
        if (room is null || sender is null)
            return Fail(request, ErrorCode.OperationNotAllowedInCurrentState, "Not in a room.");

        var eventCode = GetByte(request, ParameterCode.Code) ?? 0;
        var data = Get(request, ParameterCode.Data);

        var decision = room.CheckRaiseEvent(sender, eventCode, data);
        if (!decision.Allowed) return Fail(request, decision.ErrorCode, decision.Message ?? "Rejected.");

        if (ActivePlugin(room) is { } plugin)
        {
            var verdict = plugin.OnRaiseEvent(request, sender.UserId, sender.UserId, sender.ActorNumber);

            await plugin.FlushAsync(CancellationToken.None);

            if (verdict.Cancelled) return new OperationResponse(request.OperationCode);
            if (!verdict.Allowed) return Fail(request, verdict.ErrorCode, verdict.Message ?? "Rejected by plugin.");

            eventCode = GetByte(request, ParameterCode.Code) ?? eventCode;
            data = Get(request, ParameterCode.Data);
        }

        var caching = (EventCaching)(GetByte(request, ParameterCode.Cache) ?? (byte)EventCaching.DoNotCache);
        if (ApplyCaching(room, sender, eventCode, data, caching))
        {
            return new OperationResponse(request.OperationCode);
        }

        var payload = new EventData(eventCode, new Dictionary<byte, object?>
        {
            [ParameterCode.Data] = data,
            [ParameterCode.ActorNr] = sender.ActorNumber,
        });

        var group = (ReceiverGroup)(GetByte(request, ParameterCode.ReceiverGroup) ?? (byte)ReceiverGroup.Others);
        var interestGroup = GetByte(request, ParameterCode.Group) ?? 0;

        var targets = Get(request, ParameterCode.ActorList);

        if (targets is int[] explicitTargets)
        {
            var sent = new List<int>();

            foreach (var number in explicitTargets)
            {
                if (room.Find(number) is not { } target) continue;

                if (target.IsInactive)
                {
                    _log($"    [ev {eventCode}] to actor {number} skipped: slot is inactive");
                    continue;
                }

                try
                {
                    await target.Channel.SendEventAsync(payload, CancellationToken.None);
                    sent.Add(number);
                }
                catch (Exception ex)
                {
                    _log($"    send to {target} failed: {ex.GetType().Name}");
                }
            }

            if (sent.Count > 0) _log($"    [ev {eventCode}] -> actor {string.Join(", ", sent)}");
        }
        else
        {
            await BroadcastAsync(
                room,
                except: group == ReceiverGroup.Others ? sender : null,
                payload,
                onlyMaster: group == ReceiverGroup.MasterClient ? room.MasterClientId : null,
                interestGroup: interestGroup);
        }

        return new OperationResponse(request.OperationCode);
    }

    async ValueTask<OperationResponse?> SetPropertiesAsync(PeerState peer, OperationRequest request)
    {
        var room = peer.CurrentRoom;
        var actor = peer.CurrentActor;
        if (room is null || actor is null)
            return Fail(request, ErrorCode.OperationNotAllowedInCurrentState, "Not in a room.");

        var update = GetHashtable(request, ParameterCode.Properties);
        if (update is null) return new OperationResponse(request.OperationCode);

        var size = OperationalLimits.EstimateSize(update);
        if (size > _limits.MaxPropertiesSizePerRequest)
        {
            _log($"    {actor} sent {size} bytes of properties, over the {_limits.MaxPropertiesSizePerRequest} limit");
            return Fail(request, ErrorCode.InvalidOperation,
                        $"Properties exceed the {_limits.MaxPropertiesSizePerRequest} byte limit.");
        }

        var decision = room.CheckSetProperties(actor, update);
        if (!decision.Allowed) return Fail(request, decision.ErrorCode, decision.Message ?? "Rejected.");

        if (ActivePlugin(room) is { } gate)
        {
            var verdict = gate.BeforeSetProperties(request, actor.UserId, actor.UserId, actor.ActorNumber);
            await gate.FlushAsync(CancellationToken.None);

            if (verdict.Cancelled) return new OperationResponse(request.OperationCode);
            if (!verdict.Allowed) return Fail(request, verdict.ErrorCode, verdict.Message ?? "Rejected by plugin.");

            update = GetHashtable(request, ParameterCode.Properties);
            if (update is null) return new OperationResponse(request.OperationCode);
        }

        var targetActor = GetInt(request, ParameterCode.ActorNr);
        if (targetActor is > 0)
        {
            var target = room.Find(targetActor.Value);
            if (target is null) return Fail(request, ErrorCode.InvalidOperation, $"No actor {targetActor}.");
            Room.Merge(target.Properties, update);
        }
        else
        {
            Room.Merge(room.Properties, update);
            RefreshRoomOptions(room);
        }

        if (ActivePlugin(room) is { } applied)
        {
            var verdict = applied.OnSetProperties(request, actor.UserId, actor.UserId, actor.ActorNumber);
            await applied.FlushAsync(CancellationToken.None);

            if (!verdict.Allowed)
                return Fail(request, verdict.ErrorCode, verdict.Message ?? "Rejected by plugin.");
        }

        if (Get(request, ParameterCode.Broadcast) is true)
        {
            var changed = new Dictionary<byte, object?>
            {
                [ParameterCode.Properties] = update,
                [ParameterCode.ActorNr] = actor.ActorNumber,
                [ParameterCode.TargetActorNr] = targetActor is > 0 ? targetActor.Value : 0,
            };

            await BroadcastAsync(room, except: actor, new EventData(EventCode.PropertiesChanged, changed));
        }

        return new OperationResponse(request.OperationCode);
    }

    ValueTask<OperationResponse?> GetPropertiesAsync(PeerState peer, OperationRequest request)
    {
        var room = peer.CurrentRoom;
        if (room is null)
            return ValueTask.FromResult<OperationResponse?>(
                Fail(request, ErrorCode.OperationNotAllowedInCurrentState, "Not in a room."));

        return ValueTask.FromResult<OperationResponse?>(
            new OperationResponse(request.OperationCode, new Dictionary<byte, object?>
            {
                [ParameterCode.GameProperties] = room.Properties,
                [ParameterCode.PlayerProperties] = PlayerPropertiesOf(room),
            }));
    }

    /// <summary>
    /// Applies a cache instruction. Returns true when the request was cache maintenance
    /// with nothing to relay.
    /// </summary>
    bool ApplyCaching(Room room, Actor sender, byte eventCode, object? data, EventCaching caching)
    {
        switch (caching)
        {
            case EventCaching.DoNotCache:
                return false;

            case EventCaching.AddToRoomCache:
                room.CacheEvent(sender.ActorNumber, eventCode, data);
                return false;

            case EventCaching.AddToRoomCacheGlobal:
                room.CacheEvent(0, eventCode, data);
                return false;

            case EventCaching.RemoveFromRoomCache:
                _log($"    removed {room.RemoveCached(sender.ActorNumber, eventCode)} cached event(s)");
                return true;

            case EventCaching.RemoveFromRoomCacheForActorsLeft:
                _log($"    purged {room.RemoveCachedForAbsentActors()} orphaned cached event(s)");
                return true;

            default:
                _log($"    caching option {caching} not implemented, relaying only");
                return false;
        }
    }

    async ValueTask BroadcastAsync(
        Room room, Actor? except, EventData payload, int? onlyMaster = null, byte interestGroup = 0)
    {
        var sent = new List<int>();

        foreach (var actor in room.Actors.ToArray())
        {
            if (except is not null && actor.ActorNumber == except.ActorNumber) continue;
            if (onlyMaster is not null && actor.ActorNumber != onlyMaster) continue;
            if (!actor.ListensTo(interestGroup)) continue;

            sent.Add(actor.ActorNumber);

            try
            {
                await actor.Channel.SendEventAsync(payload, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log($"    send to {actor} failed: {ex.GetType().Name}");
            }
        }

        if (sent.Count > 0) _log($"    [ev {payload.Code}] -> actor {string.Join(", ", sent)}");
    }

    /// <summary>
    /// Writes the current master client into the room's properties.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The master client is a well-known room property (248), not merely a server-side
    /// notion. A client learns which actor it is from the game properties in its join
    /// response, and thereafter from a properties-changed event; there is no other channel
    /// for it, and no operation to ask.
    /// </para>
    /// <para>
    /// Leaving it unset does not fail anything visibly. The join succeeds, the client reports
    /// itself in the room, and then nothing happens: the client will not begin its own room setup
    /// until it knows whether it is the master, because that decides whether it is the one
    /// responsible for instantiating the world's networked objects. With the property absent
    /// the first player waits for a master that, as far as it can tell, is not there — and
    /// eventually gives up with "Could not enter the network room in time".
    /// </para>
    /// </remarks>
    /// <returns>Whether the value changed, and so needs announcing.</returns>
    static bool SyncMasterClient(Room room)
    {
        var current = room.MasterClientId;
        if (room.Properties[GamePropertyKey.MasterClientId] is int existing && existing == current) return false;

        room.Properties[GamePropertyKey.MasterClientId] = current;
        return true;
    }

    static OperationResponse Fail(OperationRequest request, short code, string message) =>
        new(request.OperationCode) { ReturnCode = code, DebugMessage = message };

    static object? Get(OperationRequest request, byte key) =>
        request.Parameters is not null && request.Parameters.TryGetValue(key, out var value) ? value : null;

    static string? GetString(OperationRequest request, byte key) => Get(request, key) as string;

    static byte? GetByte(OperationRequest request, byte key) => Get(request, key) as byte?;

    static int? GetInt(OperationRequest request, byte key) => Get(request, key) switch
    {
        int i => i,
        byte b => b,
        short s => s,
        _ => null,
    };

    static Hashtable? GetHashtable(OperationRequest request, byte key) => Get(request, key) as Hashtable;
}
