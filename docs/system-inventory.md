# System inventory coverage and limits

V1.1-B adds three read-only observations to the existing cross-platform System package family:

- `system.apps` reports installed applications;
- `system.devices` reports bounded hardware/device inventory;
- `system.info` includes an additive `Hardware model` line.

The Windows and Linux packages produce the same JSON shape for application and device inventories.
Every response includes `status`, `observedItems`, `returnedItems`, `truncated`, `sources` and
`items`. Source status is explicit: `available`, `partial`, `unavailable`, `unsupported` or `notApplicable`.
An empty result with available sources therefore means “observed empty”; it is distinguishable
from a source that could not be read.

## Bounds and deterministic behavior

Both inventory tools accept optional `limit` and `maxOutputBytes` integer arguments. Defaults are
100 items and 32 KiB; hard maxima are 500 items and 64 KiB. Collection itself stops at a finite
2,000-record observation ceiling. Hitting any collection, item or UTF-8 output ceiling sets
`truncated: true`. Values outside the supported ranges fail before collection.

Items are deduplicated by stable platform identity and sorted with ordinal, case-insensitive keys
plus ordinal tie-breakers. Source metadata and nullable fields are emitted explicitly. Tool output
never contains arbitrary commands, registry exports, package-manager command output or an
unbounded sysfs tree.

## Windows sources

`system.apps` reads the standard per-machine and per-user Uninstall registry locations through
both 32-bit and 64-bit registry views where applicable. Entries without `DisplayName` and entries
marked `SystemComponent=1` are omitted. Name, version and publisher come from `DisplayName`,
`DisplayVersion` and `Publisher`; the registry subkey remains the platform identity. Access-denied
or unreadable views are reported as unavailable rather than as an empty application list.

`system.devices` reads the bounded three-level Plug and Play enumeration tree under
`HKLM\SYSTEM\CurrentControlSet\Enum`. It reports instance identity, class/category, friendly name
or device description, manufacturer, hardware/model identifier and problem status when those
values are present. Missing optional fields remain JSON `null`. Registry access can be restricted
by local policy; inaccessible branches are reported through source status.

`system.info` reads `SystemProductName` from the BIOS registry data, falling back to
`BaseBoardProduct`. If neither value is readable, the output says `Hardware model: unknown`.

## Linux sources

`system.apps` supports the dpkg status database at `/var/lib/dpkg/status`, parsed directly without
launching `dpkg`, a shell or another process. Only records whose status is exactly
`install ok installed` are included. Package identity, version, architecture and maintainer are
read from the database. Detected RPM or APK databases are reported as `unsupported`; they are not
silently treated as empty and bOps does not guess at their formats.

`system.devices` reads bounded PCI and USB device directories exposed by sysfs. PCI records use
vendor/device/class attributes and driver binding where available. USB records use idVendor,
idProduct, manufacturer, product and authorization attributes. Missing sysfs buses are reported as
not applicable; permission or I/O failures are unavailable.

`system.info` reads the DMI product name and then the device-tree model as a fallback. If neither
source is available, the output says `Hardware model: unknown`.

These observations do not check for newer drivers, install software, update devices or execute a
package manager. Later Skills may combine the bounded evidence with separately governed Web tools.
