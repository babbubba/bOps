// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>
/// The Windows <c>system.dump_analyze</c> (ADR-0048): authorizes one kernel dump path, optionally runs the DumpChk integrity
/// preflight, runs KD once with the fixed <see cref="KdCommandScript"/>, and parses the marker-delimited output. Read-only:
/// the only files written are bOps-owned (the symbol cache and a per-run empty working directory that is always removed).
/// </summary>
public sealed class WindowsKernelDumpAnalyzeTool : SystemDumpAnalyzeToolBase
{
    internal static readonly TimeSpan KdTimeout = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan DumpChkTimeout = TimeSpan.FromSeconds(60);
    internal const int KdOutputCharacters = 4 * 1024 * 1024;
    internal const int DumpChkOutputCharacters = 64 * 1024;
    internal const string PublicSymbolServer = "https://msdl.microsoft.com/download/symbols";

    internal const string ReasonWindowsHostRequired = "windows-host-required";
    internal const string WarningAccessDenied = "access-denied-elevation-may-be-required";
    internal const string WarningDumpChkUnavailable = "dumpchk-unavailable";
    internal const string WarningDumpChkTimeout = "dumpchk-timeout";
    internal const string WarningDumpChkNotStarted = "dumpchk-not-started";
    internal const string WarningDumpChkProblem = "dumpchk-reported-problem";
    internal const string WarningKdTimeout = "kd-timeout";
    internal const string WarningKdNotStarted = "kd-not-started";
    internal const string WarningKdExitCode = "kd-exit-nonzero";
    internal const string WarningKdOutputTruncated = "kd-output-truncated";
    internal const string WarningSmallDump = "small-dump-limited-structures";

    private readonly Func<string, DumpPathDecision> decidePath;
    private readonly Func<DebuggerInstallation?> locate;
    private readonly Func<ProcessStartInfo, TimeSpan, int, CancellationToken, Task<DebuggerProcessResult>> run;
    private readonly Func<string> nonce;
    private readonly Func<string> dataRootFactory;
    private string? dataRoot;

    /// <summary>
    /// Creates the tool for this machine. Constructing it, and reading its manifest, touches no environment state: the Windows
    /// directory, the file system, the debugger installation and the data root are all resolved only when the tool executes.
    /// </summary>
    public WindowsKernelDumpAnalyzeTool()
        : this(DecideOnThisMachine, WindowsDebuggerLocator.Locate, DebuggerProcessRunner.RunAsync, KdCommandScript.NewNonce, DefaultDataRoot)
    {
    }

    /// <summary>Creates the tool with deterministic seams for tests.</summary>
    internal WindowsKernelDumpAnalyzeTool(
        Func<string, DumpPathDecision> decidePath,
        Func<DebuggerInstallation?> locate,
        Func<ProcessStartInfo, TimeSpan, int, CancellationToken, Task<DebuggerProcessResult>> run,
        Func<string> nonce,
        Func<string> dataRoot)
        : base("windows", WindowsDebuggerCapabilities.KernelDumpAnalysis, WindowsDebuggerCapabilities.DumpCheck)
    {
        this.decidePath = decidePath;
        this.locate = locate;
        this.run = run;
        this.nonce = nonce;
        dataRootFactory = dataRoot;
    }

    private string DataRoot => dataRoot ??= dataRootFactory();

    /// <summary>The bOps-owned symbol cache directory.</summary>
    internal string SymbolCache => Path.Combine(DataRoot, "Symbols");

    /// <summary>The fixed symbol path: Microsoft's public symbol server through the bOps-owned cache. Never from arguments.</summary>
    internal string SymbolPath => $"srv*{SymbolCache}*{PublicSymbolServer}";

