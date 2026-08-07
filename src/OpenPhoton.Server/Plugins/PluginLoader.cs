#nullable disable

using System.Reflection;
using System.Runtime.Loader;
using System.Xml.Linq;
using Photon.Hive.Plugin;

namespace OpenPhoton.Server.Plugins;

/// <summary>One plugin entry from <c>plugin.config</c>.</summary>
public sealed record PluginSettings(string Name, string AssemblyName, string TypeName, Dictionary<string, string> Config)
{
    /// <summary>
    /// Reads Photon's plugin.config.
    /// </summary>
    /// <remarks>
    /// Every attribute other than the three structural ones becomes plugin configuration
    /// verbatim, because that is how plugins are configured: the reference plugin takes its API URL and
    /// shared secret from attributes Photon knows nothing about.
    /// </remarks>
    public static IReadOnlyList<PluginSettings> Parse(string xml)
    {
        var root = XDocument.Parse(xml).Root;
        var settings = root?.Element("PluginSettings");

        if (settings is null) return [];
        if (!string.Equals((string)settings.Attribute("Enabled"), "true", StringComparison.OrdinalIgnoreCase))
            return [];

        var results = new List<PluginSettings>();

        foreach (var element in settings.Element("Plugins")?.Elements("Plugin") ?? [])
        {
            var name = (string)element.Attribute("Name");
            var assembly = (string)element.Attribute("AssemblyName");
            var type = (string)element.Attribute("Type");

            if (string.IsNullOrWhiteSpace(assembly) || string.IsNullOrWhiteSpace(type)) continue;

            var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var attribute in element.Attributes())
            {
                var key = attribute.Name.LocalName;
                if (key is "AssemblyName" or "Type") continue;
                config[key] = attribute.Value;
            }

            results.Add(new PluginSettings(name ?? type, assembly, type, config));
        }

        return results;
    }

    public static IReadOnlyList<PluginSettings> Load(string path) =>
        File.Exists(path) ? Parse(File.ReadAllText(path)) : [];
}

/// <summary>
/// Loads a compiled Photon plugin and produces one instance per room.
/// </summary>
/// <remarks>
/// <para>
/// The assembly is loaded into its own <see cref="AssemblyLoadContext"/> with the plugin
/// directory as the probing path, so a plugin's private dependencies — the reference plugin needs
/// Newtonsoft.Json — resolve from beside it rather than having to sit next to the server.
/// </para>
/// <para>
/// <c>PhotonHivePlugin</c> itself is deliberately <em>not</em> loaded from the plugin
/// directory. The plugin and the host must agree on those types by identity; loading a
/// second copy would give them structurally identical types that fail every cast between
/// them, with an error message that names the same type twice.
/// </para>
/// </remarks>
public sealed class PluginLoader(Action<string> log)
{
    readonly Action<string> _log = log;

    public IPluginFactory Factory { get; private set; }

    public PluginSettings Settings { get; private set; }

    public bool IsLoaded => Factory is not null;

    /// <summary>Loads the first enabled plugin from a config file, if any.</summary>
    public bool TryLoadFrom(string configPath, string pluginDirectory)
    {
        var entries = PluginSettings.Load(configPath);
        if (entries.Count == 0)
        {
            _log($"no plugins configured ({configPath})");
            return false;
        }

        return TryLoad(entries[0], pluginDirectory);
    }

    public bool TryLoad(PluginSettings settings, string pluginDirectory)
    {
        var assemblyPath = Path.Combine(pluginDirectory, settings.AssemblyName);
        if (!File.Exists(assemblyPath))
        {
            _log($"plugin assembly not found: {assemblyPath}");
            return false;
        }

        try
        {
            var context = new PluginLoadContext(assemblyPath);
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));

            var factoryType = assembly.GetType(settings.TypeName)
                              ?? assembly.GetTypes().FirstOrDefault(t =>
                                     typeof(IPluginFactory).IsAssignableFrom(t) && !t.IsAbstract);

            if (factoryType is null)
            {
                _log($"plugin '{settings.Name}': no factory type '{settings.TypeName}'");
                return false;
            }

            if (!typeof(IPluginFactory).IsAssignableFrom(factoryType))
            {
                _log($"plugin '{settings.Name}': {factoryType.FullName} is not an IPluginFactory");
                return false;
            }

            var factory = (IPluginFactory)Activator.CreateInstance(factoryType);

            if (factory is IPluginFactory2 extended)
                extended.SetFactoryHost(new FactoryHost(_log), new FactoryParams { PluginConfig = settings.Config });

            Factory = factory;
            Settings = settings;

            _log($"loaded plugin '{settings.Name}' ({factoryType.FullName}) from {settings.AssemblyName}");
            return true;
        }
        catch (Exception ex)
        {
            _log($"plugin '{settings.Name}' failed to load: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Creates the plugin for one room, or null when none is loaded or it refuses.</summary>
    public PluginBridge CreateFor(Rooms.Room room)
    {
        if (Factory is null) return null;

        try
        {
            var host = new PluginHostAdapter(room, _log);
            var plugin = Factory.Create(host, Settings.Name, Settings.Config, out var error);

            if (plugin is null)
            {
                _log($"    plugin factory returned nothing for '{room.Name}': {error}");
                return null;
            }

            return new PluginBridge(plugin, host, _log);
        }
        catch (Exception ex)
        {
            _log($"    plugin creation failed for '{room.Name}': {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    sealed class FactoryHost(Action<string> log) : IFactoryHost
    {
        readonly Action<string> _log = log;

        public IPluginFiber CreateFiber() => null;

        public IPluginLogger CreateLogger(string loggerName) => new PluginLogger(loggerName, _log);
    }

    /// <summary>Resolves a plugin's private dependencies from its own directory.</summary>
    sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext(isCollectible: false)
    {
        readonly AssemblyDependencyResolver _resolver = new(pluginPath);

        protected override Assembly Load(AssemblyName assemblyName)
        {
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}
