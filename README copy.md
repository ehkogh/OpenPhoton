<img src="assets/openphoton.svg" width="64" alt="">

# OpenPhoton

A self-hostable realtime server that speaks Photon Server 5's wire protocol. Photon clients
connect unmodified; plugins built against `PhotonHivePlugin` load and run.

Built for game preservation.

## Build

.NET 8 SDK or newer.

```
dotnet build OpenPhoton.slnx
```

## Run

```
dotnet run --project src/OpenPhoton.Server -- --host 192.168.1.50
```

`--help` lists everything.

`--host` is the address handed to clients mid-connect, and the client dials it itself — so it
has to be reachable *from the client*. Same machine `127.0.0.1`, LAN your LAN IP, internet
your public IP or hostname. Wrong value and the client stalls after picking a room, with
nothing useful in the server log.

## Ports

| Role | TCP | UDP (pre-2022) | UDP (2022+) |
|---|---:|---:|---:|
| NameServer | 4533 | 5058 | 27000 |
| Master | 4530 | 5055 | 27001 |
| GameServer | 4531 | 5056 | 27002 |

All are opened. UDP is Photon's default transport and needs firewall forwarding too. Both UDP
generations are served because a client's own port table overrides whatever the server hands it.

## Plugins

Drop a plugin in `plugins/` with a `plugin.config` in Photon's own format — an existing
deployment's folder copies across unchanged. Plugins compile against `PhotonHivePlugin`
version `1.1.0.24499`, built from `src/OpenPhoton.HivePlugin`.

## Authentication

Off by default. To check against your own service:

```
openphoton --auth-url https://your-api.example/photon/auth
```

Credentials are forwarded there and a verdict read back:

```json
{ "ResultCode": 1, "UserId": "alice", "Nickname": "Alice" }
```

`1` succeeds, anything else refuses. `--require-auth` also rejects clients presenting none.

## Scope

GpBinary V16 and V18 · TCP and reliable UDP · Diffie–Hellman and AES-256 · NameServer →
Master → GameServer handoff · rooms, actors, properties, events, interest groups, event
caching · lobby, listings, random matchmaking · rejoin, TTLs · `PhotonHivePlugin` with plugin
HTTP and timers · custom authentication.

Not supported: WebSocket and WebRTC transports · multi-machine deployments · Photon Chat,
WebRPC, persistent rooms · platform authentication (Steam, Oculus).

## Licence

AGPL-3.0. Clean-room implementation; contains no Exit Games code. Not affiliated with Exit
Games GmbH — "Photon" is their trademark.
