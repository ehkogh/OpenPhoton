using System;
using System.Collections;
using System.Collections.Generic;

namespace Photon.Hive.Plugin
{
    /// <summary>
    /// Default plugin: every callback continues the operation unchanged.
    /// </summary>
    /// <remarks>
    /// Plugins override only what they care about, so the base must resolve every call. A
    /// callback that fell through without calling <see cref="ICallInfo.Continue"/> would
    /// hang the operation, which is why none of these are empty bodies.
    /// </remarks>
    public class PluginBase : IGamePlugin
    {
        public static Version PluginsVersion = new Version(1, 1, 0, 24499);

        public static Version BuildVersion = new Version(1, 1, 0, 24499);

        protected bool fireAssert;

        public virtual string Name => "Default";

        public virtual string Version => "1.0";

        public virtual bool IsPersistent => false;

        /// <summary>Public, not protected: plugins are documented to read it from outside.</summary>
        public IPluginHost PluginHost { get; set; }

        public string AppVersion { get; }

        public string AppId { get; }

        public string Region { get; }

        public string Cloud { get; }

        public string EnvironmentVerion { get; }

        /// <summary>When set, callbacks that fail to resolve a call are treated as errors.</summary>
        public bool UseStrictMode { get; set; }

        public virtual bool SetupInstance(IPluginHost host, Dictionary<string, string> config, out string errorMsg)
        {
            PluginHost = host;
            errorMsg = null;
            return true;
        }

        public virtual void OnSetPropertiesFailed(ISetPropertiesFailedCallInfo info) => info.Continue();

        /// <summary>Hook for deserialising a type the protocol does not know.</summary>
        public virtual bool OnUnknownType(Type type, ref object value) => false;

        protected virtual void OnChangeMasterClientId(int oldId, int newId) { }

        protected virtual void StrictModeCheck(ICallInfo info) { }

        protected void BroadcastEvent(byte code, Dictionary<byte, object> data) =>
            PluginHost?.BroadcastEvent(ReciverGroup.All, 0, 0, code, data, CacheOperations.DoNotCache, default);

        public virtual void OnCreateGame(ICreateGameCallInfo info) => info.Continue();

        public virtual void BeforeJoin(IBeforeJoinGameCallInfo info) => info.Continue();

        public virtual void OnJoin(IJoinGameCallInfo info) => info.Continue();

        public virtual void OnLeave(ILeaveGameCallInfo info) => info.Continue();

        public virtual void BeforeCloseGame(IBeforeCloseGameCallInfo info) => info.Continue();

        public virtual void OnCloseGame(ICloseGameCallInfo info) => info.Continue();

        public virtual void OnRaiseEvent(IRaiseEventCallInfo info) => info.Continue();

        public virtual void BeforeSetProperties(IBeforeSetPropertiesCallInfo info) => info.Continue();

        public virtual void OnSetProperties(ISetPropertiesCallInfo info) => info.Continue();

        public virtual void OnUnexpectedException(string method, Exception e) =>
            PluginHost?.LogError($"Unexpected exception in {method}: {e}");

        protected virtual void ReportError(short errorCode, Exception exception, object state) =>
            PluginHost?.LogError($"Plugin error {errorCode}: {exception}");

        void IGamePlugin.ReportError(short errorCode, Exception exception, object state) =>
            ReportError(errorCode, exception, state);
    }

    /// <summary>
    /// Base factory. Plugins override <see cref="CreatePlugin"/> and nothing else.
    /// </summary>
    /// <remarks>
    /// Abstract with an abstract <c>CreatePlugin</c>, matching Photon: the host calls
    /// <see cref="Create"/>, which wires up the instance and delegates the one decision a
    /// plugin actually makes. The reference plugin overrides exactly this method, so its signature and
    /// abstractness are part of the contract rather than an implementation choice.
    /// </remarks>
    public abstract class PluginFactoryBase : IPluginFactory2, IPluginFactory
    {
        protected IFactoryHost factoryHost;