    /// <inheritdoc />
    protected override async Task<DumpAnalysisReport> AnalyzeAsync(string path, CancellationToken ct)
    {
        var decision = decidePath(path);
        if (Refusal(decision) is { } refused)
        {
            return refused;
        }

        var dump = Describe(decision);
        var installation = locate();
        if (installation is null)
        {
            return new DumpAnalysisReport { Status = DumpAnalysisStatus.Unavailable, Failure = DumpAnalysisFailure.DebuggerUnavailable, Dump = dump };
        }

        if (ProbeReadable(decision.Path!) is { } unreadable)
        {
            return unreadable with { Dump = dump };
        }

        var markerNonce = nonce();
        var workDirectory = Path.Combine(DataRoot, "DebuggerWork", markerNonce);
        var warnings = new List<string>();
        try
        {
            Directory.CreateDirectory(SymbolCache);
            Directory.CreateDirectory(workDirectory);

            bool? dumpCheckPassed = null;
            if (installation.DumpChkPath is null)
            {
                warnings.Add(WarningDumpChkUnavailable);
            }
            else
            {
                if (Refusal(decidePath(path)) is { } changed) return changed;
                var check = await run(
                    DebuggerProcessRunner.CreateStartInfo(installation.DumpChkPath, [decision.Path!], workDirectory),
                    DumpChkTimeout, DumpChkOutputCharacters, ct).ConfigureAwait(false);
                if (!check.Started) warnings.Add(WarningDumpChkNotStarted);
                else if (check.TimedOut) warnings.Add(WarningDumpChkTimeout);
                else dumpCheckPassed = check.ExitCode == 0;
                if (dumpCheckPassed == false) warnings.Add(WarningDumpChkProblem);
            }

            // Rule S11: the path is decided again immediately before the debugger opens it.
            if (Refusal(decidePath(path)) is { } moved) return moved;
            var debugger = new DumpDebuggerDescription("kd", installation.Version, installation.DumpChkPath is not null, dumpCheckPassed);
            var result = await run(
                DebuggerProcessRunner.CreateStartInfo(installation.KdPath, KdCommandScript.Arguments(decision.Path!, SymbolPath, markerNonce), workDirectory),
                KdTimeout, KdOutputCharacters, ct).ConfigureAwait(false);
            return Classify(dump, debugger, result, markerNonce, warnings);
        }
        finally
        {
            TryDelete(workDirectory);
        }
    }

    /// <summary>Turns one KD run into a report (ADR-0048 §8).</summary>
    internal static DumpAnalysisReport Classify(
        DumpFileDescription dump, DumpDebuggerDescription debugger, DebuggerProcessResult result, string markerNonce, List<string> warnings)
    {
        if (!result.Started)
        {
            warnings.Add(WarningKdNotStarted);
            return new DumpAnalysisReport
            {
                Status = DumpAnalysisStatus.Unavailable, Failure = DumpAnalysisFailure.DebuggerFailure, Dump = dump, Debugger = debugger, Warnings = warnings,
            };
        }

        var parsed = KdAnalysisParser.Parse(result.StandardOutput, markerNonce);
        if (result.TimedOut) warnings.Add(WarningKdTimeout);
        if (!result.TimedOut && result.ExitCode != 0) warnings.Add(WarningKdExitCode);
        if (result.StandardOutputTruncated) warnings.Add(WarningKdOutputTruncated);
        warnings.AddRange(parsed.Warnings);
        if (dump.Kind == WindowsDumpPathPolicy.KindSmall && (!parsed.Pnp.Available || !parsed.Bsd.Available))
        {
            warnings.Add(WarningSmallDump);
        }

        if (!parsed.HasEvidence)
        {
            var (status, failure) = debugger.DumpCheckPassed == false
                ? (DumpAnalysisStatus.Invalid, DumpAnalysisFailure.InvalidDump)
                : (DumpAnalysisStatus.Unavailable, result.TimedOut ? DumpAnalysisFailure.Timeout : DumpAnalysisFailure.DebuggerFailure);
            return new DumpAnalysisReport
            {
                Status = status, Failure = failure, Dump = dump, Debugger = debugger, SymbolsStatus = parsed.SymbolsStatus,
                Warnings = warnings, RawAnalysisExcerpt = parsed.RawAnalysisExcerpt,
            };
        }

        // Missing black-box data and a missing integrity preflight are reported as warnings; they do not make the debugger's
        // own analysis partial. Everything that limits the analysis itself does.
        var complete = parsed.AllSectionsComplete
            && parsed.SymbolsStatus == "loaded"
            && parsed.Bugcheck?.Code is not null
            && !parsed.Warnings.Contains(KdAnalysisParser.WarningModuleUnresolved)
            && !result.TimedOut
            && !result.StandardOutputTruncated
            && debugger.DumpCheckPassed != false;
        return new DumpAnalysisReport
        {
            Status = complete ? DumpAnalysisStatus.Complete : DumpAnalysisStatus.Partial,
            Failure = result.TimedOut ? DumpAnalysisFailure.Timeout : DumpAnalysisFailure.None,
            Dump = dump,
            Debugger = debugger,
            SymbolsStatus = parsed.SymbolsStatus,
            Bugcheck = parsed.Bugcheck,
            Attribution = parsed.Attribution,
            Stack = parsed.Stack,
            Modules = parsed.Modules,
            PnpBlackbox = parsed.Pnp,
            BsdBlackbox = parsed.Bsd,
            Warnings = warnings,
            RawAnalysisExcerpt = parsed.RawAnalysisExcerpt,
        };
    }

