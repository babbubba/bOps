// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Globalization;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// Whether, and where, the operator authorized the harness to write protected diagnosis records. Writing is opt-in: nothing is written
/// unless the operator names an archive root <b>and</b> separately accepts the local write. Anything else is recorded in
/// <c>result.json</c> as "not measurable", never silently skipped.
/// </summary>
internal sealed record DiagnosisArchiveSettings(string Status, string? Root, IArchiveKeyProtector? Protector)
{
    internal const string Authorized = "Authorized";

    internal static DiagnosisArchiveSettings Disabled { get; } = new(DiagnosisArchiveOutcome.NotConfigured, null, null);

    internal bool IsAuthorized => Status == Authorized;
}

/// <summary>Reads the D7 operator switches. Pure over the supplied lookup so the offline tests never touch the process environment.</summary>
internal static class DiagnosisArchiveEnvironment
{
    internal const string Root = "BOPS_KNOW_BENCH_DIAGNOSIS_ARCHIVE";
    internal const string Accept = "BOPS_KNOW_BENCH_DIAGNOSIS_ARCHIVE_ACCEPT";
    internal const string Protection = "BOPS_KNOW_BENCH_ARCHIVE_PROTECTION";
    internal const string Secret = "BOPS_KNOW_BENCH_ARCHIVE_SECRET";
    internal const string Repetition = "BOPS_KNOW_BENCH_REPETITION";

    internal static DiagnosisArchiveSettings Read(Func<string, string?> get)
    {
        ArgumentNullException.ThrowIfNull(get);
        var root = get(Root);
        if (string.IsNullOrWhiteSpace(root)) return DiagnosisArchiveSettings.Disabled;
        if (get(Accept) != "1") return new(DiagnosisArchiveOutcome.NotAuthorized, null, null);
        if (!Path.IsPathFullyQualified(root)) throw new InvalidOperationException("The diagnosis archive root must be an absolute path.");
        var requested = get(Protection);
        var mode = string.IsNullOrWhiteSpace(requested) ? (OperatingSystem.IsWindows() ? "dpapi" : "operator-secret") : requested;
        IArchiveKeyProtector protector;
        switch (mode)
        {
            case "dpapi":
                if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("DPAPI key protection is available on Windows only.");
                protector = NewDpapi();
                break;
            case "operator-secret":
                protector = new OperatorSecretKeyProtector(get(Secret) ?? throw new InvalidOperationException("An operator secret is required."));
                break;
            default:
                throw new InvalidOperationException("Unsupported archive key protection.");
        }

        return new(DiagnosisArchiveSettings.Authorized, root, protector);
    }

    internal static DiagnosisArchiveSettings Read() => Read(Environment.GetEnvironmentVariable);

    /// <summary>The 1-based repetition of the Latin-square plan; defaults to 1 for a single run.</summary>
    internal static int ReadRepetition(Func<string, string?> get)
    {
        var value = get(Repetition);
        if (string.IsNullOrWhiteSpace(value)) return 1;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 99
            ? number : throw new InvalidOperationException("Repetition must be a number from 1 to 99.");
    }

    /// <summary>
    /// For the live entry points: refuse to start a paid run whose diagnosis could not be kept. Creates the archive (restricted) and opens
    /// it once, so a permission or key problem surfaces before the first model call.
    /// </summary>
    internal static DiagnosisArchiveSettings RequireAuthorized(Func<string, string?> get)
    {
        var settings = Read(get);
        if (!settings.IsAuthorized)
            throw new InvalidOperationException(
                $"Diagnosis archive not authorized: set {Root} to a new absolute path and {Accept}=1 (the quality of a run without a stored diagnosis cannot be measured).");
        try { using var archive = DiagnosisArchive.OpenOrCreate(settings.Root!, settings.Protector!); }
        catch (ArchiveException ex) { throw new InvalidOperationException("Diagnosis archive unavailable: " + ex.Code); }
        return settings;
    }

#pragma warning disable CA1416 // Guarded by the OperatingSystem.IsWindows() check at the only call site.
    private static WindowsDpapiKeyProtector NewDpapi() => new();
#pragma warning restore CA1416
}
