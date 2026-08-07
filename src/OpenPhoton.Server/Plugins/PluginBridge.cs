#nullable disable

using OpenPhoton.Server.Rooms;
using Photon.Hive.Plugin;
using Photon.SocketServer;

namespace OpenPhoton.Server.Plugins;

/// <summary>
/// Runs a room's plugin and translates its decisions back into room behaviour.
/// </summary>
/// <remarks>
/// <para>
/// The bridge is deliberately the only place that knows both sides. Room code never sees a
/// plugin type and plugin code never sees a room, which is what allows a plugin compiled
/// against Photon to run here at all.
/// </para>
/// <para>
/// Two rules govern every callback. A plugin must resolve the call, and an unresolved call
/// is treated as a failure rather than an implicit continue — silently proceeding would let
/// a buggy plugin's rejections be ignored. And request state is read <em>after</em> the
/// callback returns, because plugins mutate the request in place: the reference plugin strips keys out
/// of an event payload and rewrites its cache option, expecting both to take effect.
/// </para>
/// </remarks>
public sealed class PluginBridge(IGamePlugin plugin, PluginHostAdapter host, Action<string> log) : IAsyncDisposable
{
    readonly IGamePlugin _plugin = plugin;
    readonly PluginHostAdapter _host = host;
    readonly Action<string> _log = log;

    public IGamePlugin Plugin => _plugin;

    /// <summary>The room as the plugin sees it. Exposed for hosting code and tests.</summary>
    public IPluginHost Host => _host;

    /// <summary>Shuts the plugin's host down once its room is gone.</summary>
    /// <remarks>
    /// Not optional. The host runs a pump task and owns the plugin's timers, both of which
    /// would otherwise keep the closed room alive for as long as the process runs.
    /// </remarks>
    public ValueTask DisposeAsync() => _host.DisposeAsync();

    /// <summary>
    /// Delivers everything the plugin emitted during its callbacks.
    /// </summary>
    /// <remarks>
    /// Callers must invoke this after each callback. Plugin sends are queued rather than
    /// performed inline, because <c>BroadcastEvent</c> is a void method a plugin expects to
    /// return immediately while our send path is asynchronous.
    /// </remarks>
    public ValueTask FlushAsync(CancellationToken ct) => _host.FlushAsync(ct);

    /// <summary>Holds the plugin's output until <see cref="Release"/>.</summary>
    /// <remarks>
    /// Held across a join, so that what a plugin sends from inside <c>OnCreateGame</c> or
    /// <c>OnJoin</c> cannot reach the client before the join event does. Flushing alone does
    /// not achieve this: it waits for the pump, and the pump has already delivered.
    /// </remarks>
    public void Hold() => _host.Hold();

    /// <summary>Lets the plugin's held output go.</summary>
    public void Release() => _host.Release();

    /// <summary>Creates a bridge and wires the plugin to its host.</summary>
    public static PluginBridge Create(IGamePlugin plugin, Rooms.Room room, Action<string> log,
                                      Dictionary<string, string> config = null)
    {
        var host = new PluginHostAdapter(room, log);
        var bridge = new PluginBridge(plugin, host, log);

        if (!plugin.SetupInstance(host, config ?? [], out var error))
            log($"    plugin {plugin.Name} refused setup: {error}");

        return bridge;
    }

    /// <summary>Result of a plugin callback, expressed in room terms.</summary>
    public readonly record struct Verdict(bool Allowed, bool Cancelled, short ErrorCode, string Message)
    {
        public static Verdict Allow { get; } = new(true, false, 0, null);

        public static Verdict Cancel { get; } = new(false, true, 0, null);

        public static Verdict Deny(short code, string message) => new(false, false, code, message);
    }

    Verdict Resolve(PluginCallInfo info, string callbackName)
    {
        if (info.ConflictingResolution is { } conflict)
            _log($"    plugin resolved {callbackName} twice ({info.Outcome} then {conflict}); keeping the first");

        switch (info.Outcome)
        {
            case CallOutcome.Continued:
            case CallOutcome.Deferred:
                return Verdict.Allow;

            case CallOutcome.Cancelled:
                return Verdict.Cancel;

            case CallOutcome.Failed:
                return Verdict.Deny(Realtime.Protocol.ErrorCode.PluginReportedError,
                    info.FailMessage ?? "Rejected by plugin.");

            default:
                _log($"    plugin left {callbackName} unresolved; treating as failure");
                return Verdict.Deny(Realtime.Protocol.ErrorCode.PluginReportedError,
                    $"Plugin did not resolve {callbackName}.");
        }
    }