    private static DumpAnalysisReport? Refusal(DumpPathDecision decision) => decision.Verdict switch
    {
        DumpPathVerdict.Authorized => null,
        DumpPathVerdict.NotFound => new DumpAnalysisReport
        {
            Status = DumpAnalysisStatus.Unavailable, Failure = DumpAnalysisFailure.NotFound, Dump = Describe(decision), Warnings = [decision.Reason!],
        },
        DumpPathVerdict.AccessDenied => new DumpAnalysisReport
        {
            Status = DumpAnalysisStatus.Unavailable, Failure = DumpAnalysisFailure.AccessDenied, Dump = Describe(decision),
            Warnings = [WarningAccessDenied, decision.Reason!],
        },
        _ => new DumpAnalysisReport { Status = DumpAnalysisStatus.Invalid, Failure = DumpAnalysisFailure.InvalidPath, Warnings = [decision.Reason ?? "rejected"] },
    };

    /// <summary>Opens the dump for shared reading and closes it at once, so access denial is never reported as absence.</summary>
    private static DumpAnalysisReport? ProbeReadable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            return null;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new DumpAnalysisReport { Status = DumpAnalysisStatus.Unavailable, Failure = DumpAnalysisFailure.NotFound, Warnings = ["file-not-found"] };
        }
        catch (UnauthorizedAccessException)
        {
            return new DumpAnalysisReport { Status = DumpAnalysisStatus.Unavailable, Failure = DumpAnalysisFailure.AccessDenied, Warnings = [WarningAccessDenied] };
        }
        catch (IOException)
        {
            return new DumpAnalysisReport { Status = DumpAnalysisStatus.Unavailable, Failure = DumpAnalysisFailure.AccessDenied, Warnings = ["file-not-readable"] };
        }
    }

    private static DumpFileDescription Describe(DumpPathDecision decision)
    {
        long? size = null;
        DateTimeOffset? lastWrite = null;
        if (decision.Verdict == DumpPathVerdict.Authorized)
        {
            try
            {
                var info = new FileInfo(decision.Path!);
                size = info.Length;
                lastWrite = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return new DumpFileDescription(decision.Path!, Path.GetFileName(decision.Path!), size, lastWrite, decision.Kind!);
    }

    /// <summary>
    /// The live decision: the real <c>%SystemRoot%</c> policy, created per call so nothing is read at construction. On any other
    /// host the path is refused — a Linux or macOS path is never interpreted as a Windows dump path.
    /// </summary>
    internal static DumpPathDecision DecideOnThisMachine(string requested) =>
        OperatingSystem.IsWindows()
            ? WindowsDumpPathPolicy.ForThisMachine().Decide(requested)
            : new DumpPathDecision(DumpPathVerdict.Rejected, null, null, ReasonWindowsHostRequired);

    private static string DefaultDataRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(string.IsNullOrEmpty(local) ? Path.GetTempPath() : local, "bOps");
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
