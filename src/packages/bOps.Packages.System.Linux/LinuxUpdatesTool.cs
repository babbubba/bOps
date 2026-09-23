// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>Reads pending update evidence through the distro's fixed, read-only package-manager query.</summary>
public sealed class LinuxUpdatesTool : SystemUpdatesToolBase
{
    private const int MaxOutput = 1_048_576;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public LinuxUpdatesTool() : base("linux") { }

    protected override async Task<MaintenanceSnapshot<UpdateRecord>> CollectAsync(MaintenanceArguments arguments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var distro = LinuxUpdateParser.Detect(File.Exists("/etc/os-release") ? await File.ReadAllTextAsync("/etc/os-release", ct) : string.Empty);
        if (distro is null) return Snapshot([], InventorySourceStatus.Unsupported, "linux.updates.unsupported-distro", "Unsupported Linux distribution; /etc/os-release did not identify a supported family.");
        var (exe, args, source) = distro switch
        {
            "debian" => ("apt-get", new[] { "--just-print", "upgrade" }, "linux.apt"),
            "dnf" => ("dnf", new[] { "check-update", "--cacheonly" }, "linux.dnf"),
            _ => ("zypper", new[] { "--xmlout", "list-updates" }, "linux.zypper"),
        };
        try
        {
            using var p = new Process { StartInfo = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } };
            foreach (var a in args) p.StartInfo.ArgumentList.Add(a);
            p.StartInfo.Environment.Clear(); p.StartInfo.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin"; p.StartInfo.Environment["LC_ALL"] = "C"; p.StartInfo.Environment["LANG"] = "C"; p.StartInfo.Environment["HOME"] = "/";
            if (!p.Start()) return Snapshot([], InventorySourceStatus.Unavailable, source + ".unavailable", "Expected package manager could not be started.");
            p.StandardInput.Close();
            using var timeout = new CancellationTokenSource(Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var stdout = ReadBounded(p.StandardOutput, MaxOutput, linked.Token);
            var stderr = ReadBounded(p.StandardError, 16_384, linked.Token);
            try { await p.WaitForExitAsync(linked.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } await p.WaitForExitAsync(CancellationToken.None); if (ct.IsCancellationRequested) throw; return Snapshot([], InventorySourceStatus.Partial, source + ".timeout", "Package-manager query timed out; process tree was terminated."); }
            var output = await stdout; var error = await stderr;
            if (output is null || error is null) return Snapshot([], InventorySourceStatus.Partial, source + ".oversized", "Package-manager output exceeded the bounded capture size.");
            var parsed = LinuxUpdateParser.Parse(distro, output, p.ExitCode);
            if (!parsed.Valid) return Snapshot([], InventorySourceStatus.Partial, source + ".malformed", "Package-manager output was malformed or incomplete.");
            var warnings = new List<string> { "Local package metadata age is unknown; no reliable catalog timestamp is exposed by this query." };
            if (parsed.KeptBack) warnings.Add("Some packages are held back and are not reported as normally applicable updates.");
            if (!string.IsNullOrWhiteSpace(error)) warnings.Add("Package manager emitted diagnostics on stderr.");
            var rows = parsed.Items.Where(x => arguments.Kind == "all" || x.Kind == arguments.Kind).OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).Take(arguments.Limit).ToArray();
            return new(rows, [new(source, InventorySourceStatus.Available)], warnings, parsed.Items.Count > arguments.Limit, null);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return Snapshot([], InventorySourceStatus.Unavailable, source + ".unavailable", "Expected package manager is unavailable for the detected distribution.");
        }
    }

    private static async Task<string?> ReadBounded(StreamReader reader, int max, CancellationToken ct)
    {
        var b = new StringBuilder(); var buf = new char[4096];
        while (true) { var n = await reader.ReadAsync(buf.AsMemory(), ct); if (n == 0) return b.ToString(); if (b.Length + n > max) return null; b.Append(buf, 0, n); }
    }

    private static MaintenanceSnapshot<UpdateRecord> Snapshot(IReadOnlyList<UpdateRecord> rows, InventorySourceStatus status, string detail, string warning) => new(rows, [new("linux.package-manager", status, detail)], [warning]);
}

