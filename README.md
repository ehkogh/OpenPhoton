<img src="assets/openphoton.svg" width="64" alt="">

# OpenPhoton

A self-hostable realtime server implementing the Photon Server 5 protocol.

## Build

Requires .NET 8 SDK or newer.

```sh
dotnet build src/OpenPhoton.Server
```

## Run

```sh
dotnet run --project src/OpenPhoton.Server -- --host 192.168.1.50
```

Use `--help` to see all options.

`--host` must be an address reachable by connecting clients:

* Same machine: `127.0.0.1`
* LAN: your LAN IP
* Internet: your public IP or hostname

## Ports

| Role       |  TCP | UDP (pre-2022) | UDP (2022+) |
| ---------- | ---: | -------------: | ----------: |
| NameServer | 4533 |           5058 |       27000 |
| Master     | 4530 |           5055 |       27001 |
| GameServer | 4531 |           5056 |       27002 |

All ports are opened by the server. If hosting across a firewall or NAT, forward the required TCP and UDP ports.

Both UDP port generations are supported for compatibility with different Photon client versions.

## Plugins

Place plugins in `plugins/` with a Photon-format `plugin.config`.

Existing Photon plugin directories can be copied over unchanged.

Plugins target `PhotonHivePlugin` version `1.1.0.24499`, provided by `src/OpenPhoton.HivePlugin`.

## Authentication

Authentication is disabled by default.

To use a custom authentication service:

```sh
openphoton --auth-url https://your-api.example/photon/auth
```

The service should return a response such as:

```json
{
  "ResultCode": 1,
  "UserId": "alice",
  "Nickname": "Alice"
}
```

`ResultCode: 1` accepts the client; any other value rejects it.

Use `--require-auth` to also reject clients that provide no authentication credentials.

## Compatibility

Supported:

* GpBinary V16 and V18
* TCP and reliable UDP
* Diffie–Hellman and AES-256
* NameServer → Master → GameServer handoff
* Rooms, actors, properties, events, interest groups, and event caching
* Lobbies, room listings, and random matchmaking
* Rejoin and TTLs
* `PhotonHivePlugin`, including HTTP and timers
* Custom authentication

Not supported:

* WebSocket or WebRTC transports
* Multi-machine deployments
* Photon Chat
* WebRPC
* Persistent rooms
* Platform authentication such as Steam or Oculus

## License

AGPL-3.0.

OpenPhoton is a clean-room implementation and contains no Exit Games code. It is not affiliated with Exit Games GmbH. "Photon" is a trademark of Exit Games GmbH.