    /// <summary>Runs a callback, converting a thrown plugin into a clean rejection.</summary>
    Verdict Invoke(string name, PluginCallInfo info, Action<IGamePlugin> callback)
    {
        try
        {
            callback(_plugin);
        }
        catch (Exception ex)
        {
            _log($"    plugin threw in {name}: {ex.GetType().Name}: {ex.Message}");
            try { _plugin.ReportError(ErrorCodes.UnhandledException, ex, null); } catch { }

            return Verdict.Deny(Realtime.Protocol.ErrorCode.PluginReportedError, $"Plugin failed in {name}.");
        }

        return Resolve(info, name);
    }

    public Verdict OnCreateGame(OperationRequest request, string gameId, string userId, string nickname, bool isJoin)
    {
        var info = new CreateGameCallInfo(new JoinGameRequestAdapter(request, gameId), userId, nickname)
        {
            IsJoin = isJoin,
        };
        return Invoke(nameof(OnCreateGame), info, p => p.OnCreateGame(info));
    }

    public Verdict BeforeJoin(OperationRequest request, string gameId, string userId, string nickname)
    {
        var info = new BeforeJoinGameCallInfo(new JoinGameRequestAdapter(request, gameId), userId, nickname);
        return Invoke(nameof(BeforeJoin), info, p => p.BeforeJoin(info));
    }

    /// <summary>Runs OnJoin and reports whether the plugin wants the usual join traffic suppressed.</summary>
    public Verdict OnJoin(OperationRequest request, string gameId, string userId, string nickname, int actorNr,
                          out bool publishJoinEvents, out bool publishCache)
    {
        var adapter = new JoinGameRequestAdapter(request, gameId) { ActorNr = actorNr };

        var info = new JoinGameCallInfo(adapter, userId, nickname, actorNr);
        var verdict = Invoke(nameof(OnJoin), info, p => p.OnJoin(info));

        publishJoinEvents = info.JoinParams.PublishJoinEvents;
        publishCache = info.JoinParams.PublishCache;
        return verdict;
    }

    public Verdict OnLeave(OperationRequest request, string userId, string nickname, int actorNr, bool isInactive, int reason)
    {
        var info = new LeaveGameCallInfo(new LeaveGameRequestAdapter(request), userId, nickname, actorNr)
        {
            IsInactive = isInactive,
            Reason = reason,
        };
        return Invoke(nameof(OnLeave), info, p => p.OnLeave(info));
    }

    /// <summary>
    /// Runs OnRaiseEvent. The request is mutable and its post-callback state is authoritative.
    /// </summary>
    public Verdict OnRaiseEvent(OperationRequest request, string userId, string nickname, int actorNr)
    {
        var info = new RaiseEventCallInfo(new RaiseEventRequestAdapter(request), userId, nickname, actorNr);
        return Invoke(nameof(OnRaiseEvent), info, p => p.OnRaiseEvent(info));
    }

    public Verdict BeforeSetProperties(OperationRequest request, string userId, string nickname, int actorNr)
    {
        var info = new BeforeSetPropertiesCallInfo(new SetPropertiesRequestAdapter(request), userId, nickname, actorNr);
        return Invoke(nameof(BeforeSetProperties), info, p => p.BeforeSetProperties(info));
    }

    public Verdict OnSetProperties(OperationRequest request, string userId, string nickname, int actorNr)
    {
        var info = new SetPropertiesCallInfo(new SetPropertiesRequestAdapter(request), userId, nickname, actorNr);
        return Invoke(nameof(OnSetProperties), info, p => p.OnSetProperties(info));
    }

    public Verdict BeforeCloseGame()
    {
        var info = new BeforeCloseGameCallInfo(new CloseRequestAdapter());
        return Invoke(nameof(BeforeCloseGame), info, p => p.BeforeCloseGame(info));
    }

    public Verdict OnCloseGame(int actorCount)
    {
        var info = new CloseGameCallInfo(new CloseRequestAdapter(), actorCount);
        return Invoke(nameof(OnCloseGame), info, p => p.OnCloseGame(info));
    }
}