internal static class LinuxUpdateParser
{
    internal sealed record Result(bool Valid, List<UpdateRecord> Items, bool KeptBack = false);
    internal static string? Detect(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n')) { var i = line.IndexOf('='); if (i > 0) fields[line[..i].Trim()] = line[(i + 1)..].Trim().Trim('"', '\''); }
        fields.TryGetValue("ID", out var id); fields.TryGetValue("ID_LIKE", out var like);
        var ids = ((id ?? "") + " " + (like ?? "")).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => x.ToLowerInvariant()).ToHashSet();
        if (ids.Overlaps(["debian", "ubuntu"])) return "debian";
        if (ids.Overlaps(["fedora", "rhel", "centos", "rocky", "almalinux"])) return "dnf";
        if ((id ?? "").StartsWith("opensuse", StringComparison.OrdinalIgnoreCase) || ids.Overlaps(["suse", "sles"])) return "zypper";
        return null;
    }

    internal static Result Parse(string distro, string text, int exitCode)
    {
        if (distro == "debian") return Apt(text, exitCode);
        if (distro == "dnf") return Dnf(text, exitCode);
        return Zypper(text, exitCode);
    }

    private static Result Apt(string text, int exit)
    {
        if (exit != 0) return new(false, []);
        var rows = new List<UpdateRecord>(); var kept = false;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
        {
            if (line.StartsWith("The following packages have been kept back:", StringComparison.OrdinalIgnoreCase)) { kept = true; continue; }
            if (line.StartsWith("Inst ", StringComparison.Ordinal))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, "^Inst\\s+(\\S+)(?:\\s+\\[([^]]+)\\])?\\s+\\((\\S+)(?:\\s+([^)]*))?\\)$");
                if (!m.Success) return new(false, []);
                var meta = m.Groups[4].Value;
                rows.Add(new("deb:" + m.Groups[1].Value, m.Groups[1].Value, Empty(m.Groups[2].Value), m.Groups[3].Value, meta.Contains("security", StringComparison.OrdinalIgnoreCase) ? "security" : "other", null, "linux.apt", null));
            }
            else if (line.StartsWith("Conf ", StringComparison.Ordinal) || line.StartsWith("Remv ", StringComparison.Ordinal)) return new(false, []);
        }
        return new(true, rows, kept);
    }
    private static Result Dnf(string text, int exit)
    {
        if (exit == 0) return string.IsNullOrWhiteSpace(text) ? new(true, []) : new(false, []);
        if (exit != 100) return new(false, []);
        var rows = new List<UpdateRecord>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
        {
            if (line.StartsWith("Last metadata expiration check:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Obsoleting Packages", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Available Packages", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Security:", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); if (parts.Length == 0) continue;
            if (parts.Length != 3 || !parts[0].Contains('.')) return new(false, []);
            var nvra = parts[0]; var dot = nvra.LastIndexOf('.'); if (dot <= 0 || dot == nvra.Length - 1) return new(false, []);
            var name = nvra[..dot]; var arch = nvra[(dot + 1)..];
            rows.Add(new("rpm:" + name + "." + arch, name, null, parts[1], "other", null, "linux.dnf", null));
        }
        return new(rows.Count > 0, rows);
    }
    private static Result Zypper(string text, int exit)
    {
        if (exit != 0) return new(false, []);
        try
        {
            using var sr = new StringReader(text); using var xr = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_048_576 }); var doc = XDocument.Load(xr);
            var rows = new List<UpdateRecord>();
            foreach (var e in doc.Descendants().Where(x => x.Name.LocalName == "update"))
            {
                var name = (string?)e.Attribute("name"); var edition = (string?)e.Attribute("edition"); if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(edition)) return new(false, []);
                var arch = (string?)e.Attribute("arch"); rows.Add(new("rpm:" + name + (arch is null ? "" : "." + arch), name, null, edition, "other", null, "linux.zypper", null));
            }
            return new(true, rows);
        }
        catch (XmlException) { return new(false, []); }
    }
    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
