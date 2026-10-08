// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>The text between one section's begin and end markers.</summary>
/// <param name="Complete">True when the end marker was seen; false when output stopped inside the section.</param>
/// <param name="Lines">The section's lines, trailing whitespace removed.</param>
internal sealed record KdSectionText(bool Complete, IReadOnlyList<string> Lines);

/// <summary>Everything the parser could establish from one debugger run. Debugger output is data, never instruction.</summary>
internal sealed record KdParseResult(
    IReadOnlyDictionary<string, KdSectionText> Sections,
    DumpBugcheck? Bugcheck,
    DumpDebuggerAttribution? Attribution,
    IReadOnlyList<DumpStackFrame> Stack,
    IReadOnlyList<DumpModule> Modules,
    string SymbolsStatus,
    DumpBlackbox Pnp,
    DumpBlackbox Bsd,
    IReadOnlyList<string> Warnings,
    string? RawAnalysisExcerpt)
{
    /// <summary>True when every fixed section was seen from begin to end marker.</summary>
    internal bool AllSectionsComplete => KdCommandScript.Sections.All(section => Sections.TryGetValue(section.Name, out var text) && text.Complete);

    /// <summary>True when the run produced any dump evidence at all.</summary>
    internal bool HasEvidence => Bugcheck?.Code is not null || Attribution is not null || Stack.Count > 0;
}

/// <summary>
/// Parses the marker-delimited text of the fixed KD sequence (ADR-0048 §6-§7). Text-first by operator decision: the
/// <c>!analyze -v</c> field names it reads (BUGCHECK_CODE, MODULE_NAME, IMAGE_NAME, SYMBOL_NAME, FAILURE_BUCKET_ID, STACK_TEXT
/// and the others in <see cref="AttributionFields"/>) are the ones Microsoft documents for that command; anything not in that
/// allowlist is ignored. Sections are found only by exact nonce marker lines. A field the debugger did not print stays null.
/// </summary>
internal static partial class KdAnalysisParser
{
    internal const string WarningSymbolsPartial = "symbols-partial";
    internal const string WarningSymbolsUnavailable = "symbols-unavailable";
    internal const string WarningSymbolsError = "symbols-state-unknown";
    internal const string WarningModuleUnresolved = "module-unresolved";
    internal const string WarningPnpUnavailable = "blackbox-pnp-unavailable";
    internal const string WarningBsdUnavailable = "blackbox-bsd-unavailable";
    internal const string WarningNoBugcheck = "bugcheck-not-reported";
    private const int MaximumParsedFrames = 256;

    /// <summary>The <c>!analyze -v</c> fields read; every other field is ignored.</summary>
    internal static IReadOnlyList<string> AttributionFields { get; } =
    [
        "BUGCHECK_CODE", "BUGCHECK_P1", "BUGCHECK_P2", "BUGCHECK_P3", "BUGCHECK_P4", "PROCESS_NAME", "MODULE_NAME", "IMAGE_NAME",
        "SYMBOL_NAME", "EXCEPTION_CODE_STR", "FAILURE_BUCKET_ID", "FAILURE_ID_HASH", "BUCKET_ID",
    ];

