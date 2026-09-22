# Network diagnostics (`network.sockets`, `network.routes`, `network.neighbors`, `network.interface_stats`, `network.dns_query`, `network.traceroute`, `network.ntp_probe`)

V1.3-D adds senior network-troubleshooting evidence: which process owns a socket, the full routing and
neighbor tables, interface throughput as live rates, a deliberate DNS query, a hop-by-hop path trace and
a clock-offset probe. See [ADR-0035](architecture/adr/0035-sockets-routes-and-network-diagnostics.md)
for the full design and rationale.

This is additive. The existing `network.route` (default gateway and directly connected subnets),
`network.ping`, `network.dns`, `network.interfaces`, `network.connections` and `network.port_check` are
unchanged — see [`bOps.Packages.Network`](../src/packages/bOps.Packages.Network/) for those.

All seven tools are `Read` risk and run automatically under the shipped policy. None shells out to
`netstat`, `ss`, `arp`, `tracert` or `traceroute`, and none reads or exposes a raw DNS query type beyond
A/AAAA/PTR — see rule S1 in `agentic/03-security-rules.md`. On Linux, `network.routes` and
`network.neighbors` run exactly two fixed, argument-listed `ip -j ...` invocations; no other process is
ever started.

## `network.sockets` — who owns this port

| Argument | Range | Default |
|---|---|---|
| `protocol` | `all` / `tcp` / `udp` | `all` |
| `state` | exact, case-insensitive TCP state | — |
| `pid` | owning process id | — |
| `localPort` | 1–65535 | — |
| `limit` | 1–5000 | 500 |

```json
{"schemaVersion":1,"matchedSockets":1,"returnedSockets":1,"truncated":false,
 "pidMappingComplete":true,"pidMappingDetail":null,
 "sockets":[{"protocol":"tcp","addressFamily":"ipv4","localAddress":"0.0.0.0","localPort":8080,
   "remoteAddress":null,"remotePort":null,"state":"listen","pid":4812,"processName":"dotnet"}]}
```

UDP rows report `remoteAddress`, `remotePort` and `state` as `null` — a UDP socket has no fixed peer or
TCP-shaped state. `pidMappingComplete` is a single fact for the whole call: on Linux, mapping a socket
inode to a PID means listing `/proc/<pid>/fd` for every visible process, and a process this identity
cannot list leaves its sockets' `pid`/`processName` `null` and sets `pidMappingComplete: false` with a
`pidMappingDetail`.

## `network.routes` — the full routing table

| Argument | Range | Default |
|---|---|---|
| `addressFamily` | `all` / `ipv4` / `ipv6` | `all` |
| `limit` | 1–5000 | 500 |

```json
{"schemaVersion":1,"matchedRoutes":1,"returnedRoutes":1,"truncated":false,
 "routes":[{"destination":"0.0.0.0","prefixLength":0,"gateway":"192.168.1.1","interfaceName":"eth0",
   "interfaceIndex":4,"metric":100,"addressFamily":"ipv4","protocol":"dhcp"}]}
```

Complements `network.route`, which only reports the default gateway and directly connected subnets.

## `network.neighbors` — the ARP/NDP cache

| Argument | Range | Default |
|---|---|---|
| `addressFamily` | `all` / `ipv4` / `ipv6` | `all` |
| `interfaceName` | exact, case-insensitive | — |
| `limit` | 1–5000 | 500 |

```json
{"schemaVersion":1,"matchedNeighbors":1,"returnedNeighbors":1,"truncated":false,
 "neighbors":[{"ip":"192.168.1.1","mac":"aa:bb:cc:dd:ee:ff","interfaceName":"eth0",
   "state":"reachable","addressFamily":"ipv4"}]}
```

## `network.interface_stats` — two samples, live rates

| Argument | Range | Default |
|---|---|---|
| `interfaceName` | exact, case-insensitive | every interface |
| `sampleMilliseconds` | 100–5000 | 500 |

```json
{"schemaVersion":1,"sampleMilliseconds":500,
 "interfaces":[{"interfaceName":"eth0","bytesReceivedPerSec":125000.5,"bytesSentPerSec":8200.0,
   "packetsReceivedPerSec":140.0,"packetsSentPerSec":30.0,"receiveErrorsPerSec":0.0,
   "sendErrorsPerSec":0.0,"receiveDropsPerSec":0.0,"sendDropsPerSec":0.0,"speedMbps":1000.0,
   "operationalStatus":"Up"}]}
```

