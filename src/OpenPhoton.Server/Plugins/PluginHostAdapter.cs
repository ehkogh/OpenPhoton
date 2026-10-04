#nullable disable

using System.Collections;
using System.Threading.Channels;
using OpenPhoton.Realtime.Protocol;
using OpenPhoton.Server.Rooms;
using Photon.Hive.Plugin;
using Photon.SocketServer;
using EventData = Photon.SocketServer.EventData;

namespace OpenPhoton.Server.Plugins;

/// <summary>Exposes one of our actors to a plugin.</summary>
public sealed class ActorAdapter(Actor actor) : IActor
{
    readonly Actor _actor = actor;

    public int ActorNr => _actor.ActorNumber;

    public string UserId => _actor.UserId;

    public string Nickname => _actor.Properties[ActorProperties.NickName] as string;

    /// <summary>Note the polarity: Photon exposes presence, not absence.</summary>
    public bool IsActive => !_actor.IsInactive;

    public object Secure => _secure;

    readonly Dictionary<string, object> _secure = [];

    public void UpdateSecure(string key, object value) => _secure[key] = value;

    /// <summary>
    /// The actor's properties, wrapping the same storage rather than copying it.
    /// </summary>
    /// <remarks>
    /// Plugins read and write actor properties expecting the room to see the change, so a
    /// snapshot here would make every plugin-side property write silently vanish.
    /// </remarks>
    public PropertyBag<object> Properties
    {
        get
        {
            if (_properties is not null) return _properties;

            _properties = new PropertyBag<object>();
            foreach (DictionaryEntry entry in _actor.Properties) _properties.Set(entry.Key, entry.Value);

            _properties.PropertyChanged += (_, e) =>
            {
                if (_properties.TryGetValue(e.Key, out var value)) _actor.Properties[e.Key] = value;
                else _actor.Properties.Remove(e.Key);
            };

            return _properties;
        }
    }

    PropertyBag<object> _properties;
}

/// <summary>
/// The room, as a plugin sees it.
/// </summary>
/// <remarks>
/// <para>
/// Sends are posted to a pump rather than performed inline. Photon runs plugin code on the
/// room's own fiber and <c>BroadcastEvent</c> returns void, so a plugin expects it to be
/// non-blocking; our send path is asynchronous. Blocking on it inside a callback would
/// deadlock the very dispatch that is waiting for the plugin to return.
/// </para>
/// <para>
/// The pump runs continuously rather than being drained by whoever called the plugin,
/// because plugins do not only send from inside callbacks. The reference plugin starts a
/// <c>Task.Run</c> loop in <c>SetupInstance</c> that broadcasts to every actor once a
/// second, forever. Work collected only during a callback would sit unsent until the next
/// operation happened to arrive — and a room where nobody is doing anything is exactly when
/// that timer matters. A continuously running pump also makes off-thread sends safe, which
/// a plain queue was not.
/// </para>
/// <para>
/// Ordering still holds where it is relied upon: the pump is a single reader over a FIFO
/// channel, so everything a plugin emits during a callback is delivered in order, and
/// <see cref="FlushAsync"/> waits for exactly that much to have happened.
/// </para>
/// </remarks>
public sealed class PluginHostAdapter : IPluginHost, IAsyncDisposable
{
    readonly Room _room;
    readonly Action<string> _log;
    readonly Channel<Func<CancellationToken, ValueTask>> _outbound =
        Channel.CreateUnbounded<Func<CancellationToken, ValueTask>>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    readonly CancellationTokenSource _stopping = new();
    readonly Task _pump;

