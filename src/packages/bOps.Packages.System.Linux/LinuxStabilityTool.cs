// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>
/// Collects typed machine-stability evidence on Linux (ADR-0041 §3.2): one direct <c>journalctl</c> read of the kernel transport over
/// the window, every record's message matched against fixed package rules (kernel panic, oops/BUG/general protection fault, MCE/EDAC,
/// OOM kill), first match wins. No journal expression or pattern comes from the caller, no shell is involved, and no message text
/// leaves the package. Categories this platform cannot evidence are <c>notApplicable</c> or <c>notCollected</c>, never zero.
/// </summary>
public sealed partial class LinuxStabilityTool : SystemStabilityToolBase
{
    internal const string SourceName = "linux.journald.kernel";

    /// <summary>The coverage probe scope: the same kernel transport the source reads (review note R6).</summary>
    internal static readonly IReadOnlyList<string> ProbeScope = ["_TRANSPORT=kernel"];

    private const string KernelCrashDetail = "A panic is visible only if it reached the persistent journal before the machine stopped.";
    private const string UnexpectedShutdownDetail = "journald has no typed record of an unclean shutdown; bOps does not infer one from missing shutdown messages.";
    private const string DisplayFaultDetail = "GPU fault and reset messages are driver-specific and are not collected by this version.";
    private const string StorageErrorDetail = "Kernel block-layer and controller errors are not collected by this version; use system.events with channel kernel.";
    private const string MinidumpDetail = "Kernel crash dumps (kdump) are not inventoried by this version.";

    private static readonly TimeSpan ProbeCap = TimeSpan.FromSeconds(2);

    private readonly JournalRunner runner;

    /// <summary>Creates the tool over the system <c>journalctl</c>.</summary>
    public LinuxStabilityTool()
        : this(TimeProvider.System, JournalctlProcess.For("journalctl"))
    {
    }

    internal LinuxStabilityTool(TimeProvider clock, JournalRunner runner)
        : base("linux", clock)
    {
        this.runner = runner;
    }

    /// <summary>The fixed category plan of this platform (ADR-0041 §3.2).</summary>
    internal static IReadOnlyList<StabilityCategoryPlan> Categories { get; } =
    [
        new(StabilityCategory.UnexpectedShutdown, CategoryApplicability.NotApplicable, UnexpectedShutdownDetail, []),
        new(StabilityCategory.KernelCrash, CategoryApplicability.Applicable, KernelCrashDetail, [SourceName]),
        new(StabilityCategory.KernelFault, CategoryApplicability.Applicable, null, [SourceName]),
        new(StabilityCategory.HardwareError, CategoryApplicability.Applicable, null, [SourceName]),
        new(StabilityCategory.DisplayFault, CategoryApplicability.NotCollected, DisplayFaultDetail, []),
        new(StabilityCategory.StorageError, CategoryApplicability.NotCollected, StorageErrorDetail, []),
        new(StabilityCategory.MemoryExhaustion, CategoryApplicability.Applicable, null, [SourceName]),
        new(StabilityCategory.Minidump, CategoryApplicability.NotCollected, MinidumpDetail, []),
    ];

    /// <summary>The fixed read of the kernel transport for a window (ADR-0041 §3.2).</summary>
    internal static IReadOnlyList<string> Arguments(StabilityQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return
        [
            "--no-pager",
            "--output=json",
            "--reverse",
            "--utc",
            "--since=" + Time(query.FromUtc),
            "--until=" + Time(query.ToUtc),
            "--lines=" + StabilityLimits.JournalRecordCeiling.ToString(CultureInfo.InvariantCulture),
            "--output-fields=MESSAGE,_TRANSPORT",
            "_TRANSPORT=kernel",
        ];
    }

    /// <inheritdoc />
    protected override async Task<StabilitySnapshot> CollectAsync(StabilityQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var budget = new EvidenceTimeBudget(StabilityLimits.CallTimeout, Clock);
        CoverageStore store;
        using (var probeSlice = budget.Start(2, ct, ProbeCap))
        {
            store = await JournalCoverageProbe.ProbeAsync(runner, ProbeScope, [SourceName], probeSlice);
        }

        var evidence = new List<StabilityEvidence>();
        var scanned = 0;
        var malformed = 0;
        DateTimeOffset? oldestRead = null;
        InventorySourceResult source;
        var truncated = false;
        using (var slice = budget.Start(1, ct))
        {
            try
            {
                var run = await runner(Arguments(query), line =>
                {
                    var kind = JournalRecordParser.TryParse(line, out var record);
                    if (kind == JournalRecordParser.LineKind.Malformed)
                    {
                        malformed++;
                        return true;
                    }

                    if (kind != JournalRecordParser.LineKind.Record)
                    {
                        return true;
                    }

                    scanned++;
                    oldestRead = oldestRead is { } known && known < record!.TimestampUtc ? known : record!.TimestampUtc;
                    if (Classify(record.Message, record.TimestampUtc) is { } classified)
                    {
                        evidence.Add(classified);
                    }

                    return scanned < StabilityLimits.JournalRecordCeiling;
                }, slice.Token);

                (source, truncated) = Describe(run, scanned, malformed, query.FromUtc, oldestRead);
            }
            catch (OperationCanceledException) when (slice.TimedOut)
            {
                source = new InventorySourceResult(SourceName, InventorySourceStatus.Partial, "journalctl did not finish within the time bound and was stopped.") { ExaminedFromUtc = oldestRead };
                truncated = true;
            }
        }

        return new StabilitySnapshot(
            evidence,
            [source],
            Categories,
            new StabilityContext(CategoryApplicability.NotCollected, null, 0, 0),
            new MinidumpInventory(CategoryApplicability.NotCollected, null, null, []),
            truncated)
        { Stores = [store] };
    }

