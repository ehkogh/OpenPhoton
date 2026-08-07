using System;
using System.Collections;
using System.Collections.Generic;

namespace Photon.Hive.Plugin
{
    /// <summary>Room-scoped plugin. One instance per room.</summary>
    public interface IGamePlugin
    {
        void BeforeCloseGame(IBeforeCloseGameCallInfo info);

        void BeforeJoin(IBeforeJoinGameCallInfo info);

        void BeforeSetProperties(IBeforeSetPropertiesCallInfo info);

        void OnCloseGame(ICloseGameCallInfo info);

        void OnCreateGame(ICreateGameCallInfo info);

        void OnJoin(IJoinGameCallInfo info);

        void OnLeave(ILeaveGameCallInfo info);

        void OnRaiseEvent(IRaiseEventCallInfo info);

        void OnSetProperties(ISetPropertiesCallInfo info);

        bool OnUnknownType(Type type, ref object value);

        bool SetupInstance(IPluginHost host, Dictionary<string, string> config, out string errorMsg);

        void ReportError(short errorCode, Exception e, object state);

        string Name { get; }

        string Version { get; }

        bool IsPersistent { get; }
    }

    /// <summary>Creates the plugin instance for a room.</summary>
    public interface IPluginFactory
    {
        IGamePlugin Create(IPluginHost gameHost, string pluginName, Dictionary<string, string> config, out string errorMsg);
    }

    /// <summary>Factory variant that receives host services before creating plugins.</summary>
    public interface IPluginFactory2 : IPluginFactory
    {
        void SetFactoryHost(IFactoryHost factoryHost, FactoryParams factoryParams);
    }

    public interface IFactoryHost
    {
        IPluginFiber CreateFiber();

        IPluginLogger CreateLogger(string loggerName);
    }

    /// <summary>Configuration handed to a factory. A plain field, as Photon declares it.</summary>
    public class FactoryParams
    {
        public Dictionary<string, string> PluginConfig;
    }

    public interface IPluginLogger
    {
        void Debug(object message);

        void Debug(object message, Exception exception);

        void DebugFormat(string format, params object[] args);

        void DebugFormat(IFormatProvider provider, string format, params object[] args);

        void Error(object message);

        void Error(object message, Exception exception);

        void ErrorFormat(string format, params object[] args);

        void ErrorFormat(IFormatProvider provider, string format, params object[] args);

        void Fatal(object message);

        void Fatal(object message, Exception exception);

        void FatalFormat(string format, params object[] args);

        void FatalFormat(IFormatProvider provider, string format, params object[] args);

        void Info(object message);

        void Info(object message, Exception exception);

        void InfoFormat(string format, params object[] args);

        void InfoFormat(IFormatProvider provider, string format, params object[] args);

        void Warn(object message);

        void Warn(object message, Exception exception);

        void WarnFormat(string format, params object[] args);

        void WarnFormat(IFormatProvider provider, string format, params object[] args);

        bool IsDebugEnabled { get; }

        bool IsErrorEnabled { get; }

        bool IsFatalEnabled { get; }

        bool IsInfoEnabled { get; }

        bool IsWarnEnabled { get; }

        string Name { get; }
    }

    /// <summary>
    /// Serialises plugin work onto the room's own thread.
    /// </summary>
    /// <remarks>
    /// A room is single-threaded. Plugin work that arrives from elsewhere — an HTTP reply,
    /// a timer — must be posted here rather than touching room state directly, or two
    /// threads end up mutating the same actor list.
    /// </remarks>
    public interface IPluginFiber
    {
        int Enqueue(Action action);

        object CreateTimer(Action action, int firstInMs, int regularInMs);

        object CreateOneTimeTimer(Action action, long firstInMs);

        object CreateOneTimeTimer(Action action, int firstInMs);

        void StopTimer(object timer);
    }

    /// <summary>
    /// An actor, as the plugin sees it.
    /// </summary>
    /// <remarks>
    /// Note the presence is exposed as <see cref="IsActive"/>, not IsInactive. Inverting it
    /// compiles and makes every presence check mean its opposite.
    /// </remarks>
    public interface IActor
    {
        void UpdateSecure(string key, object value);

