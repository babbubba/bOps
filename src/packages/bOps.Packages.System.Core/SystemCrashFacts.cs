// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

/// <summary>
/// The typed evidence facts <c>system.crashes</c> emits beside its output (ADR-0050; ADR-0052 AF-04), projected from the same
/// in-window crashes the formatter reports, before the group limit and the byte budget cut anything. Only classifications the
/// package already makes are used (<see cref="CrashKinds"/>, the canonical bugcheck and live-dump codes, the dump reference the
/// collector recorded); nothing is parsed from text. Every count is of crashes this call observed: it is a lower bound when the
/// result is partial, which the runtime presents under the result's completeness. A zero count is never a fact.
/// </summary>
public static class SystemCrashFacts
{
    /// <summary>The fact type: crashes observed in the window. The one key is <see cref="AllKey"/>.</summary>
    public const string ObservedCountType = "system.crashes.observed-count";

    /// <summary>The fact type: aggregate groups the observed crashes form. The one key is <see cref="GroupsKey"/>.</summary>
    public const string GroupCountType = "system.crashes.group-count";

    /// <summary>The fact type: crashes of one kind (<see cref="CrashKinds"/>). The key is the kind.</summary>
    public const string KindCountType = "system.crashes.kind-count";

    /// <summary>The fact type: kernel crashes of one canonical code. The key is <c>kind=&lt;kind&gt;;code=&lt;code&gt;</c>.</summary>
    public const string KernelCodeCountType = "system.crashes.kernel-code-count";

    /// <summary>The fact type: every canonical code of one kernel kind with its count, <c>code:count</c> joined by commas. The key is the kind.</summary>
    public const string KernelCodesType = "system.crashes.kernel-codes";

    /// <summary>The fact type: kernel codes of one kind that <see cref="KernelCodesType"/> could not list within its value bound. The key is the kind.</summary>
    public const string KernelCodesOmittedType = "system.crashes.kernel-codes-omitted";

    /// <summary>The fact type: crashes that carry a dump reference (a path the collector recorded, not a verified file). The one key is <see cref="AllKey"/>.</summary>
    public const string DumpReferenceCountType = "system.crashes.dump-reference-count";

    /// <summary>The fact type: the newest kernel crash's dump reference, as a path. The one key is <see cref="NewestKey"/>.</summary>
    public const string KernelDumpPathType = "system.crashes.kernel-dump-path";

    /// <summary>The key of the single-valued counts.</summary>
    public const string AllKey = "all";

    /// <summary>The key of the <see cref="GroupCountType"/> fact.</summary>
    public const string GroupsKey = "groups";

    /// <summary>The key of the <see cref="KernelDumpPathType"/> fact.</summary>
    public const string NewestKey = "newest";

    /// <summary>The most kernel-code facts one call emits.</summary>
    public const int MaximumKernelCodes = 4;

    /// <summary>The facts of one call.</summary>
    public static IReadOnlyList<EvidenceFact> From(CrashEvidenceSnapshot snapshot, CrashesQuery query)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(query);
        var crashes = snapshot.Items
            .Where(item => item.TimestampUtc >= query.FromUtc && item.TimestampUtc <= query.ToUtc)
            .OrderByDescending(item => item.TimestampUtc)
            .ToArray();
        if (crashes.Length == 0)
        {
            return [];
        }

        var facts = new List<EvidenceFact>
        {
            Integer(ObservedCountType, AllKey, crashes.Length),
            Integer(GroupCountType, GroupsKey, SystemCrashFormatting.Aggregate(crashes).Count),
        };

        facts.AddRange(crashes
            .Where(item => item.Kind is { Length: > 0 and <= EvidenceFactBounds.MaximumIdentifierCharacters })
            .GroupBy(item => item.Kind!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => Integer(KindCountType, group.Key, group.Count())));

