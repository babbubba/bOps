# KNOW-M1-B-FIX01 — Unicode globalization runtime compatibility

Decision: [ADR-0057](adr/README.md#adr-0057) section U.5 (D-049, **Accepted**; this note adds no decision and changes no status).
Scope: documentation, the host runtime configuration, a typed failure and its tests. Offline only: no model, no benchmark, no pack change, no T5–T8.

## The defect

`Directory.Build.props` sets `InvariantGlobalization=true` for every project. Under invariant globalization .NET does not normalize non-ASCII
text: measured on this runtime, `"Å".Normalize(NormalizationForm.FormC)` returns the input unchanged, `"Ａ"` does not fold to `A` under NFKC,
and `IsNormalized` answers `true` for text that is not normalized. So the NFC rule (U2) and the NFKC fold (U6, and the scoring tokens of section S.2)
were vacuous in the repository's default configuration, while the unit tests, which ran with `InvariantGlobalization=false`, passed.

Before this change the policy already refused every non-ASCII description when normalization was missing (`KnowledgeTextViolation.NormalizationUnavailable`),
but the selector kept going: `KnowledgeIndexSelector.Tokenize` called `Normalize(FormKC)` regardless and silently scored with an unfolded text.

## Who decides

The hosting **process**, not a library, decides whether globalization is full. `InvariantGlobalization` becomes `System.Globalization.Invariant` in the
host's `*.runtimeconfig.json`; the `bOps.Knowledge` assembly cannot change it. The environment variable `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`
**wins over** a runtimeconfig that says `false` (measured), so an operator can always force the safe invariant mode without editing files.

## Hosts that can use Knowledge

| Host | Executable? | Reaches Knowledge | Setting after this change |
|---|---|---|---|
| `bOps.Api` (`src/core/bOps.Api`) | yes, Linux and Windows | via `bOps.AgentFramework` → `bOps.Knowledge` (T5 composes it) | `InvariantGlobalization=false` in its project; `HostCulture.PinInvariant()` first in `Program.cs` |
| `bOps.Cli` (`bops`, `src/core/bOps.Cli`) | yes, `win-x64` and `linux-x64`, self-contained | same | same |
| `bOps.Knowledge.Tests` | test host | `bOps.Knowledge` directly | `InvariantGlobalization=false` in its project |
| `bOps.Knowledge.InvariantProbe` (new, test-only) | child process of the previous | `bOps.Knowledge` | `InvariantGlobalization=true`, explicit: it is the invariant runtime under test |
| `bOps.AgentFramework.Tests`, `bOps.Api.Tests`, `bOps.Api.E2EHost`, `bOps.Cli.Tests` | test hosts | reference `bOps.AgentFramework` / `bOps.Api`, but nothing composes Knowledge yet | **unchanged (invariant)**: switch them in the task that makes them exercise discovery (T5 for the host-side tests, T8 for the index tests). *Update (ADR-0058): `bOps.AgentFramework.Tests` already runs with full globalization; `bOps.Cli.Tests` now does too, because it runs the CLI's Knowledge composition in-process (its process tests need nothing: `bops` itself is full-globalization). `bOps.Api*` stay invariant and compose no Knowledge.* |
| `bOps.AppHost` | dev-only Aspire orchestrator (D-002) | none: it starts `bOps.Api` as a separate process with the Api's own runtimeconfig | unchanged |
| Windows helpers (`Firewall.Windows.Helper`, `System.Windows.Updates.Helper`), `TestFixtures.WindowsService`, `System.Linux.TestChild` | yes | none | unchanged (invariant) |

There is no Dockerfile, no container image of bOps and no worker or service host in the repository; `bOps.AppHost` only runs stock Linux containers as
test targets. Release publishing is the self-contained `bOps.Cli` build in `.github/workflows/release.yml`; neither the CI nor the release workflow sets
a `DOTNET_SYSTEM_GLOBALIZATION_*` variable. Nothing in the code reads `GlobalizationMode` or those variables; the only dependent is `KnowledgeTextPolicy`.

### Why only these hosts, and what the change does not touch

The solution default stays invariant. Switching it off for everything would change every package's behaviour and add an ICU dependency to hosts that
do not need it. The two production hosts that will carry Knowledge are switched, with these safeguards, all measured or searched in this change:

* **Culture.** Under full globalization the process would take the machine culture. `HostCulture.PinInvariant()` (in `bOps.Hosting`) pins the invariant
  culture, so formatting and parsing behave exactly as before. There is no `CurrentCulture` / `DefaultThreadCurrentCulture` use anywhere else in `src`.
* **String comparison.** With ICU the invariant culture compares linguistically, unlike the ordinal comparison of invariant mode. Every
  `StartsWith`/`EndsWith`/`IndexOf` with a string argument in `src` already passes a `StringComparison`, and the string sorts use ordinal or a custom
  comparer. One site did not: `FirewallContracts` sorted address, port and profile strings with the default comparer to build an evidence string
  that is hashed. They now pass `StringComparer.Ordinal`, which is exactly what invariant mode did, so the result is byte-identical to before.
* **Analyzer.** CA1308 is silent under invariant globalization and fires otherwise, so `bOps.Api` and `bOps.Cli` carry `NoWarn CA1308`
  ([`suppressions.md`](suppressions.md)); no call site changed.

## Effective Unicode behaviour

With full globalization (`bOps.Api`, `bOps.Cli`, `bOps.Knowledge.Tests`), `KnowledgeTextPolicy.NormalizationAvailable` is `true` and:

| Input | Result |
|---|---|
| Italian accented text in NFC (`perché`, `già`, `città`) | accepted |
| A decomposed accent (`e` + U+0301) | refused: `DisallowedCategory` (U3 refuses the combining mark before U2 is asked) |
| A text of allowed categories that is not NFC (for example U+212B ANGSTROM SIGN) | refused: `NotNormalized` |
| Fullwidth role marker (`ｓｙｓｔｅｍ：`, `Ｕｓｅｒ :`) | refused: `RoleLabel` (NFKC fold) |
| Bidi controls, zero-width characters, BOM | refused: `DisallowedCategory` |
| Goal or query `ｌｏｇ`, `ﬁle`, `perche` + U+0301 | folded by NFKC: they match `log`, `file`, `perché` |

Ranking stays deterministic: integer scores, ties by ref number, independent of candidate order and of the current culture.

## Invariant runtime: fail closed, typed

If the process runs with invariant globalization (a Linux host without ICU forced with `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`, or any host built from
the repository default), `KnowledgeTextPolicy.NormalizationAvailable` is `false`. Probed once per process by actually normalizing U+212B and U+FF21, not
by reading the switch, so it is also right if the switch is set another way.

* **Policy.** ASCII text is judged exactly (ASCII is invariant under every normalization form). Any non-ASCII description or query is refused:
  `NormalizationUnavailable` / `IsValidQuery == false`. Bidi and zero-width characters are still refused as `DisallowedCategory`. Nothing is accepted on
  an unverified check, nothing is repaired.
* **Selector and discover.** `KnowledgeIndexSelector.Select` and the `discover` query path throw `KnowledgeNormalizationUnavailableException`
  (`InvalidOperationException`, diagnostic code `KNOW-NORM-UNAVAILABLE`) **before scoring anything** when the goal, the query or a candidate description
  is non-ASCII. The message is fixed text and never carries goal, query or description. A fully ASCII request is ranked exactly, as a normal run would.
* **No authority.** The failure is raised before any ranking; it creates no candidate, no grant, no trust decision and no pin. The caller's candidate set
  is still the only authority.
* **Does not stop the runtime.** The exception is the existing, documented `DescribeIndex` failure of ADR-0057 section C.7: T5 maps it to
  `Suppressed/SessionFault` with the control sentence "Knowledge is not available now.", the task goes on without the index, and the audit event carries
  the code. This note does not implement that mapping (T5).

## Platform dependencies (ICU / NLS)

| Platform | What full globalization needs | If it is missing |
|---|---|---|
| Linux | `libicu` (any version the .NET runtime supports, found at startup) installed in the image or host. Self-contained publishing does **not** bundle it. `ubuntu-latest` in CI has it. | With `InvariantGlobalization=false` and no ICU, .NET stops at startup with "Couldn't find a valid ICU package". Install `libicu`, or set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` to run without it, accepting that Knowledge is then unavailable. |
| Windows | Nothing to install. .NET uses ICU (`icu.dll`) on Windows 10 1903+ / Server 2019+ and falls back to the operating system's NLS (`NormalizeString`) otherwise. Both implement NFC and NFKC. | not applicable |

I5 of ADR-0057 (NFC results equal on Windows and Linux for characters admitted by U3) is a property of two Unicode implementations (NLS/ICU and
ICU). This change ran its tests on Windows only (WSL was out of scope), so I5 is **not** newly confirmed on Linux; the Linux run stays a gate item of M1.

## Contract clarifications for T5/T6 (documentation only)

**Marker hash and `IndexHash` are two different hashes.** In the renderer, the marker `[knowledge-index v1 sha256=<12 hex>]` carries the first 12 hex
digits of SHA-256 of the **body** only (the entry lines, without marker, closing tag or trailer). `IndexHash` is the full SHA-256 of the **whole** text
of Part D (`marker + body + closing tag + trailer`). The marker is therefore *not* a prefix of `IndexHash`. The sentence in section C.4 ("primi 12 hex di
`IndexHash` calcolato sul testo senza il marker") is read as: the marker is a 12-digit checksum of the body without the marker. What binds the turn to the
runtime is `IndexHash` (`ContentHash`, the audit field, the T6 check `ContentHash == IndexHash == sha256(Content)`); the marker is a visible label.

**Who generates Part C.** Part C (the tail of the system prompt) is produced by the host side, by `KnowledgeIndexRenderer.RenderControl`. Its inputs are a
closed status, a closed suppression code, a function name validated by shape, host-generated refs (`K1`, `K2`…), and integers. No publisher text is an input
of it, so nothing a publisher wrote can appear in Part C or alter its sentences. Publisher text exists only in Part D, as quoted data in a `User` turn,
after admission under the text policy, which refuses the marker characters and the framing words. The trailer that follows Part D is a constant of the
renderer, not pack text. The statement that Part D is third-party data therefore comes from the host, not from the publisher.

## Tests

`tests/bOps.Knowledge.Tests/KnowledgeGlobalizationRuntimeTests.cs`, run in this host and in a child process:

* **Full globalization** (this test host): asserts `NormalizationAvailable` is `true` and the switch is off, with no conditional skip; accented NFC accepted;
  decomposed, ANGSTROM SIGN, fullwidth role marker, bidi, zero-width, word joiner and BOM refused; NFKC applied to tokens (fullwidth, ligature, decomposed accent
  and upper case match); the ranking is identical for shuffled candidates and for every available culture.
* **Invariant runtime**: `tests/bOps.Knowledge.InvariantProbe` is built with `InvariantGlobalization=true`, copied next to the test assembly and run as a
  separate `dotnet` process with the inherited override removed. The test checks that the child's runtimeconfig says invariant and that the child reports:
  `NormalizationAvailable == false`; ASCII judged and ranked exactly; every non-ASCII description and query refused; `DisallowedCategory` for bidi and zero-width;
  `KNOW-NORM-UNAVAILABLE` from `Select` (non-ASCII goal, fullwidth goal, non-ASCII candidate) and from the `discover` query; no echo of input text;
  `AuthorizedTotal` equal to the candidates passed.
