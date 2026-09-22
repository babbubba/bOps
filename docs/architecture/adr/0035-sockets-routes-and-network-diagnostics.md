# ADR-0035 — Sockets, routes and network diagnostics (`network.sockets`, `network.routes`, `network.neighbors`, `network.interface_stats`, `network.dns_query`, `network.traceroute`, `network.ntp_probe`)

Status: Accepted
Date: 2026-09-22

## Context

The existing `bOps.Packages.Network` package answers "is this host reachable" (`network.ping`), "what
does DNS say" (`network.dns`), "what is listening on my interfaces" (`network.interfaces`), "what TCP
connections exist" (`network.connections`, without an owning process), "is this one port open"
(`network.port_check`) and "what is my default gateway" (`network.route`, a summary, not the routing
table). A senior operator diagnosing a network problem routinely needs five things this package cannot
give:

- **Which process owns this port?** `network.connections` lists sockets but not PIDs — "something is
  listening on 8080, but what?" has no answer today.
- **The full routing table**, not just the default gateway and directly connected subnets.
- **The neighbor cache** (ARP/NDP) — is this host resolving its gateway's MAC at all?
- **Interface health as rates**, not single point-in-time counters — is this NIC dropping packets right
  now?
- **A deliberate DNS query against a specific server**, a hop-by-hop path trace, and a clock-offset
  probe — three diagnostics `network.dns`/`network.ping` cannot express.

All of this needs native OS APIs (`GetExtendedTcpTable`, `GetIpForwardTable2`, `/proc/net/tcp`, `ip`)
that the cross-platform `bOps.Packages.Network` deliberately avoided (see that package's own doc
comment: "the BCL already abstracts the difference" — true for `network.route`'s summary, not true for
owner-PID sockets or the full routing/neighbor tables). This ADR adds the native surface as a sibling
package family, following the `System`/`Process` precedent (ADR-0034), rather than reworking the
existing package's shape or its established plain-text output convention.

## Decision

### A new package family, the existing one untouched

`bOps.Packages.Network.Native.Core`, `.Windows` and `.Linux` are added, matching the `System.Core`/
`.Windows`/`.Linux` shape (rule A8): manifests, argument validation and JSON output formatting live in
`.Core`; each OS package implements only collection. `bOps.Packages.Network` and its six existing tools
are unchanged — this is additive, and the hosts register both packages side by side, exactly one native
package chosen by OS (mirroring how the `System` family is registered).

Two of the seven new tools — `network.dns_query` and `network.traceroute` — and a third,
`network.ntp_probe`, need **no OS-specific collection** at all: the system resolver, `Ping` with
increasing TTL and a UDP client are all BCL. Unlike every other tool family in this codebase, these
three are concrete, non-abstract classes that live directly in `.Core` and take a `platform` string in
their constructor; both native packages construct the same class under their own platform id. This is
new for the codebase (`System.Core` never has a directly-instantiable tool, because every `system.*`/
`process.*` tool genuinely needs OS-specific collection) and is called out explicitly here rather than
silently deviating from the abstract-base-class pattern the rest of this ADR otherwise follows.

### They are plain `ITool`, with no `VerificationSpec`

Every one of the seven is `RiskLevel.Read` and declares no verification, for the identical reason
ADR-0034 gives: verification confirms an intended effect, and a read tool has none to confirm. This
matches the established convention `SystemToolConformance`/`NetworkNativeConformance` assert for every
read-only `system.*`/`process.*`/`network.*` tool in this codebase.

### `network.sockets` — port/PID ownership, argument-filtered

| Argument | Type | Meaning |
|---|---|---|
| `protocol` | Enum `all/tcp/udp`, default `all` | Transport filter. |
| `state` | String, optional | Exact, case-insensitive TCP state. |
| `pid` | Integer, optional | Owning process id filter. |
| `localPort` | Integer 1–65535, optional | Local port filter. |
| `limit` | Integer 1–5000, default 500 | Rows returned. |

Rows: `protocol, addressFamily, localAddress, localPort, remoteAddress, remotePort, state, pid,
processName`. UDP rows report `remoteAddress`, `remotePort` and `state` as `null` — a UDP socket has
neither a fixed peer nor a TCP-shaped state. `pidMappingComplete` is a top-level envelope fact, not a
per-row one: this identity either could map every socket to a process or it could not, and a caller
needs that once, not smeared across hundreds of rows with the same value.