    public PluginHostAdapter(Room room, Action<string> log)
    {
        _room = room;
        _log = log;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Queues work for the pump. Safe from any thread.</summary>
    void Post(Func<CancellationToken, ValueTask> work)
    {
        _outbound.Writer.TryWrite(work);
    }

    /// <summary>
    /// Holds the plugin's output until the matching <see cref="Release"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Taken for the duration of a join, because a plugin sends from inside
    /// <c>OnCreateGame</c> and <c>OnJoin</c> — the reference plugin pushes a moderation request and
    /// rate-limiter values at the arriving actor — and those callbacks run <em>before</em>
    /// the join event exists. Without a hold the pump delivers them the instant they are
    /// posted, so they overtake the join.
    /// </para>
    /// <para>
    /// The client refuses to act on an event that arrives before it is in the room: one client's
    /// handler answers by raising an event of its own, its Photon client rejects that with
    /// "RaiseEvent failed ... check if you are in a Room", and the join stalls with a black
    /// screen and nothing further logged.
    /// </para>
    /// <para>
    /// A counter rather than a flag, so overlapping joins cannot have the first one to finish
    /// release the second one's hold.
    /// </para>
    /// </remarks>
    public void Hold()
    {
        lock (_holdGate) _holds++;
    }

    /// <summary>Releases one <see cref="Hold"/>, letting the pump run once none remain.</summary>
    public void Release()
    {
        TaskCompletionSource opened = null;

        lock (_holdGate)
        {
            if (_holds == 0) return;
            if (--_holds > 0) return;

            opened = _held;
            _held = null;
        }

        opened?.TrySetResult();
    }

    /// <summary>Drops every outstanding hold at once.</summary>
    public void ReleaseAll()
    {
        TaskCompletionSource opened;

        lock (_holdGate)
        {
            _holds = 0;
            opened = _held;
            _held = null;
        }

        opened?.TrySetResult();
    }

    /// <summary>Waits out any hold. Returns immediately when there is none.</summary>
    ValueTask WaitWhileHeldAsync()
    {
        Task waiting;

        lock (_holdGate)
        {
            if (_holds == 0) return ValueTask.CompletedTask;
            _held ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting = _held.Task;
        }

        return new ValueTask(waiting);
    }

    readonly object _holdGate = new();
    int _holds;
    TaskCompletionSource _held;

    async Task PumpAsync()
    {
        try
        {
            await foreach (var work in _outbound.Reader.ReadAllAsync(_stopping.Token))
            {
                await WaitWhileHeldAsync().AsTask().WaitAsync(_stopping.Token);

                try
                {
                    await work(_stopping.Token);
                }
                catch (Exception ex)
                {
                    _log($"    plugin-requested send failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Waits until everything queued so far has been performed.</summary>
    /// <remarks>
    /// A barrier rather than a drain: the pump owns the work, so the only thing a caller can
    /// do is put a marker in the queue and wait for it to come round. Because the channel is
    /// FIFO with one reader, the marker completing means everything ahead of it is done.
    /// </remarks>
    public async ValueTask FlushAsync(CancellationToken ct)
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_outbound.Writer.TryWrite(_ => { barrier.TrySetResult(); return ValueTask.CompletedTask; }))
            return;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
        try
        {
            await barrier.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
    }

    /// <summary>Stops the pump once its remaining work is out.</summary>
    public async ValueTask DisposeAsync()
    {
        StopAllTimers();
        _outbound.Writer.TryComplete();

        ReleaseAll();

        try
        {
            await _pump.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            _log("    plugin pump did not finish; abandoning its remaining sends");
        }

        await _stopping.CancelAsync();
        _stopping.Dispose();
    }

    public void BroadcastEvent(IList<int> recieverActors, int senderActor, byte evCode,
                               Dictionary<byte, object> data, byte cacheOp, SendParameters sendParameters = default)
    {
        var targets = recieverActors?.ToArray() ?? [];
        var payload = BuildEvent(evCode, data, senderActor);

        Post(async ct =>
        {
            var delivered = new List<int>();

            foreach (var number in targets)
                if (_room.Find(number) is { } actor && !actor.IsInactive)
                {
                    await actor.Channel.SendEventAsync(payload, ct);
                    delivered.Add(number);
                }

            LogEvent(evCode, delivered, targets.Length);
        });

        ApplyCacheOp(cacheOp, senderActor, evCode, data);
    }

    public void BroadcastEvent(byte target, int senderActor, byte targetGroup, byte evCode,
                               Dictionary<byte, object> data, byte cacheOp, SendParameters sendParameters = default)
    {
        var payload = BuildEvent(evCode, data, senderActor);

        Post(async ct =>
        {
            var delivered = new List<int>();

            foreach (var actor in _room.ActiveActors.ToArray())
            {
                if (target == ReciverGroup.Others && actor.ActorNumber == senderActor) continue;
                if (target == ReciverGroup.MasterClient && actor.ActorNumber != _room.MasterClientId) continue;
                if (!actor.ListensTo(targetGroup)) continue;

                delivered.Add(actor.ActorNumber);
                await actor.Channel.SendEventAsync(payload, ct);
            }

            LogEvent(evCode, delivered, delivered.Count);
        });

        ApplyCacheOp(cacheOp, senderActor, evCode, data);
    }

    /// <summary>
    /// Records an event the plugin emitted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rate-limited per event code, because a plugin may broadcast on a timer — the reference plugin
    /// sends event 35 to every actor once a second, forever, and logging each one buries
    /// everything else within a minute. The first of each code is always logged, then one
    /// every <see cref="EventLogInterval"/>, so a periodic event proves it is still running
    /// without drowning the log.
    /// </para>
    /// <para>
    /// A plugin's output is otherwise entirely invisible. A plugin that initialises and then
    /// does nothing looks exactly like one that is working, and the difference is most of
    /// what one wants to know when a client sits waiting.
    /// </para>
    /// </remarks>
    void LogEvent(byte evCode, List<int> delivered, int requested)
    {
        var now = DateTime.UtcNow;

        lock (_eventLogGate)
        {
            if (_lastEventLog.TryGetValue(evCode, out var last) && now - last < EventLogInterval) return;
            _lastEventLog[evCode] = now;
        }

        var missed = requested - delivered.Count;
        _log($"    [plugin ev {evCode}] -> actor {(delivered.Count == 0 ? "(none)" : string.Join(", ", delivered))}"
           + $"{(missed > 0 ? $" ({missed} not present)" : "")}");
    }

    static readonly TimeSpan EventLogInterval = TimeSpan.FromSeconds(30);

    readonly object _eventLogGate = new();
    readonly Dictionary<byte, DateTime> _lastEventLog = [];

    public void BroadcastErrorInfoEvent(string message, SendParameters sendParameters = default) =>
        BroadcastEvent(ReciverGroup.All, 0, 0, EventCode.ErrorInfo,
            new Dictionary<byte, object> { [ParameterCode.Info] = message }, CacheOperations.DoNotCache);

    public void BroadcastErrorInfoEvent(string message, ICallInfo info, SendParameters sendParameters = default) =>
        BroadcastErrorInfoEvent(message, sendParameters);

    static EventData BuildEvent(byte evCode, Dictionary<byte, object> data, int senderActor)
    {
        var parameters = new Dictionary<byte, object>();
        if (data is not null) foreach (var pair in data) parameters[pair.Key] = pair.Value;

        if (!parameters.ContainsKey(ParameterCode.ActorNr) && senderActor != 0)
            parameters[ParameterCode.ActorNr] = senderActor;

        return new EventData(evCode, parameters);
    }

    void ApplyCacheOp(byte cacheOp, int senderActor, byte evCode, Dictionary<byte, object> data)
    {
        switch (cacheOp)
        {
            case CacheOperations.AddToRoomCache:
                _room.CacheEvent(senderActor, evCode, data);
                break;
            case CacheOperations.AddToRoomCacheGlobal:
                _room.CacheEvent(0, evCode, data);
                break;
            case CacheOperations.RemoveFromRoomCache:
                _room.RemoveCached(senderActor, evCode);
                break;
            case CacheOperations.RemoveFromCacheForActorsLeft:
                _room.RemoveCachedForAbsentActors();
                break;
        }
    }

    public string GameId => _room.Name;

    public Hashtable GameProperties => _room.Properties;

    public Dictionary<string, object> CustomGameProperties
    {
        get
        {
            var custom = new Dictionary<string, object>();
            foreach (DictionaryEntry entry in _room.Properties)
                if (entry.Key is string key) custom[key] = entry.Value;
            return custom;
        }
    }

    public int MasterClientId => _room.MasterClientId;

    public bool IsSuspended => false;

    /// <summary>Photon exposes both spellings.</summary>
    public bool IsSyspended => IsSuspended;

    public IList<IActor> GameActors => [.. _room.Actors.Select(a => (IActor)new ActorAdapter(a))];

    public IList<IActor> GameActorsActive => [.. _room.ActiveActors.Select(a => (IActor)new ActorAdapter(a))];

    public IList<IActor> GameActorsInactive =>
        [.. _room.Actors.Where(a => a.IsInactive).Select(a => (IActor)new ActorAdapter(a))];

    public Dictionary<string, object> Environment { get; } = new() { ["Server"] = "OpenPhoton" };

    public bool SetProperties(int actorNr, Hashtable properties, Hashtable expected, bool broadcast)
    {
        if (properties is null) return true;

        var target = actorNr > 0 ? _room.Find(actorNr)?.Properties : _room.Properties;
        if (target is null) return false;

        Room.Merge(target, properties);

        if (broadcast)
        {
            var changed = new Dictionary<byte, object>
            {
                [ParameterCode.Properties] = properties,
                [ParameterCode.ActorNr] = actorNr,
                [ParameterCode.TargetActorNr] = actorNr > 0 ? actorNr : 0,
            };

            BroadcastEvent(ReciverGroup.All, 0, 0, EventCode.PropertiesChanged, changed, CacheOperations.DoNotCache);
        }

        return true;
    }

    public bool RemoveActor(int actorNr, string reasonDetail) =>
        RemoveActor(actorNr, RemoveActorReason.Kick, reasonDetail);

    public bool RemoveActor(int actorNr, byte reason, string reasonDetail)
    {
        var actor = _room.Find(actorNr);
        if (actor is null) return false;

        _log($"    plugin removed {actor}: {reasonDetail}");

        Post(ct =>
        {
            actor.Channel.Close();
            return ValueTask.CompletedTask;
        });

        return true;
    }

    public bool ExecuteCacheOperation(CacheOp operation, out string errorMsg)
    {
        errorMsg = null;
        if (operation is null) return true;

        ApplyCacheOp(operation.CacheOperation, operation.ActorNr, operation.EventCode, null);
        return true;
    }

    public SerializableGameState GetSerializableGameState() => new() { GameId = _room.Name };

    public bool SetGameState(SerializableGameState state) => false;

    public bool TryRegisterType(Type type, byte typeCode,
                               Func<object, byte[]> serializeFunction, Func<byte[], object> deserializeFunction)
    {
        _log($"    plugin tried to register custom type {typeCode} ({type.Name}); not supported");
        return false;
    }

    public void HttpRequest(HttpRequest request) => HttpRequest(request, null);

    /// <summary>
    /// Calls out to a web service on the plugin's behalf.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The request is performed on the pump, so the plugin's callback arrives on the same
    /// thread as everything else it does and cannot race its own room state. That is the
    /// point of the <c>Async</c> flag: Photon's synchronous mode blocks the room until the
    /// response lands, and running the call as a pump work item reproduces it — nothing else
    /// the plugin queued proceeds until the callback has returned.
    /// </para>
    /// <para>
    /// An asynchronous request performs the call off the pump and posts only the callback,
    /// so a slow endpoint delays that one plugin callback instead of the whole room.
    /// </para>
    /// <para>
    /// The callback runs even when the call fails. A plugin that treats <c>HttpRequest</c> as
    /// "do this and continue" would otherwise leave the operation it was resolving hanging
    /// forever, so a failure has to arrive as a response with an error status, never as
    /// silence.
    /// </para>
    /// </remarks>
    public void HttpRequest(HttpRequest request, ICallInfo info)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
        {
            Deliver(PluginHttp.Failed(request, info, "No URL."), request);
            return;
        }

        if (request.Async)
        {
            _ = Task.Run(async () =>
            {
                var response = await PluginHttp.SendAsync(request, info, _stopping.Token);
                Post(_ => { Deliver(response, request); return ValueTask.CompletedTask; });
            });

            return;
        }

        Post(async ct =>
        {
            var response = await PluginHttp.SendAsync(request, info, ct);
            Deliver(response, request);
        });
    }

    void Deliver(IHttpResponse response, HttpRequest request)
    {
        try
        {
            request.Callback?.Invoke(response, request.UserState);
        }
        catch (Exception ex)
        {
            _log($"    plugin threw in an HTTP callback: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public EnvironmentVersion GetEnvironmentVersion() =>
        new() { BuiltWithVersion = "1.1.0.24499", HostVersion = "OpenPhoton" };

    public void Enqueue(Action action) => Post(_ =>
    {
        action();
        return ValueTask.CompletedTask;
    });

    public IPluginFiber GetRoomFiber() => _fiber ??= new RoomFiber(this);

    RoomFiber _fiber;

    sealed class RoomFiber(PluginHostAdapter host) : IPluginFiber
    {
        readonly PluginHostAdapter _host = host;

        public int Enqueue(Action action)
        {
            _host.Enqueue(action);
            return EnqueueStatus.Success;
        }

        public object CreateTimer(Action action, int firstInMs, int regularInMs) =>
            _host.CreateTimer(action, firstInMs, regularInMs);

        public object CreateOneTimeTimer(Action action, long firstInMs) =>
            _host.CreateOneTimeTimer(action, (int)Math.Min(firstInMs, int.MaxValue));

        public object CreateOneTimeTimer(Action action, int firstInMs) =>
            _host.CreateOneTimeTimer(action, firstInMs);

        public void StopTimer(object timer) => _host.StopTimer(timer);
    }

    /// <summary>
    /// Runs a plugin callback later, and repeatedly.
    /// </summary>
    /// <remarks>
    /// The timer thread only posts to the pump; the plugin's action runs there. Calling it on
    /// the timer thread instead would let it run concurrently with a room operation calling
    /// into the same plugin, which Photon never does and no plugin is written to survive.
    /// </remarks>
    public object CreateTimer(Action callback, int dueTimeMs, int intervalMs)
    {
        var timer = new Timer(_ => Enqueue(callback), null, Timeout.Infinite, Timeout.Infinite);
        timer.Change(Math.Max(dueTimeMs, 0), intervalMs <= 0 ? Timeout.Infinite : intervalMs);

        lock (_timers) _timers.Add(timer);
        return timer;
    }

    public object CreateOneTimeTimer(Action callback, int dueTimeMs) => CreateTimer(callback, dueTimeMs, 0);

    public object CreateOneTimeTimer(ICallInfo info, Action callback, int dueTimeMs) =>
        CreateTimer(callback, dueTimeMs, 0);

    public void StopTimer(object timer)
    {
        if (timer is not Timer handle) return;

        lock (_timers) _timers.Remove(handle);
        handle.Dispose();
    }

    readonly List<Timer> _timers = [];

    void StopAllTimers()
    {
        lock (_timers)
        {
            foreach (var timer in _timers) timer.Dispose();
            _timers.Clear();
        }
    }

    public IPluginLogger CreateLogger(string name) => new PluginLogger(name, _log);

    public void LogDebug(object message) => _log($"    [plugin] {message}");

    public void LogInfo(object message) => _log($"    [plugin] {message}");

    public void LogWarning(object message) => _log($"    [plugin] warn: {message}");

    public void LogError(object message) => _log($"    [plugin] error: {message}");

    public void LogFatal(object message) => _log($"    [plugin] fatal: {message}");
}

/// <summary>Routes plugin logging into the server's log.</summary>
public sealed class PluginLogger(string name, Action<string> log) : IPluginLogger
{
    readonly string _name = name;
    readonly Action<string> _log = log;

    public string Name => _name;

    public bool IsDebugEnabled => true;

    public bool IsErrorEnabled => true;

    public bool IsFatalEnabled => true;

    public bool IsInfoEnabled => true;

    public bool IsWarnEnabled => true;

    void Write(string level, object message, Exception exception = null) =>
        _log($"    [{_name}] {level}: {message}{(exception is null ? "" : $" -- {exception.GetType().Name}: {exception.Message}")}");

    public void Debug(object message) => Write("debug", message);

    public void Debug(object message, Exception exception) => Write("debug", message, exception);

    public void DebugFormat(string format, params object[] args) => Write("debug", string.Format(format, args));

    public void DebugFormat(IFormatProvider provider, string format, params object[] args) =>
        Write("debug", string.Format(provider, format, args));

    public void Error(object message) => Write("error", message);

    public void Error(object message, Exception exception) => Write("error", message, exception);

    public void ErrorFormat(string format, params object[] args) => Write("error", string.Format(format, args));

    public void ErrorFormat(IFormatProvider provider, string format, params object[] args) =>
        Write("error", string.Format(provider, format, args));

    public void Fatal(object message) => Write("fatal", message);

    public void Fatal(object message, Exception exception) => Write("fatal", message, exception);

    public void FatalFormat(string format, params object[] args) => Write("fatal", string.Format(format, args));

    public void FatalFormat(IFormatProvider provider, string format, params object[] args) =>
        Write("fatal", string.Format(provider, format, args));

    public void Info(object message) => Write("info", message);

    public void Info(object message, Exception exception) => Write("info", message, exception);

    public void InfoFormat(string format, params object[] args) => Write("info", string.Format(format, args));

    public void InfoFormat(IFormatProvider provider, string format, params object[] args) =>
        Write("info", string.Format(provider, format, args));

    public void Warn(object message) => Write("warn", message);

    public void Warn(object message, Exception exception) => Write("warn", message, exception);

    public void WarnFormat(string format, params object[] args) => Write("warn", string.Format(format, args));

    public void WarnFormat(IFormatProvider provider, string format, params object[] args) =>
        Write("warn", string.Format(provider, format, args));
}
