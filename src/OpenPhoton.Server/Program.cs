using System.Reflection;
using OpenPhoton.Realtime.Protocol;
using OpenPhoton.Transport;

namespace OpenPhoton.Server;

/// <summary>OpenPhoton server host.</summary>
public static class Program
{
    const string Usage = """
        OpenPhoton - an open-source Photon Server 5 compatible realtime server.

        usage: openphoton [options] [tcp-port ...]

        Network
          --host <address>      Address handed to clients during the server handoff.
                                Must be reachable BY THE CLIENT, not by this machine.
                                Use your LAN or public IP for anything but localhost.
                                (default: 127.0.0.1)
          --region <name,...>   Regions the name server advertises. (default: us,usw,eu,jp)
          --no-udp              Serve TCP only. UDP is Photon's default transport, so
                                most clients will not connect without it.
          <numbers>             Override the TCP ports. Order: nameserver, master, game.
          --wss-cert <file>     Serve the name server over secure WebSocket, with this
          --wss-key <file>      PEM certificate and key. Current clients reach the name
                                server this way first (AuthOnceWss) and use UDP after.
          --wss-port <port>     Port for it. (default: 443)

        Plugins
          --plugins <dir>       Directory holding plugin.config.
                                (default: 'plugins' beside the executable)

        Authentication
          --auth-url <url>      Verify clients against this endpoint, Photon-style.
                                Off entirely when not given: everyone is let in.
          --require-auth        Also refuse clients that present no credentials.

        Other
          --version             Print the version and exit.
          --help                Print this and exit.

        Default ports
          tcp   nameserver 4533   master 4530   game 4531
          udp   nameserver 5058   master 5055   game 5056     (pre-2022 clients)
                nameserver 27000  master 27001  game 27002    (2022+ clients)

          Both UDP generations are served at once. A client's own port table overrides
          whatever the handoff names, so listening on both is the only way to serve both.
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (args.Contains("--version"))
        {
            Console.WriteLine(Version);
            return 0;
        }

        var publicHost = Option(args, "--host") ?? "127.0.0.1";

        var regions = Option(args, "--region")?.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                          | StringSplitOptions.TrimEntries);

        var endpoints = new ServerEndpoints
        {
            PublicHost = publicHost,
            Regions = regions is { Length: > 0 } ? regions : new ServerEndpoints().Regions,
        };

        var explicitPorts = args
            .Where((a, i) => int.TryParse(a, out _) && (i == 0 || !args[i - 1].StartsWith("--")))
            .Select(int.Parse).ToArray();
        var ports = explicitPorts.Length > 0 ? explicitPorts : endpoints.Ports.ToArray();

        var udpPorts = args.Contains("--no-udp") ? [] : endpoints.UdpPorts.ToArray();

        Console.WriteLine($"OpenPhoton {Version}");
        Console.WriteLine($"  NameServer  tcp :{endpoints.NameServerPort}  udp :{endpoints.NameServerPortUdp}");
        Console.WriteLine($"  Master      tcp :{endpoints.MasterPort}  udp :{endpoints.MasterPortUdp}");
        Console.WriteLine($"  GameServer  tcp :{endpoints.GameServerPort}  udp :{endpoints.GameServerPortUdp}");
        Console.WriteLine($"  advertising host '{publicHost}', regions {string.Join(", ", endpoints.Regions)}");

        if (publicHost is "127.0.0.1" or "localhost")
        {
            Console.WriteLine("  note: only clients on this machine can connect. Pass --host <lan-ip> for others.");
        }

        Console.WriteLine();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var handshake = new HandshakeHandler(Console.WriteLine, endpoints);

        if (Option(args, "--auth-url") is { } authUrl)
        {
            handshake.Authentication = new CustomAuthenticationService(authUrl, Console.WriteLine)
            {
                AllowAnonymous = !args.Contains("--require-auth"),
            };

            Console.WriteLine($"  authenticating against {authUrl}"
                            + $"{(args.Contains("--require-auth") ? " (anonymous clients refused)" : "")}");
        }

        var pluginDirectory = Option(args, "--plugins") is { } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(AppContext.BaseDirectory, "plugins");

        var pluginConfig = Path.Combine(pluginDirectory, "plugin.config");
        if (File.Exists(pluginConfig))
        {
            handshake.LoadPlugin(pluginConfig, pluginDirectory);
        }
        else
        {
            Console.WriteLine($"  no plugin.config at {Path.GetFullPath(pluginConfig)}; running without plugins");
        }

        Console.WriteLine();

        var adapter = new PhotonMessageAdapter(handshake, Console.WriteLine);
        var listener = new PhotonTcpListener(adapter, Console.WriteLine);

        var udp = new Transport.Enet.PhotonUdpListener(handshake, Console.WriteLine);

        var maintenance = handshake.RunMaintenanceAsync(cts.Token);
        var serving = udpPorts.Length > 0 ? udp.RunAsync(udpPorts, cts.Token) : Task.CompletedTask;

        var webSocket = Task.CompletedTask;
        if (Option(args, "--wss-cert") is { } certFile)
        {
            var keyFile = Option(args, "--wss-key")
                          ?? throw new ArgumentException("--wss-cert needs --wss-key alongside it.");
            var wssPort = int.TryParse(Option(args, "--wss-port"), out var p) ? p : endpoints.NameServerPortSecureWebSocket;
            var certificate = LoadCertificate(certFile, keyFile);

            Console.WriteLine($"  NameServer  wss :{wssPort}  ({certificate.Subject}, expires {certificate.NotAfter:yyyy-MM-dd})");
            webSocket = new PhotonWebSocketListener(handshake, certificate, Console.WriteLine).RunAsync([wssPort], cts.Token);
        }

        try
        {
            await Task.WhenAny(listener.RunAsync(ports, cts.Token), serving, webSocket);
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            cts.Cancel();
            await maintenance;
        }

        Console.WriteLine("stopped");
        return 0;
    }

    /// <summary>Loads a PEM certificate and key into something TLS on every OS can use.</summary>
    /// <remarks>
    /// A key read from PEM is ephemeral, and Windows' TLS stack refuses ephemeral keys with an
    /// error about missing credentials that names neither the key nor the format. Round-tripping
    /// through PKCS#12 gives it a key it will accept; elsewhere the round trip is harmless.
    /// </remarks>
    static System.Security.Cryptography.X509Certificates.X509Certificate2 LoadCertificate(string certFile, string keyFile)
    {
        using var pem = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(certFile, keyFile);
        return new System.Security.Cryptography.X509Certificates.X509Certificate2(
            pem.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12));
    }

    /// <summary>Reads <c>--name value</c>, or null when absent or unaccompanied.</summary>
    static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0";
}