A counter the platform does not report is `null`, never zero — the same rule `process.metrics`
established in V1.3-C. Call it twice a minute apart to tell a saturated link from a quiet one.

## `network.dns_query` — a deliberate lookup

| Argument | Range | Default |
|---|---|---|
| `name` | required | — |
| `recordType` | `A` / `AAAA` / `PTR`, required | — |
| `server` | optional explicit server | system resolver |
| `timeout` | 100–10000 ms | 3000 |

```json
{"schemaVersion":1,"name":"example.com","recordType":"A","resolver":"system","success":true,
 "errorMessage":null,"elapsedMilliseconds":18,"answerCount":1,
 "answers":[{"name":"example.com","recordType":"A","data":"93.184.216.34","ttlSeconds":null}]}
```

Without `server`, the system resolver answers and TTL is not reported (the BCL resolver does not expose
it). With `server`, a minimal typed UDP DNS client sends exactly one query and reports the TTL directly
from the reply. No other record type, class or option is ever sent.

## `network.traceroute` — one probe per hop, no external executable

| Argument | Range | Default |
|---|---|---|
| `host` | required | — |
| `maxHops` | 1–64 | 30 |
| `timeout` | 100–5000 ms, per hop | 1000 |
| `addressFamily` | `auto` / `ipv4` / `ipv6` | `auto` |

```json
{"schemaVersion":1,"host":"example.com","resolvedAddress":"93.184.216.34","addressFamily":"ipv4",
 "maxHops":30,"timeoutMilliseconds":1000,"destinationReached":true,"errorMessage":null,"hopCount":6,
 "hops":[{"hop":1,"address":"192.168.1.1","roundTripMilliseconds":1.2,"status":"ttlExpired"},
   {"hop":6,"address":"93.184.216.34","roundTripMilliseconds":14.5,"status":"reached"}]}
```

Implemented with `System.Net.NetworkInformation.Ping` and an increasing TTL/hop-limit — there is no
`tracert`/`traceroute` process anywhere in this implementation. A hop that never replies is `"timeout"`;
one probe per hop in this version.

## `network.ntp_probe` — clock offset, one request

| Argument | Range | Default |
|---|---|---|
| `host` | required | — |
| `timeout` | 100–5000 ms | 2000 |

```json
{"schemaVersion":1,"server":"pool.ntp.org","localUtc":"2026-09-22T10:00:00.1000000+00:00",
 "serverUtc":"2026-09-22T10:00:00.0500000+00:00","offsetMilliseconds":-50.0,
 "roundTripMilliseconds":12.4,"stratum":2,"version":4,"valid":true,"errorMessage":null}
```

A reply is `valid: false` with an `errorMessage` — never a trustworthy-looking offset — when the mode
is not "server", the leap indicator signals an unsynchronized clock, or the stratum is 0 (a
kiss-of-death reply, whose code is included in the message). This is a diagnostic probe: it never
adjusts this host's clock.

## Safety

- No `network.exec`, `network.shell` or any way to run an arbitrary command — permanent, see rule S1.
- No raw DNS query type, class or option beyond A/AAAA/PTR.
- No `tracert`/`traceroute` executable; `network.traceroute` is pure `Ping`-based.
- On Linux, `network.routes` and `network.neighbors` run exactly `ip -j route show[/-6]` and
  `ip -j neighbor show[/-6]` via `ProcessStartInfo.ArgumentList` — no shell, no other argument ever
  appended, no other command ever run.
- Every field you may not read (permission, platform limitation) is `null`, never a silent zero.

## Examples of what to ask

- "What process is listening on port 8080?" → `network.sockets` with `localPort: 8080`.
- "Show me the full routing table, IPv4 only." → `network.routes` with `addressFamily: ipv4`.
- "Is this host resolving its gateway's MAC address?" → `network.neighbors`.
- "Is eth0 dropping packets right now?" → `network.interface_stats`.
- "Resolve example.com against 1.1.1.1." → `network.dns_query` with `server: 1.1.1.1`.
- "Where does the path to example.com break down?" → `network.traceroute`.
- "How far off is this host's clock from pool.ntp.org?" → `network.ntp_probe`.