        int ActorNr { get; }

        PropertyBag<object> Properties { get; }

        string UserId { get; }

        string Nickname { get; }

        bool IsActive { get; }

        object Secure { get; }
    }

    /// <summary>
    /// The room, as the plugin sees it.
    /// </summary>
    /// <remarks>
    /// A plugin never touches the server directly; everything it can observe or change goes
    /// through this. That indirection is what lets a plugin compiled against Photon run
    /// against a different implementation.
    /// </remarks>
    public interface IPluginHost
    {

        void BroadcastEvent(IList<int> recieverActors, int senderActor, byte evCode,
                            Dictionary<byte, object> data, byte cacheOp, SendParameters sendParameters = default);

        void BroadcastEvent(byte target, int senderActor, byte targetGroup, byte evCode,
                            Dictionary<byte, object> data, byte cacheOp, SendParameters sendParameters = default);

        void BroadcastErrorInfoEvent(string message, SendParameters sendParameters = default);

        void BroadcastErrorInfoEvent(string message, ICallInfo info, SendParameters sendParameters = default);

        object CreateOneTimeTimer(Action callback, int dueTimeMs);

        object CreateOneTimeTimer(ICallInfo info, Action callback, int dueTimeMs);

        object CreateTimer(Action callback, int dueTimeMs, int intervalMs);

        SerializableGameState GetSerializableGameState();

        void HttpRequest(HttpRequest request);

        void HttpRequest(HttpRequest request, ICallInfo info);

        void LogDebug(object message);

        void LogError(object message);

        void LogFatal(object message);

        void LogInfo(object message);

        void LogWarning(object message);

        bool SetGameState(SerializableGameState state);

        bool SetProperties(int actorNr, Hashtable properties, Hashtable expected, bool broadcast);

        void StopTimer(object timer);

        bool RemoveActor(int actorNr, string reasonDetail);

        bool RemoveActor(int actorNr, byte reason, string reasonDetail);

        bool TryRegisterType(Type type, byte typeCode, Func<object, byte[]> serializeFunction, Func<byte[], object> deserializeFunction);

        EnvironmentVersion GetEnvironmentVersion();

        IPluginLogger CreateLogger(string name);

        void Enqueue(Action action);

        bool ExecuteCacheOperation(CacheOp operation, out string errorMsg);

        IPluginFiber GetRoomFiber();

        Dictionary<string, object> Environment { get; }

        IList<IActor> GameActors { get; }

        IList<IActor> GameActorsActive { get; }

        IList<IActor> GameActorsInactive { get; }

        string GameId { get; }

        Hashtable GameProperties { get; }

        Dictionary<string, object> CustomGameProperties { get; }

        int MasterClientId { get; }

        bool IsSuspended { get; }

        /// <summary>Photon exposes both spellings; both are part of the contract.</summary>
        bool IsSyspended { get; }
    }

    /// <summary>How a message is delivered.</summary>
    public struct SendParameters
    {
        public byte ChannelId { get; set; }

        public bool Encrypted { get; set; }

        public bool Flush { get; set; }

        public bool Unreliable { get; set; }

        public PluginDeliveryMode? DeliveryMode { get; set; }
    }

    /// <summary>An outbound HTTP call made on the plugin's behalf.</summary>
    public struct HttpRequest
    {
        public string Url { get; set; }

        public string Method { get; set; }

        public string ContentType { get; set; }

        public byte[] DataStream { get; set; }

        public string Accept { get; set; }

        public HttpRequestCallback Callback { get; set; }

        public object UserState { get; set; }

        public bool Async { get; set; }

        public Dictionary<string, string> CustomHeaders { get; set; }
    }

    public delegate void HttpRequestCallback(IHttpResponse response, object userState);

    public interface IHttpResponse
    {
        int Status { get; }

        int HttpCode { get; }

        string Reason { get; }

        byte[] ResponseData { get; }

        string ResponseText { get; }

        object UserState { get; }

        HttpRequest Request { get; }

        ICallInfo CallInfo { get; }

        int WebStatus { get; }

        Dictionary<string, string> Headers { get; }
    }
}