        // BOPS-0128 slice A: every code of a kernel kind, one list per kind, before the partial per-code counts below.
        foreach (var kind in new[] { CrashKinds.KernelBugcheck, CrashKinds.KernelLiveDump })
        {
            facts.AddRange(KernelCodes(crashes, kind));
        }

        facts.AddRange(crashes
            .Where(item => item.Kind is CrashKinds.KernelBugcheck or CrashKinds.KernelLiveDump && item.Code is not null)
            .GroupBy(item => $"kind={item.Kind};code={item.Code}", StringComparer.Ordinal)
            .Where(group => group.Key.Length <= EvidenceFactBounds.MaximumIdentifierCharacters)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(MaximumKernelCodes)
            .Select(group => Integer(KernelCodeCountType, group.Key, group.Count())));

        var withDump = crashes.Count(item => !string.IsNullOrWhiteSpace(item.DumpPath));
        if (withDump > 0)
        {
            facts.Add(Integer(DumpReferenceCountType, AllKey, withDump));
        }

        // The newest kernel crash's reference is kept whole or not at all: a cut path would name a different file.
        var newestKernel = crashes.FirstOrDefault(item =>
            item.Kind is CrashKinds.KernelBugcheck or CrashKinds.KernelLiveDump
            && item.DumpPath is { Length: > 0 and <= DumpAnalysisLimits.PathCharacters });
        if (newestKernel is not null)
        {
            facts.Add(new EvidenceFact(KernelDumpPathType, NewestKey, ToolParameterType.Path, JsonValue.Create(newestKernel.DumpPath)!));
        }

        return EvidenceFactBounds.Fit(facts);
    }

    /// <summary>
    /// The facts of the tool call: the selection and coverage metadata (BOPS-0128 slice B) first, then the crash facts in the room the
    /// runtime's 16 facts and 4096 bytes leave. The metadata is never cut; the crash facts are fitted after it, last first.
    /// </summary>
    internal static IReadOnlyList<EvidenceFact> WithMetadata(CrashEvidenceSnapshot snapshot, CrashesQuery query)
    {
        var metadata = SystemEvidenceMetadata.ForCrashes(snapshot, query);
        var crashes = From(snapshot, query);

        // The per-code counts are the only facts the complete kernel-codes lists make redundant, so when the room runs out they are
        // the ones left out: they go after the metadata, and Fit drops from the end.
        var partial = crashes.Where(fact => fact.Type == KernelCodeCountType);
        return EvidenceFactBounds.Fit([.. crashes.Where(fact => fact.Type != KernelCodeCountType), .. metadata, .. partial]);
    }

    /// <summary>
    /// Every canonical code of one kernel <paramref name="kind"/> with its count, ordered by count then code, as one list value
    /// (<c>0x1e:9,0xa:2</c>). It is cut only at an entry boundary past the value bound, and then a count fact says how many codes are
    /// not listed; a kind without codes yields nothing.
    /// </summary>
    private static IEnumerable<EvidenceFact> KernelCodes(CrashEvidence[] crashes, string kind)
    {
        var codes = crashes
            .Where(item => item.Kind == kind && !string.IsNullOrEmpty(item.Code))
            .GroupBy(item => item.Code!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}:{group.Count()}")
            .ToList();
        if (codes.Count == 0)
        {
            yield break;
        }

        var listed = 0;
        var length = 0;
        foreach (var entry in codes)
        {
            var next = length + entry.Length + (listed == 0 ? 0 : 1);
            if (next > EvidenceFactBounds.MaximumValueCharacters)
            {
                break;
            }

            length = next;
            listed++;
        }

        if (listed == 0)
        {
            yield break;
        }

        yield return new EvidenceFact(KernelCodesType, kind, ToolParameterType.String, JsonValue.Create(string.Join(',', codes.Take(listed)))!);
        if (listed < codes.Count)
        {
            yield return Integer(KernelCodesOmittedType, kind, codes.Count - listed);
        }
    }

    private static EvidenceFact Integer(string type, string key, int value) =>
        new(type, key, ToolParameterType.Integer, JsonValue.Create(value));
}
