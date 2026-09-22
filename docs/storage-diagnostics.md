# Storage diagnostics (`storage.disks`, `storage.partitions`, `storage.mounts`, `storage.io`, `storage.health`)

V1.3-E adds one read-only storage evidence family on Windows and Linux. It complements the existing
`system.disk` and `system.io` quick summaries; those tools and their output remain unchanged.

## Tools and bounds

| Tool | Purpose | Filters |
|---|---|---|
| `storage.disks` | Physical identity, model, serial, bus/media, size, sector geometry and access flags | `limit`, `maxOutputBytes` |
| `storage.partitions` | Disk-to-partition topology, offsets, sizes, type and boot/read-only flags | `diskId`, `limit`, `maxOutputBytes` |
| `storage.mounts` | Filesystem capacity, inode capacity, mount options and read-only state | `limit`, `maxOutputBytes` |
| `storage.io` | Two-sample IOPS, throughput, latency, queue depth and utilization | `device`, `sampleMilliseconds`, `maxOutputBytes` |
| `storage.health` | Normalized hardware health and selected SMART evidence | `device`, `limit`, `maxOutputBytes` |

List calls default to 200 rows and allow at most 2,000. Output defaults to 64 KiB and allows at
most 256 KiB. `storage.io` samples for 1,000 ms by default and accepts 500–5,000 ms. Out-of-range
arguments fail; they are never clamped silently.

Every output is a single JSON object with `schemaVersion`, `matched`, `returned`, `truncated` and
the relevant row array. Missing evidence is `null` or `unknown`, never an invented zero or a
healthy-looking default.

## Platform sources

Windows uses `Win32_DiskDrive`, `Win32_DiskPartition` and `Win32_LogicalDisk`. I/O comes from
`Win32_PerfRawData_PerfDisk_PhysicalDisk`; an instance is associated with a disk only when its
numeric prefix maps unambiguously to an enumerated disk. Health prefers `MSFT_PhysicalDisk` in
`root\Microsoft\Windows\Storage`. A missing provider or insufficient permission yields
`health: "unknown"` with a bounded detail.

Linux reads `/sys/block` for devices and partitions, `/proc/self/mountinfo` plus `statvfs(3)` for
filesystem bytes and inodes, and samples `/proc/diskstats` twice for live rates. If `smartctl` is
installed, the package starts it directly with the fixed argument sequence `-j -a -- <device>`;
`<device>` must come from `/sys/block`. There is no shell, no model-supplied switch and no raw SMART
JSON in output. Without `smartctl`, base sysfs state is returned with `smartAvailable: false` and
`health: "unknown"`.

## Health semantics

`health` is exactly `healthy`, `warning`, `critical` or `unknown`. `unknown` means the platform did
not provide enough evidence; it is not success. The tool exposes only selected fields:
temperature, power-on hours, media errors, reallocated sectors and wear percentage. Private data,
raw WMI objects and raw SMART payloads never cross the tool boundary.

## Operational examples

- Correlate database latency with `storage.io`, filtering the device that backs the database mount.
- Find capacity pressure with `storage.mounts`; on Linux check both byte and inode percentages.
- Trace a filesystem to a partition and physical disk with `storage.mounts`, `storage.partitions`
  and `storage.disks`.
- Treat `storage.health` warnings as evidence to investigate, not as permission for an automated
  destructive action. All five tools are read-only.
