#nullable disable

using System.Collections;
using OpenPhoton.Realtime.Protocol;
using Photon.Hive.Plugin;
using Photon.SocketServer;

namespace OpenPhoton.Server.Plugins;

/// <summary>How a plugin resolved a callback.</summary>
public enum CallOutcome
{
    /// <summary>Still outstanding. A plugin that leaves a call here has hung the operation.</summary>
    Pending,

    Continued,
    Failed,
    Cancelled,
    Deferred,
}

/// <summary>
/// Base call info handed to a plugin.
/// </summary>
/// <remarks>
/// The host blocks the operation until the plugin resolves the call, so the outcome has to
/// be observable afterwards rather than acted on immediately. A plugin that returns without
/// resolving leaves <see cref="Outcome"/> at <see cref="CallOutcome.Pending"/>, which the
/// host treats as a fault rather than silently continuing — a hung operation surfaces to
/// the client as a timeout, which is far harder to diagnose than an explicit error.
/// </remarks>
public abstract class PluginCallInfo(IOperationRequest request) : ICallInfo
{
    public CallOutcome Outcome { get; private set; } = CallOutcome.Pending;

    public string FailMessage { get; private set; }

    public Dictionary<byte, object> FailData { get; private set; }

    public IOperationRequest OperationRequest { get; } = request;

    public byte Status => (byte)Outcome;

    public bool IsNew => Outcome == CallOutcome.Pending;

    public bool IsDeferred => Outcome == CallOutcome.Deferred;

    public bool IsSucceeded => Outcome == CallOutcome.Continued;

    public bool IsFailed => Outcome == CallOutcome.Failed;

    public bool IsCanceled => Outcome == CallOutcome.Cancelled;

    /// <summary>Photon exposes both spellings.</summary>
    public bool IsCancelled => IsCanceled;

    public bool IsProcessed => Outcome != CallOutcome.Pending;

    public bool IsPaused => Outcome == CallOutcome.Deferred;

    public void Continue() => Resolve(CallOutcome.Continued);

    public void Fail(string msg = null, Dictionary<byte, object> errorData = null)
    {
        FailMessage = msg;
        FailData = errorData;
        Resolve(CallOutcome.Failed);
    }

    public void Cancel() => Resolve(CallOutcome.Cancelled);

    public void Defer() => Resolve(CallOutcome.Deferred);

    public bool StrictModeCheck(out string errorMsg)
    {
        errorMsg = IsProcessed ? null : "Call was not resolved.";
        return IsProcessed;
    }

    void Resolve(CallOutcome outcome)
    {
        if (Outcome != CallOutcome.Pending)
        {
            ConflictingResolution = outcome;
            return;
        }

        Outcome = outcome;
    }

    /// <summary>Set when a plugin resolved the same call more than once.</summary>
    public CallOutcome? ConflictingResolution { get; private set; }
}

/// <summary>Typed call info.</summary>
public abstract class PluginCallInfo<TRequest>(TRequest request, string userId, string nickname)
    : PluginCallInfo((IOperationRequest)request), ITypedCallInfo<TRequest>
    where TRequest : IOperationRequest
{
    public TRequest Request { get; } = request;

    public string UserId { get; } = userId;

    public string Nickname { get; } = nickname;

    public object AuthResultsToken { get; set; }

    public Dictionary<string, object> AuthCookie { get; set; }
}

/// <summary>
/// Adapts one of our operation requests to the plugin-facing shape.
/// </summary>
/// <remarks>
/// Every property reads and writes the underlying parameter dictionary rather than copying
/// it. Plugins mutate the request expecting the change to take effect — the reference plugin strips
/// position and rotation keys out of an event's payload and sets the cache option in place
/// — so a snapshot would let the plugin appear to work while changing nothing.
/// </remarks>
public sealed class RaiseEventRequestAdapter(OperationRequest request) : IRaiseEventRequest
{
    readonly OperationRequest _request = request;

    public byte OperationCode => _request.OperationCode;

    public Dictionary<byte, object> Parameters => _request.Parameters ??= [];

    public byte WebFlags { get; set; }

    object Get(byte key) => Parameters.TryGetValue(key, out var value) ? value : null;

    void Set(byte key, object value) => Parameters[key] = value;

    public byte EvCode
    {
        get => Get(ParameterCode.Code) as byte? ?? 0;
        set => Set(ParameterCode.Code, value);
    }

    public object Data
    {
        get => Get(ParameterCode.Data);
        set => Set(ParameterCode.Data, value);
    }

    public byte Cache
    {
        get => Get(ParameterCode.Cache) as byte? ?? (byte)EventCaching.DoNotCache;
        set => Set(ParameterCode.Cache, value);
    }

    public byte ReceiverGroup
    {
        get => Get(ParameterCode.ReceiverGroup) as byte? ?? (byte)Realtime.Protocol.ReceiverGroup.Others;
        set => Set(ParameterCode.ReceiverGroup, value);
    }

    public byte Group
    {
        get => Get(ParameterCode.Group) as byte? ?? 0;
        set => Set(ParameterCode.Group, value);
    }

    public int[] Actors
    {
        get => Get(ParameterCode.TargetActorNr) as int[];
        set => Set(ParameterCode.TargetActorNr, value);
    }

    public string GameId { get; set; }

    public bool HttpForward { get; set; }

    public int? CacheSliceIndex
    {
        get => Get(ParameterCode.CacheSliceIndex) as int?;
        set => Set(ParameterCode.CacheSliceIndex, value);
    }
}

/// <summary>Join request as the plugin sees it.</summary>
public sealed class JoinGameRequestAdapter(OperationRequest request, string gameId) : IJoinGameRequest
{
    readonly OperationRequest _request = request;