        protected FactoryParams FactoryParams { get; private set; }

        public void SetFactoryHost(IFactoryHost fHost, FactoryParams factoryParams)
        {
            factoryHost = fHost;
            FactoryParams = factoryParams;
        }

        public abstract IGamePlugin CreatePlugin(string pluginName);

        public IGamePlugin Create(IPluginHost gameHost, string pluginName, Dictionary<string, string> config, out string errorMsg)
        {
            var plugin = CreatePlugin(pluginName);
            if (plugin is null)
            {
                errorMsg = $"Factory returned no plugin for '{pluginName}'.";
                return null;
            }

            return plugin.SetupInstance(gameHost, config, out errorMsg) ? plugin : null;
        }
    }

    /// <summary>Plugin returned when a factory fails, so a broken plugin surfaces rather than vanishing.</summary>
    public class ErrorPlugin : PluginBase
    {
        public ErrorPlugin(string message) => Message = message;

        public string Message { get; }

        public override string Name => "ErrorPlugin";
    }

    /// <summary>
    /// Property store with change notification and check-and-set updates.
    /// </summary>
    /// <remarks>
    /// Generic over the <em>key</em>, not the value — <c>PropertyBag&lt;object&gt;</c> means
    /// object keys holding object values. Reading it as a value type compiles against
    /// nothing a plugin actually writes.
    /// </remarks>
    public class PropertyBag<TKey>
    {
        readonly Dictionary<TKey, Property<TKey>> _values = new Dictionary<TKey, Property<TKey>>();

        public PropertyBag() { }

        public PropertyBag(IEnumerable<KeyValuePair<TKey, object>> values)
        {
            foreach (var pair in values) Set(pair.Key, pair.Value);
        }

        public PropertyBag(IDictionary values) => SetProperties(values);

        public int Count => _values.Count;

        public bool DeleteNullProps { get; set; }

        public int TotalSize => _values.Count;

        public event EventHandler<PropertyChangedEventArgs<TKey>> PropertyChanged;

        public IDictionary<TKey, Property<TKey>> AsDictionary() => new Dictionary<TKey, Property<TKey>>(_values);

        public IList<Property<TKey>> GetAll() => new List<Property<TKey>>(_values.Values);

        public Hashtable GetProperties()
        {
            var table = new Hashtable();
            foreach (var pair in _values) table[pair.Key] = pair.Value.Value;
            return table;
        }

        public Hashtable GetProperties(IList<TKey> propertyKeys) => GetProperties((IEnumerable)propertyKeys);

        public Hashtable GetProperties(IEnumerable<TKey> propertyKeys) => GetProperties((IEnumerable)propertyKeys);

        public Hashtable GetProperties(IEnumerable propertyKeys)
        {
            var table = new Hashtable();
            if (propertyKeys is null) return GetProperties();

            foreach (var key in propertyKeys)
                if (key is TKey typed && _values.TryGetValue(typed, out var property))
                    table[typed] = property.Value;

            return table;
        }

        public Property<TKey> GetProperty(TKey key) => _values.TryGetValue(key, out var property) ? property : null;

        public bool SetProperty(TKey key, object value)
        {
            Set(key, value);
            return true;
        }

        public bool SetProperty(TKey key, object value, KeyValuePair<int, int>? keyValueSizes) => SetProperty(key, value);

        public void Set(TKey key, object value)
        {
            if (value is null && DeleteNullProps) _values.Remove(key);
            else _values[key] = new Property<TKey>(key, value);

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs<TKey>(key));
        }

        public void Set(TKey key, object value, KeyValuePair<int, int>? keyValueSizes) => Set(key, value);

        public void SetProperties(IDictionary values) => SetProperties(values, out _);

        public void SetProperties(IDictionary values, out bool changed)
        {
            changed = false;
            if (values is null) return;

            foreach (DictionaryEntry entry in values)
            {
                if (entry.Key is not TKey key) continue;
                Set(key, entry.Value);
                changed = true;
            }
        }