    /// <summary>
    /// Applies the fixed rules of ADR-0041 §3.2 to one kernel message, in order, first match wins: "starts with" is an ordinal,
    /// case-sensitive prefix test on the message as journald returns it. A message matching no rule is not evidence.
    /// </summary>
    internal static StabilityEvidence? Classify(string message, DateTimeOffset timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(message);
        (StabilityCategory Category, string Code, string? Component, string? Severity)? match = message switch
        {
            _ when message.StartsWith("Kernel panic - not syncing", StringComparison.Ordinal) => (StabilityCategory.KernelCrash, "panic", null, null),
            _ when message.StartsWith("Oops:", StringComparison.Ordinal) => (StabilityCategory.KernelFault, "oops", null, null),
            _ when message.StartsWith("BUG: unable to handle", StringComparison.Ordinal) || message.StartsWith("kernel BUG at", StringComparison.Ordinal) => (StabilityCategory.KernelFault, "bug", null, null),
            _ when message.StartsWith("general protection fault", StringComparison.Ordinal) => (StabilityCategory.KernelFault, "general-protection-fault", null, null),
            _ when message.StartsWith("mce: [Hardware Error]: Machine check events logged", StringComparison.Ordinal) => (StabilityCategory.HardwareError, "mce", null, StabilitySeverityClasses.Corrected),
            _ when message.StartsWith("mce: [Hardware Error]:", StringComparison.Ordinal) => (StabilityCategory.HardwareError, "mce", null, StabilitySeverityClasses.Unknown),
            _ when message.StartsWith("EDAC ", StringComparison.Ordinal) && message.Contains(" CE ", StringComparison.Ordinal) => (StabilityCategory.HardwareError, "edac", null, StabilitySeverityClasses.Corrected),
            _ when message.StartsWith("EDAC ", StringComparison.Ordinal) && message.Contains(" UE ", StringComparison.Ordinal) => (StabilityCategory.HardwareError, "edac", null, StabilitySeverityClasses.Uncorrected),
            _ when message.StartsWith("Out of memory: Killed process", StringComparison.Ordinal) => (StabilityCategory.MemoryExhaustion, "oom-kill", KilledProcess(message), null),
            _ when message.StartsWith("Memory cgroup out of memory: Killed process", StringComparison.Ordinal) => (StabilityCategory.MemoryExhaustion, "cgroup-oom-kill", KilledProcess(message), null),
            _ => null,
        };

        return match is { } found
            ? new StabilityEvidence(found.Category, "kernel", null, found.Code, found.Component, found.Severity, EvidenceTimestampKind.Occurred, timestampUtc)
            : null;
    }

    private static (InventorySourceResult Source, bool Truncated) Describe(JournalRun run, int scanned, int malformed, DateTimeOffset fromUtc, DateTimeOffset? oldestRead)
    {
        if (!run.Started)
        {
            return (new(SourceName, InventorySourceStatus.Unavailable, "journalctl could not be started: it is not installed or not executable."), false);
        }

        var reachedCeiling = run.StoppedByConsumer && scanned >= StabilityLimits.JournalRecordCeiling;
        if (!reachedCeiling && run.ExitCode is not 0)
        {
            return (new(SourceName, InventorySourceStatus.Unavailable, $"journalctl exited with code {run.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}."), false);
        }

        var examined = reachedCeiling ? oldestRead : fromUtc;
        if (run.StandardError.Contains("not seeing messages from other users", StringComparison.Ordinal))
        {
            return (new(SourceName, InventorySourceStatus.Partial, "The host identity sees only its own journal: it is not in the adm or systemd-journal group, so kernel messages are missing.") { ExaminedFromUtc = examined }, reachedCeiling);
        }

        if (reachedCeiling)
        {
            return (new(SourceName, InventorySourceStatus.Partial, "The kernel record ceiling was reached before the start of the window.") { ExaminedFromUtc = examined }, true);
        }

        return malformed > 0
            ? (new(SourceName, InventorySourceStatus.Partial, $"{malformed} journal record(s) could not be read and were skipped.") { ExaminedFromUtc = examined }, false)
            : (new(SourceName, InventorySourceStatus.Available, null) { ExaminedFromUtc = examined }, false);
    }

    /// <summary>The process name in <c>Killed process &lt;pid&gt; (&lt;name&gt;)</c>: 1–64 characters without control characters, else <c>null</c>.</summary>
    private static string? KilledProcess(string message)
    {
        var match = KilledProcessPattern().Match(message);
        return match.Success && !match.Groups[1].Value.Any(char.IsControl) ? match.Groups[1].Value : null;
    }

    private static string Time(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    [GeneratedRegex(@"Killed process \d+ \(([^()]{1,64})\)", RegexOptions.CultureInvariant)]
    private static partial Regex KilledProcessPattern();
}
