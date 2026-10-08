# Kernel-dump debugger output fixtures (synthetic)

These files are **synthetic**. No line in them was captured from a real machine, and none of them contains
private or user data: module names such as `synthdrv`, device identifiers such as `SYNTHETIC0001`, addresses and
hashes were invented for the tests.

Why synthetic: when `system.dump_analyze` was written (ADR-0048, 2026-10-08), Debugging Tools for Windows was not
installed on the development workstation, and no shareable kernel dump was available. The operator chose a text-first V1
that parses only the marker-delimited output of the fixed command sequence.

Provenance of the shapes:

- `!analyze -v` field names and layout (`NAME (code)` header, `Arg1:`–`Arg4:`, `KEY: value` fields, the `STACK_TEXT:`
  block, `SYMBOL_NAME`, `MODULE_NAME`, `IMAGE_NAME`, `BUCKET_ID`): Microsoft Learn, *Using the !analyze Extension* and
  the `!analyze` reference. `BUGCHECK_CODE`, `BUGCHECK_P1`–`P4`, `FAILURE_BUCKET_ID`, `FAILURE_ID_HASH` and the
  `ChildSP RetAddr : Args : Call Site` stack layout are the field names printed by current debugger releases.
- `.bugcheck` (`Bugcheck code` / `Arguments`) and `lm` (`start end module name (symbol state)`): Microsoft Learn command
  references.
- `!blackboxpnp` / `!blackboxbsd`: their output layout is **not** documented. The fixtures use an assumed `Name : value`
  layout; the parser only decides availability, keeps bounded excerpt lines and extracts device instance identifiers,
  so it does not depend on that layout.

`{NONCE}` is replaced by the test with the run's marker nonce.

Replace or extend these with sanitized real captures once kd output from a real kernel dump has been reviewed
(ADR-0048 follow-up). Until then, parser behavior against real debugger releases is **not** validated.
