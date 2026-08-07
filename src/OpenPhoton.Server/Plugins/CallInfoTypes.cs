#nullable disable

using Photon.Hive.Plugin;

namespace OpenPhoton.Server.Plugins;

/// <summary>Concrete call-info types passed to plugin callbacks.</summary>
public sealed class CreateGameCallInfo(IJoinGameRequest request, string userId, string nickname)
    : PluginCallInfo<IJoinGameRequest>(request, userId, nickname), ICreateGameCallInfo
{
    public bool IsJoin { get; init; }

    public bool CreateIfNotExists => Request.CreateIfNotExists;

    public Dictionary<string, object> CreateOptions { get; } = [];
}

public sealed class BeforeJoinGameCallInfo(IJoinGameRequest request, string userId, string nickname)
    : PluginCallInfo<IJoinGameRequest>(request, userId, nickname), IBeforeJoinGameCallInfo
{
}

public sealed class JoinGameCallInfo(IJoinGameRequest request, string userId, string nickname, int actorNr)
    : PluginCallInfo<IJoinGameRequest>(request, userId, nickname), IJoinGameCallInfo
{
    public int ActorNr { get; } = actorNr;

    /// <summary>
    /// Lets the plugin suppress the cache replay or the join events for this actor.
    /// </summary>
    /// <remarks>
    /// Read after the callback, not before: a plugin turning these off is taking over
    /// telling the new actor about the room.
    /// </remarks>
    public ProcessJoinParams JoinParams { get; } = new();
}

public sealed class LeaveGameCallInfo(ILeaveGameRequest request, string userId, string nickname, int actorNr)
    : PluginCallInfo<ILeaveGameRequest>(request, userId, nickname), ILeaveGameCallInfo
{
    public int ActorNr { get; } = actorNr;

    public bool IsInactive { get; init; }

    public int Reason { get; init; } = LeaveReason.LeaveRequest;

    public string Details { get; init; }
}

public sealed class RaiseEventCallInfo(IRaiseEventRequest request, string userId, string nickname, int actorNr)
    : PluginCallInfo<IRaiseEventRequest>(request, userId, nickname), IRaiseEventCallInfo
{
    public int ActorNr { get; } = actorNr;
}

public sealed class BeforeSetPropertiesCallInfo(ISetPropertiesRequest request, string userId, string nickname, int actorNr)
    : PluginCallInfo<ISetPropertiesRequest>(request, userId, nickname), IBeforeSetPropertiesCallInfo
{
    public int ActorNr { get; } = actorNr;
}

public sealed class SetPropertiesCallInfo(ISetPropertiesRequest request, string userId, string nickname, int actorNr)
    : PluginCallInfo<ISetPropertiesRequest>(request, userId, nickname), ISetPropertiesCallInfo
{
    public int ActorNr { get; } = actorNr;
}

public sealed class SetPropertiesFailedCallInfo(ISetPropertiesRequest request, string userId, string nickname, int actorNr)
    : PluginCallInfo<ISetPropertiesRequest>(request, userId, nickname), ISetPropertiesFailedCallInfo
{
    public int ActorNr { get; } = actorNr;
}

public sealed class BeforeCloseGameCallInfo(ICloseRequest request)
    : PluginCallInfo<ICloseRequest>(request, null, null), IBeforeCloseGameCallInfo
{
    public bool FailedOnCreate { get; init; }
}

public sealed class CloseGameCallInfo(ICloseRequest request, int actorCount)
    : PluginCallInfo<ICloseRequest>(request, null, null), ICloseGameCallInfo
{
    public int ActorCount { get; } = actorCount;

    public bool FailedOnCreate { get; init; }
}

public sealed class DisconnectCallInfo(IOperationRequest request, int actorNr)
    : PluginCallInfo<IOperationRequest>(request, null, null), IDisconnectCallInfo
{
    public int ActorNr { get; } = actorNr;
}