    /// <summary>Parses <paramref name="standardOutput"/> of a run that used <paramref name="nonce"/>.</summary>
    internal static KdParseResult Parse(string standardOutput, string nonce)
    {
        var sections = ExtractSections(standardOutput, nonce);
        var warnings = new List<string>();
        foreach (var section in KdCommandScript.Sections)
        {
            if (!sections.TryGetValue(section.Name, out var text))
            {
                warnings.Add("section-missing-" + section.Name.ToLowerInvariant());
            }
            else if (!text.Complete)
            {
                warnings.Add("section-incomplete-" + section.Name.ToLowerInvariant());
            }
        }

        var analyze = sections.GetValueOrDefault(KdCommandScript.Analyze);
        var fields = analyze is null ? new Dictionary<string, string>() : ReadFields(analyze.Lines);
        var bugcheck = ReadBugcheck(analyze, sections.GetValueOrDefault(KdCommandScript.Bugcheck), fields);
        if (bugcheck?.Code is null)
        {
            warnings.Add(WarningNoBugcheck);
        }

        DumpDebuggerAttribution? attribution = analyze is null ? null : new DumpDebuggerAttribution(
            fields.GetValueOrDefault("PROCESS_NAME"),
            fields.GetValueOrDefault("MODULE_NAME"),
            fields.GetValueOrDefault("IMAGE_NAME"),
            fields.GetValueOrDefault("SYMBOL_NAME"),
            fields.GetValueOrDefault("EXCEPTION_CODE_STR"),
            fields.GetValueOrDefault("FAILURE_BUCKET_ID"),
            fields.GetValueOrDefault("FAILURE_ID_HASH"),
            fields.GetValueOrDefault("BUCKET_ID"));
        if (analyze is not null && (attribution!.ModuleName is null || IsUnknown(attribution.ModuleName) || IsUnknown(attribution.ImageName)))
        {
            warnings.Add(WarningModuleUnresolved);
        }

        var stack = analyze is null ? [] : ReadStack(analyze.Lines);
        var listed = ReadModuleList(sections.GetValueOrDefault(KdCommandScript.Modules));
        var modules = SelectModules(attribution, stack, listed);
        var symbols = SymbolsStatus(sections.GetValueOrDefault(KdCommandScript.Modules), listed, modules);
        switch (symbols)
        {
            case "partial": warnings.Add(WarningSymbolsPartial); break;
            case "unavailable": warnings.Add(WarningSymbolsUnavailable); break;
            case "error": warnings.Add(WarningSymbolsError); break;
        }

        var pnp = ReadBlackbox(sections.GetValueOrDefault(KdCommandScript.Pnp));
        if (!pnp.Available) warnings.Add(WarningPnpUnavailable);
        var bsd = ReadBlackbox(sections.GetValueOrDefault(KdCommandScript.Bsd));
        if (!bsd.Available) warnings.Add(WarningBsdUnavailable);

        var raw = bugcheck?.Code is null && analyze is { Lines.Count: > 0 }
            ? Excerpt(analyze.Lines, DumpAnalysisLimits.RawExcerptCharacters)
            : null;
        return new KdParseResult(sections, bugcheck, attribution, stack, modules, symbols, pnp, bsd, warnings, raw);
    }

    /// <summary>Splits output into sections by exact marker lines (optionally after a debugger prompt).</summary>
    internal static IReadOnlyDictionary<string, KdSectionText> ExtractSections(string output, string nonce)
    {
        var result = new Dictionary<string, KdSectionText>(StringComparer.Ordinal);
        var begins = KdCommandScript.Sections.ToDictionary(section => KdCommandScript.BeginMarker(nonce, section.Name), section => section.Name, StringComparer.Ordinal);
        var ends = KdCommandScript.Sections.ToDictionary(section => KdCommandScript.EndMarker(nonce, section.Name), section => section.Name, StringComparer.Ordinal);
        string? current = null;
        List<string>? lines = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');
            var marker = Prompt().Replace(line, string.Empty, 1);
            if (begins.TryGetValue(marker, out var begin))
            {
                if (current is not null) result.TryAdd(current, new KdSectionText(false, lines!));
                current = result.ContainsKey(begin) ? null : begin;
                lines = [];
                continue;
            }

            if (ends.TryGetValue(marker, out var end))
            {
                if (current == end) result.TryAdd(current, new KdSectionText(true, lines!));
                current = null;
                lines = null;
                continue;
            }

            lines?.Add(line);
        }