        public void SetProperties(IDictionary values, out bool changed, Dictionary<object, KeyValuePair<int, int>> metaData) =>
            SetProperties(values, out changed);

        public void SetProperties(IDictionary<TKey, object> values) => SetProperties(values, out _);

        public void SetProperties(IDictionary<TKey, object> values, out bool changed)
        {
            changed = false;
            if (values is null) return;

            foreach (var pair in values)
            {
                Set(pair.Key, pair.Value);
                changed = true;
            }
        }

        public bool CompareProperties(IDictionary expectedValues, out string debugMessage)
        {
            debugMessage = null;
            if (expectedValues is null) return true;

            foreach (DictionaryEntry entry in expectedValues)
            {
                if (entry.Key is not TKey key) continue;
                var current = _values.TryGetValue(key, out var property) ? property.Value : null;

                if (!PropertyValueComparer.AreEqual(current, entry.Value))
                {
                    debugMessage = $"Property '{key}' does not match the expected value.";
                    return false;
                }
            }
            return true;
        }

        public bool SetPropertiesCAS(IDictionary values, IDictionary expectedValues, out string debugMessage)
        {
            var changed = false;
            return SetPropertiesCAS(values, expectedValues, ref changed, out debugMessage);
        }

        public bool SetPropertiesCAS(IDictionary values, IDictionary expectedValues, ref bool valuesChanged, out string debugMessage)
        {
            if (!CompareProperties(expectedValues, out debugMessage)) return false;

            SetProperties(values, out var changed);
            valuesChanged = changed;
            return true;
        }

        public bool SetPropertiesCAS(IDictionary values, IDictionary expectedValues, ref bool valuesChanged,
                                     out string debugMessage, Dictionary<object, KeyValuePair<int, int>> metaData) =>
            SetPropertiesCAS(values, expectedValues, ref valuesChanged, out debugMessage);

        public bool SetPropertiesCAS(IDictionary<TKey, object> values, IDictionary<TKey, object> expectedValues, out string debugMessage)
        {
            var expected = new Hashtable();
            if (expectedValues is not null) foreach (var pair in expectedValues) expected[pair.Key] = pair.Value;

            var supplied = new Hashtable();
            if (values is not null) foreach (var pair in values) supplied[pair.Key] = pair.Value;

            return SetPropertiesCAS(supplied, expected, out debugMessage);
        }

        public bool TryGetValue(TKey key, out object value)
        {
            if (_values.TryGetValue(key, out var property))
            {
                value = property.Value;
                return true;
            }
            value = null;
            return false;
        }

        public void Clear() => _values.Clear();
    }

    public class PropertyChangedEventArgs<TKey> : EventArgs
    {
        public PropertyChangedEventArgs(TKey key) => Key = key;

        public TKey Key { get; }
    }

    /// <summary>A single property, as exposed to plugins.</summary>
    public class Property<TKey>
    {
        public Property(TKey key, object value)
        {
            Key = key;
            Value = value;
        }

        public TKey Key { get; }

        public object Value { get; set; }
    }

    /// <summary>Room state that can be persisted and restored.</summary>
    public class SerializableGameState
    {
        public string GameId { get; set; }

        public int ActorCounter { get; set; }

        public List<SerializableActor> ActorList { get; set; } = new List<SerializableActor>();

        public Dictionary<string, object> CustomProperties { get; set; } = new Dictionary<string, object>();

        public int MaxPlayers { get; set; }

        public bool IsOpen { get; set; }

        public bool IsVisible { get; set; }

        public int PlayerTTL { get; set; }

        public int EmptyRoomTTL { get; set; }

        public bool CheckUserOnJoin { get; set; }

        public bool DeleteCacheOnLeave { get; set; }

        public bool SuppressRoomEvents { get; set; }
    }

    public class SerializableActor
    {
        public int ActorNr { get; set; }

        public string UserId { get; set; }

        public string Nickname { get; set; }

        public bool IsInactive { get; set; }

        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();

        public int DeactivationTime { get; set; }
    }
}