    public byte OperationCode => _request.OperationCode;

    public Dictionary<byte, object> Parameters => _request.Parameters ??= [];

    public byte WebFlags { get; set; }

    object Get(byte key) => Parameters.TryGetValue(key, out var value) ? value : null;

    public int ActorNr { get; set; }

    public Hashtable ActorProperties
    {
        get => Get(ParameterCode.PlayerProperties) as Hashtable;
        set => Parameters[ParameterCode.PlayerProperties] = value;
    }

    public Hashtable GameProperties
    {
        get => Get(ParameterCode.GameProperties) as Hashtable;
        set => Parameters[ParameterCode.GameProperties] = value;
    }

    public string GameId { get; set; } = gameId;

    public bool BroadcastActorProperties { get; set; } = true;

    public bool DeleteCacheOnLeave { get; set; } = true;

    public int EmptyRoomLiveTime { get; set; }

    /// <summary>
    /// Whether the client asked for room events to be suppressed.
    /// </summary>
    /// <remarks>
    /// Read from the request rather than defaulted, because plugins gate on it.
    /// The reference plugin refuses to create a room when this is set, so a server that always
    /// reports false quietly disables that check — and one that reports true
    /// rejects every room with a message about suppression the client never asked
    /// for. Photon sends it both as its own parameter and as a bit in the room
    /// option flags; either is enough.
    /// </remarks>
    public bool SuppressRoomEvents
    {
        get => Get(ParameterCode.SuppressRoomEvents) as bool?
               ?? (RoomFlags & (int)Realtime.Protocol.RoomOptionBit.SuppressRoomEvents) != 0;
        set => Parameters[ParameterCode.SuppressRoomEvents] = value;
    }

    public byte JoinMode => Get(ParameterCode.JoinMode) as byte? ?? 0;

    public bool CreateIfNotExists => JoinMode == (byte)Realtime.Protocol.JoinMode.CreateIfNotExists;

    public string LobbyName => Get(ParameterCode.LobbyName) as string;

    public byte LobbyType => Get(ParameterCode.LobbyType) as byte? ?? 0;

    /// <summary>
    /// The room option bitmask the client sent, as <c>RoomOptionFlags</c> (191).
    /// </summary>
    /// <remarks>
    /// Backed by the request so a plugin sees what the client actually asked for,
    /// and so a plugin's own write survives to the room. The reference plugin both reads this
    /// (rejecting a room whose flags are zero) and sets it to 35 in OnCreateGame,
    /// which an auto-property would accept and then throw away.
    /// </remarks>
    public int RoomFlags
    {
        get => Get(ParameterCode.RoomOptionFlags) as int? ?? 0;
        set => Parameters[ParameterCode.RoomOptionFlags] = value;
    }

    public int PlayerTTL
    {
        get => Get(ParameterCode.PlayerTTL) as int? ?? 0;
        set => Parameters[ParameterCode.PlayerTTL] = value;
    }
}

/// <summary>Leave request as the plugin sees it.</summary>
public sealed class LeaveGameRequestAdapter(OperationRequest request) : ILeaveGameRequest
{
    readonly OperationRequest _request = request;

    public byte OperationCode => _request.OperationCode;

    public Dictionary<byte, object> Parameters => _request.Parameters ??= [];

    public byte WebFlags { get; set; }

    /// <summary>Photon's spelling.</summary>
    public bool IsCommingBack { get; set; }
}

/// <summary>Property update as the plugin sees it.</summary>
public sealed class SetPropertiesRequestAdapter(OperationRequest request) : ISetPropertiesRequest
{
    readonly OperationRequest _request = request;

    public byte OperationCode => _request.OperationCode;

    public Dictionary<byte, object> Parameters => _request.Parameters ??= [];

    public byte WebFlags { get; set; }

    /// <summary>
    /// Which actor's properties are being set. Read from the request, not stored.
    /// </summary>
    /// <remarks>
    /// As an auto-property this answered 0 for every update, whatever the client sent.
    /// The reference plugin compares it against the acting actor and treats a mismatch as an attempt to
    /// write somebody else's properties — so a plugin doing the obviously correct thing
    /// kicked every player the moment they set a property, which in practice is immediately
    /// after entering a world. Same failure the room flags had: a property that looks like
    /// state but is really a view onto the request.
    /// </remarks>
    public int ActorNumber
    {
        get => Parameters.TryGetValue(ParameterCode.ActorNr, out var v) && v is int actor ? actor : 0;
        set => Parameters[ParameterCode.ActorNr] = value;
    }

    /// <summary>Whether the update is announced to the room. Also the client's to set.</summary>
    public bool Broadcast
    {
        get => Parameters.TryGetValue(ParameterCode.Broadcast, out var v) && v is bool broadcast && broadcast;
        set => Parameters[ParameterCode.Broadcast] = value;
    }

    public bool HttpForward { get; set; }

    public Hashtable Properties
    {
        get => Parameters.TryGetValue(ParameterCode.Properties, out var v) ? v as Hashtable : null;
        set => Parameters[ParameterCode.Properties] = value;
    }

    /// <summary>The compare-and-set precondition, when the client sent one.</summary>
    public Hashtable ExpectedValues
    {
        get => Parameters.TryGetValue(ParameterCode.ExpectedValues, out var v) ? v as Hashtable : null;
        set => Parameters[ParameterCode.ExpectedValues] = value;
    }
}

/// <summary>Room close as the plugin sees it.</summary>
public sealed class CloseRequestAdapter : ICloseRequest
{
    public byte OperationCode => 0;

    public Dictionary<byte, object> Parameters { get; } = [];

    public byte WebFlags { get; set; }

    public int EmptyRoomTTL { get; set; }
}