Windows reads `GetExtendedTcpTable`/`GetExtendedUdpTable` with the owner-PID table class, which already
carries the PID — no separate mapping step, no elevated privilege. Linux has no equivalent syscall:
`/proc/net/{tcp,tcp6,udp,udp6}` gives a socket inode, and the inode is mapped to a PID by a bounded scan
of every visible `/proc/<pid>/fd`, matching a `socket:[<inode>]` symlink target. A process directory
this identity cannot list (another user's, without privilege) leaves that process's sockets unmapped and
sets `pidMappingComplete: false` with a detail — an honest gap, never a silent wrong answer.

### `network.routes` — the full table, `network.route` untouched

Rows: `destination, prefixLength, gateway, interfaceName, interfaceIndex, metric, addressFamily,
protocol`. Windows: `GetIpForwardTable2(AF_UNSPEC)` in one call for both families, parsed by fixed byte
offset (see "Windows collection" below). Linux: the one fixed `ip -j route show` / `ip -j -6 route show`
invocation the task spec explicitly permits, via `ProcessStartInfo.ArgumentList` — no shell, no
model-supplied switches, exactly the two argument lists this ADR names and no others. `network.route`
(the existing default-gateway/connected-subnet summary) is not touched, not deprecated and not
redirected: the two tools answer different questions at different costs, and a caller who wants "can
this host reach the internet" still gets the cheaper answer.

### `network.neighbors` — ARP/NDP cache

Rows: `ip, mac, interfaceName, state, addressFamily`. Windows: `GetIpNetTable2(AF_UNSPEC)`, same
byte-offset parsing approach as routes (both embed a `SOCKADDR_INET` union that does not map onto one
C# `StructLayout`). Linux: `ip -j neighbor show` / `ip -j -6 neighbor show`, the second and last fixed
`ip` invocation this ADR permits.

### `network.interface_stats` — two samples, one difference

| Argument | Type | Meaning |
|---|---|---|
| `interfaceName` | String, optional | Exact, case-insensitive filter. Omit for every interface. |
| `sampleMilliseconds` | Integer 100–5000, default 500 | Interval between the two samples. |

Rows: `interfaceName, bytesReceivedPerSec, bytesSentPerSec, packetsReceivedPerSec,
packetsSentPerSec, receiveErrorsPerSec, sendErrorsPerSec, receiveDropsPerSec, sendDropsPerSec,
speedMbps, operationalStatus`. The two-sample-and-difference shape, the shared `Compute` rate
arithmetic and "a counter this platform does not report is null, never zero" are the same pattern
`process.metrics` established in ADR-0034, reused rather than reinvented. Both platforms use "or
equivalent BCL counters" as the task spec explicitly allows: Windows uses
`NetworkInterface.GetIPStatistics()` rather than a second `GetIfEntry2` P/Invoke surface (one native
struct family per package is enough); Linux reads `/proc/net/dev` for the counters and
`/sys/class/net/<iface>/{speed,operstate}` for link speed and operational status.

### `network.dns_query` — system resolver, or a minimal typed UDP client

| Argument | Type | Meaning |
|---|---|---|
| `name` | String, required | Hostname (A/AAAA) or IP address (PTR). |
| `recordType` | Enum `A/AAAA/PTR`, required | The only three types ever sent. |
| `server` | String, optional | An explicit server; omit for the system resolver. |
| `timeout` | Integer 100–10000 ms, default 3000 | Query timeout. |

Without `server`, `System.Net.Dns` answers — no new code, no new surface, whatever the platform
resolver is configured to trust. With `server`, `DnsUdpClient` sends exactly one UDP/53 query for
exactly one of A/AAAA/PTR, IN class, and parses exactly one reply, including
compression-pointer following in names (RFC 1035 §4.1.4), because a real reply routinely points a
resource record's name back at the question rather than repeating it. There is no raw query type,
class, opcode or option field exposed to the model — the three record types above are the entire
surface, by construction, not by a validation check that could later be loosened.

### `network.traceroute` — `Ping` with increasing TTL, never an executable

| Argument | Type | Meaning |
|---|---|---|
| `host` | String, required | Destination hostname or address. |
| `maxHops` | Integer 1–64, default 30 | Hop-limit ceiling. |
| `timeout` | Integer 100–5000 ms, default 1000 | Per-hop timeout. |
| `addressFamily` | Enum `auto/ipv4/ipv6`, default `auto` | Which family to resolve and probe. |

One ICMP echo per hop via `System.Net.NetworkInformation.Ping.SendPingAsync(address, timeout, buffer,
new PingOptions(ttl, dontFragment: true), ct)`, TTL from 1 upward, stopping at `IPStatus.Success`
(destination reached), `maxHops`, or an unresolvable/invalid host. Every hop is recorded — `reached`,
`ttlExpired` (a transit router replied and is named), `timeout`, or `error` — because a caller
diagnosing packet loss needs to see where the replies stop, not just whether the destination answered.
There is no `tracert.exe`/`traceroute` process anywhere in this implementation; `Ping` is cross-platform
BCL, so, like the DNS and NTP tools, this needs no OS-specific collection at all.

### `network.ntp_probe` — one SNTP request, rejected if it cannot be trusted

| Argument | Type | Meaning |
|---|---|---|
| `host` | String, required | The NTP server. |
| `timeout` | Integer 100–5000 ms, default 2000 | Query timeout. |

`SntpClient` implements RFC 4330 client mode over UDP/123: one request, the classic four timestamps
(`T1`–`T4`), `offset = ((T2-T1)+(T3-T4))/2`, `delay = (T4-T1)-(T3-T2)`. A reply is `valid: false` with a
detail — never a trustworthy-looking offset — when the mode is not 4 (server), the leap indicator signals
an unsynchronized clock (`LI == 3`), or the stratum is 0 (a kiss-of-death packet, whose four-character
kiss code is surfaced in the detail). This is a diagnostic client, not a clock source: it never adjusts
this host's clock and is not a substitute for a real NTP daemon.

### Arguments are rejected, never clamped

Every out-of-range value is refused with a message the model can act on, following D-028 — the same
rule ADR-0034 restates for `process.*`. `pid` and `localPort` are validated the same way `process.*`'s
`pid` is.

### Windows collection: byte offsets, not a marshalled struct

`MIB_IPFORWARD_ROW2` and `MIB_IPNET_ROW2` both embed a `SOCKADDR_INET` union (`sockaddr_in` or
`sockaddr_in6`, chosen by an address family field at runtime), which has no single C# `StructLayout`
representation — the same reason ADR-0034 read `/proc/<pid>/stat` and Win32 counters directly rather
than forcing every shape into one type. Both structs are read by fixed byte offset from unmanaged
memory instead, with the offsets documented as named constants and derived from the published struct
layouts (`iprtrmib.h`/`netioapi.h`), including the 4-byte alignment padding `NumEntries`/`InterfaceLuid`
require on x64. `GetExtendedTcpTable`/`GetExtendedUdpTable`'s owner-PID row structs
(`MIB_TCPROW_OWNER_PID`, `MIB_TCP6ROW_OWNER_PID`, `MIB_UDPROW_OWNER_PID`, `MIB_UDP6ROW_OWNER_PID`) are
simpler — no union, only `DWORD`s and fixed byte arrays — and are read the same way for consistency.
Every `iphlpapi.dll` entry point is declared via `LibraryImport`, never `DllImport`, matching
`bOps.Packages.System.Windows`'s kernel32 calls (ADR-0034 precedent), with
`[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]` on each one.

### Linux collection: `/proc` plus exactly two fixed `ip -j` invocations

Sockets: `/proc/net/{tcp,tcp6,udp,udp6}`, PID mapping via `/proc/<pid>/fd`. Interface stats:
`/proc/net/dev` plus `/sys/class/net`. Routes and neighbors have no `/proc` table with the fields this
task asks for (protocol, metric, neighbor state as text) that would not itself amount to re-deriving
`ip`'s own kernel-netlink parsing — the task spec explicitly permits exactly two fixed invocations for
exactly this reason: `ip -j route show` / `ip -j -6 route show` and `ip -j neighbor show` /
`ip -j -6 neighbor show`, run via `ProcessStartInfo.ArgumentList` (`UseShellExecute: false`), parsed as
JSON with `System.Text.Json`. No other argument is ever appended, no model input ever reaches the
argument list, and no other command is ever run — this is not a generic process-execution surface (rule
S1); it is two named, fixed, non-configurable invocations, exactly as `docker.build`'s fixed daemon call
was the boundary in ADR-0033.

### No new privilege

Exactly the same posture as ADR-0034: bOps asks for no extra privilege to see more. A gap this identity
cannot read is reported as a gap (`pidMappingComplete: false`, a `null` field, an empty routing table
read as "genuinely empty" only when the read itself succeeded), never silently widened or narrowed.

## Alternatives considered

- **A generic `network.exec` or shelling out to `netstat`/`ss`/`route`/`arp`/`tracert`.** Rejected
  permanently: rule S1, and the whole point of this milestone is to make it unnecessary.
- **Reworking `bOps.Packages.Network` in place instead of a sibling package.** Rejected: that package's
  plain-text output and cross-platform-BCL-only shape is an established, tested convention with its own
  tools already in production use; forcing an OS split and a JSON shape onto it would be a breaking
  change to six existing tools for a capability only some of them need.
- **`GetIfEntry2` via a full P/Invoke struct for interface stats.** Rejected for this batch: the task
  spec explicitly allows "or equivalent BCL counters", and `NetworkInterface.GetIPStatistics()` already
  gives every counter this shape needs without a third P/Invoke struct family in the same package.
  Revisit if a counter this ADR needs turns out to be genuinely absent from the BCL surface.
- **A full DNS/NTP library dependency (e.g. `DnsClient.NET`) instead of a hand-written minimal
  client.** Rejected: rule S1's spirit extends to data formats, not only execution — a general DNS
  client exposes query types, classes and options this task explicitly does not want on the surface at
  all; a minimal client that can only ever send A/AAAA/PTR is a smaller, more auditable dependency-free
  addition to `.Core`.
- **Letting `network.traceroute` send parallel probes per hop, or multiple probes per hop.** Rejected:
  the task spec asks for "one probe per hop in first version"; a richer traceroute (multiple probes,
  packet-loss percentage per hop) is a later, separately-scoped change if the evidence gap shows up in
  practice.
- **Treating NTP stratum 16 as valid with a warning.** Rejected: stratum 16 means "this server's own
  clock is unsynchronized" per RFC 5905; reporting an offset computed against it as `valid: true` would
  be exactly the "reading twice on a live machine and calling it confirmed" mistake ADR-0034 warns
  against for verification, applied here to trust in a reading.
- **A single combined P/Invoke struct definition using `[StructLayout(Explicit)]` for the
  `SOCKADDR_INET` union.** Rejected: `[StructLayout(Explicit)]` would still need runtime branching on
  the family field to know which overlapping fields are valid, which is exactly what the fixed-offset
  approach already does explicitly — the explicit layout would add ceremony without removing the branch.

## Consequences

- The agent can map a listening or connected socket to its owning process, read the full routing and
  neighbor tables, see per-interface throughput and error rates as live rates, run a deliberate DNS
  query against a specific server, trace a path hop by hop, and estimate this host's clock offset from a
  named server — through typed, bounded, `Read` tools, correlatable with `process.*` (by PID) and
  `system.events`.
- `bOps.Packages.Network.Native.Windows` P/Invokes `iphlpapi.dll` directly (no new NuGet dependency);
  `bOps.Packages.Network.Native.Linux` adds no dependency and runs exactly the two named `ip`
  invocations, nothing else.
- On Linux, an unprivileged host cannot list another user's `/proc/<pid>/fd`, so some sockets' PID stays
  unmapped and `pidMappingComplete` is `false` — a visible gap, not a silent zero, exactly like
  ADR-0034's process I/O visibility gap.
- `network.traceroute` and `network.ntp_probe` each cost one round trip (plus up to `maxHops` for
  traceroute) per call; their timeouts and hop bounds are what keep that inside the default tool
  timeout.
- No change to `bOps.Abstractions`, policy, runtime, persistence or audit.