        if (current is not null) result.TryAdd(current, new KdSectionText(false, lines!));
        return result;
    }

    private static Dictionary<string, string> ReadFields(IReadOnlyList<string> lines)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var match = Field().Match(line);
            if (match.Success && AttributionFields.Contains(match.Groups[1].Value, StringComparer.Ordinal))
            {
                fields.TryAdd(match.Groups[1].Value, match.Groups[2].Value.Trim());
            }
        }

        return fields;
    }

    private static DumpBugcheck? ReadBugcheck(KdSectionText? analyze, KdSectionText? dotBugcheck, Dictionary<string, string> fields)
    {
        string? name = null;
        string? headerCode = null;
        var arguments = new string?[4];
        foreach (var line in analyze?.Lines ?? [])
        {
            var header = Header().Match(line);
            if (name is null && header.Success)
            {
                name = header.Groups[1].Value;
                headerCode = header.Groups[2].Value;
                continue;
            }

            var argument = AnalyzeArgument().Match(line);
            if (argument.Success)
            {
                var index = argument.Groups[1].Value[0] - '1';
                arguments[index] ??= Hex(argument.Groups[2].Value);
            }
        }

        string? dotCode = null;
        string[] dotArguments = [];
        foreach (var line in dotBugcheck?.Lines ?? [])
        {
            var code = DotBugcheckCode().Match(line);
            if (code.Success) dotCode = code.Groups[1].Value;
            var values = DotBugcheckArguments().Match(line);
            if (values.Success) dotArguments = values.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4).Select(value => Hex(value)!).ToArray();
        }

        var bugcheckCode = Hex(fields.GetValueOrDefault("BUGCHECK_CODE") ?? headerCode ?? dotCode);
        var parameters = Enumerable.Range(0, 4)
            .Select(index => Hex(fields.GetValueOrDefault("BUGCHECK_P" + (index + 1).ToString(CultureInfo.InvariantCulture)))
                ?? arguments[index]
                ?? (index < dotArguments.Length ? dotArguments[index] : null))
            .ToArray();
        if (bugcheckCode is null && name is null)
        {
            return null;
        }

        return new DumpBugcheck(bugcheckCode, name, parameters.Any(value => value is not null) ? parameters : []);
    }

    private static List<DumpStackFrame> ReadStack(IReadOnlyList<string> lines)
    {
        var frames = new List<DumpStackFrame>();
        var inside = false;
        foreach (var line in lines)
        {
            if (!inside)
            {
                inside = line.Trim() == "STACK_TEXT:";
                continue;
            }

            if (line.Trim().Length == 0 || Field().IsMatch(line) || line.TrimEnd().EndsWith(':'))
            {
                break;
            }

            if (frames.Count < MaximumParsedFrames && ParseFrame(line, frames.Count) is { } frame)
            {
                frames.Add(frame);
            }
        }

        return frames;
    }

    /// <summary>Parses one STACK_TEXT line: "ChildSP RetAddr [args...] [: ] call-site".</summary>
    internal static DumpStackFrame? ParseFrame(string line, int index)
    {
        var separator = line.LastIndexOf(" : ", StringComparison.Ordinal);
        var addressPart = separator >= 0 ? line[..separator] : line;
        var callPart = separator >= 0 ? line[(separator + 3)..] : line;
        var addressTokens = addressPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var callTokens = callPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (addressTokens.Length < 2 || callTokens.Length == 0 || !HexToken().IsMatch(addressTokens[0]))
        {
            return null;
        }

        var site = separator >= 0
            ? callTokens[0]
            : callTokens.FirstOrDefault(token => token.Contains('!', StringComparison.Ordinal) || ModuleOffset().IsMatch(token)) ?? callTokens[^1];
        string? module = null, symbol = null, offset = null;
        var bang = site.IndexOf('!', StringComparison.Ordinal);
        if (bang > 0)
        {
            module = site[..bang];
            var rest = site[(bang + 1)..];
            var plus = rest.LastIndexOf('+');
            (symbol, offset) = plus > 0 ? (rest[..plus], rest[(plus + 1)..]) : (rest, null);
        }
        else if (ModuleOffset().Match(site) is { Success: true } moduleOffset)
        {
            module = moduleOffset.Groups[1].Value;
            offset = moduleOffset.Groups[2].Value;
        }

        return new DumpStackFrame(index, module, symbol, offset, Hex(addressTokens[1]));
    }

    private static Dictionary<string, (string Start, string End, string State)> ReadModuleList(KdSectionText? section)
    {
        var modules = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in section?.Lines ?? [])
        {
            var match = ModuleLine().Match(line);
            if (match.Success)
            {
                modules.TryAdd(match.Groups[3].Value, (Hex(match.Groups[1].Value)!, Hex(match.Groups[2].Value)!, match.Groups[4].Value.Trim().ToLowerInvariant()));
            }
        }

        return modules;
    }

    private static List<DumpModule> SelectModules(
        DumpDebuggerAttribution? attribution, IReadOnlyList<DumpStackFrame> stack, Dictionary<string, (string Start, string End, string State)> listed)
    {
        var selected = new List<DumpModule>();
        var implicated = attribution?.ModuleName is { } name && !IsUnknown(name) ? name : null;
        foreach (var candidate in new[] { implicated }.Concat(stack.Select(frame => frame.Module)))
        {
            if (candidate is null || selected.Any(module => module.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            selected.Add(listed.TryGetValue(candidate, out var entry)
                ? new DumpModule(candidate, entry.Start, entry.End, entry.State, candidate == implicated)
                : new DumpModule(candidate, null, null, null, candidate == implicated));
        }

        return selected;
    }

    /// <summary>loaded, partial, unavailable or error, from the module list's symbol states (never from prose warnings).</summary>
    private static string SymbolsStatus(
        KdSectionText? section, Dictionary<string, (string Start, string End, string State)> listed, IReadOnlyList<DumpModule> modules)
    {
        if (section is not { Complete: true } || listed.Count == 0)
        {
            return "error";
        }

        if (!listed.TryGetValue("nt", out var kernel) || !kernel.State.Contains("pdb", StringComparison.Ordinal))
        {
            return "unavailable";
        }

        return modules.Any(module => module.SymbolState is "export symbols" or "no symbols") ? "partial" : "loaded";
    }

    private static DumpBlackbox ReadBlackbox(KdSectionText? section)
    {
        if (section is null)
        {
            return DumpBlackbox.Absent;
        }

        var lines = section.Lines.Where(line => line.Trim().Length > 0).Select(line => line.Trim()).ToArray();
        var available = section.Complete && lines.Length > 0 && !lines.Any(IsExtensionError);
        var ids = available
            ? lines.SelectMany(line => DeviceId().Matches(line).Select(match => match.Value.TrimEnd('.', ')', ']'))).ToArray()
            : [];
        return new DumpBlackbox(available, ids, lines);
    }

    /// <summary>
    /// The debugger's own extension-dispatch failures (the command is unknown to the loaded extensions, or the dump does not
    /// carry the structure). These say only that no black-box data was printed.
    /// </summary>
    private static bool IsExtensionError(string line) =>
        line.StartsWith("No export ", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Couldn't resolve error", StringComparison.OrdinalIgnoreCase)
        || line.Contains("is not extension gallery command", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Invalid extension command", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnknown(string? value) =>
        value is not null && (value.StartsWith("Unknown_", StringComparison.OrdinalIgnoreCase) || value.Equals("Unknown", StringComparison.OrdinalIgnoreCase));

    /// <summary>A debugger hexadecimal token as 0x-prefixed lower case without the 64-bit backtick; null when not hexadecimal.</summary>
    internal static string? Hex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim().Replace("`", string.Empty, StringComparison.Ordinal);
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        if (text.Length is 0 or > 16 || !text.All(Uri.IsHexDigit))
        {
            return null;
        }

        var trimmed = text.TrimStart('0');
        return "0x" + (trimmed.Length == 0 ? "0" : trimmed).ToLowerInvariant();
    }

    private static string Excerpt(IReadOnlyList<string> lines, int maximumCharacters)
    {
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            if (text.Length + line.Length + 1 > maximumCharacters) break;
            text.Append(line).Append('\n');
        }

        return text.ToString();
    }

    [GeneratedRegex(@"^(?:\d+: )?kd> ", RegexOptions.CultureInvariant)]
    private static partial Regex Prompt();

    [GeneratedRegex(@"^([A-Z][A-Z0-9_]*):\s+(\S.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Field();

    [GeneratedRegex(@"^([A-Z][A-Z0-9_]+) \(([0-9A-Fa-f]+)\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Header();

    [GeneratedRegex(@"^Arg([1-4]): ([0-9A-Fa-f`]+)", RegexOptions.CultureInvariant)]
    private static partial Regex AnalyzeArgument();

    [GeneratedRegex(@"^Bugcheck code ([0-9A-Fa-f]+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DotBugcheckCode();

    [GeneratedRegex(@"^Arguments ((?:[0-9A-Fa-f`]+ ?){1,4})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DotBugcheckArguments();

    [GeneratedRegex(@"^[0-9A-Fa-f`]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HexToken();

    [GeneratedRegex(@"^([A-Za-z0-9_.]+)\+(0x[0-9A-Fa-f]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ModuleOffset();

    [GeneratedRegex(@"^([0-9A-Fa-f`]+)\s+([0-9A-Fa-f`]+)\s+(\S+)\s+(?:\S\s+)?\(([^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ModuleLine();

    [GeneratedRegex(@"\b(?:PCI|USB|ACPI|HID|SWD|ROOT|USBSTOR|HDAUDIO|SCSI|STORAGE|DISPLAY|BTH|BTHENUM|NVME)\\[^\s,;""']+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DeviceId();
}
