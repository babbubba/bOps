// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>Maps only authoritative WUA category evidence into the shared update contract.</summary>
internal static class WindowsUpdateRecordMapper
{
    internal const string SecurityCategoryId = "0fa1201d-4330-4fa8-8ae9-b877473b6441";
    internal const string CriticalCategoryId = "e6cf1350-c01b-414d-a61f-263d14d133b4";
    internal const string Source = "windows-update-agent";

    internal static UpdateRecord Map(WindowsUpdateNativeRecord update) => new(
        $"{update.UpdateId}:{update.RevisionNumber}",
        update.Title,
        CurrentVersion: null,
        AvailableVersion: null,
        Classify(update.CategoryIds),
        update.RebootMayBeRequired,
        Source,
        PublishedUtc: null);

    internal static string Classify(IReadOnlyList<string> categoryIds) =>
        categoryIds.Any(id => string.Equals(id, SecurityCategoryId, StringComparison.OrdinalIgnoreCase)) ? "security"
        : categoryIds.Any(id => string.Equals(id, CriticalCategoryId, StringComparison.OrdinalIgnoreCase)) ? "critical"
        : "other";

    internal static bool MatchesKind(UpdateRecord row, string kind) => kind == "all" || row.Kind == kind;
}

/// <summary>The bounded native facts WUA exposes for one pending update.</summary>
internal sealed record WindowsUpdateNativeRecord(string UpdateId, int RevisionNumber, string Title, IReadOnlyList<string> CategoryIds, bool? RebootMayBeRequired);
