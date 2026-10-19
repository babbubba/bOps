// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Windows;
using bOps.Policy;

namespace bOps.ReplayFixtureCapture;

/// <summary>Who runs the capture and with what privilege, recorded in every snapshot. Reading one's own token is not a collector query.</summary>
internal sealed record EnvironmentInfo(
    string UserName,
    string MachineName,
    bool IsElevated,
    bool IsAdministratorRole,
    string OsDescription,
    int ProcessId,
    string DotNetVersion,
    IReadOnlyDictionary<string, BinaryInfo> Binaries,
    string UserSid = "",
    int? IntegrityRid = null,
    string ElevationType = "unknown")
{
    /// <summary>The Windows mandatory integrity level name of <see cref="IntegrityRid"/> ("unknown" when the token could not be read).</summary>
    public string IntegrityLevel => IntegrityLevels.Name(IntegrityRid);
}

/// <summary>Windows mandatory integrity levels (the last sub-authority of the token's integrity SID).</summary>
internal static class IntegrityLevels
{
    public const int Untrusted = 0x0000;
    public const int Low = 0x1000;
    public const int Medium = 0x2000;
    public const int MediumPlus = 0x2100;
    public const int High = 0x3000;
    public const int System = 0x4000;

    public static string Name(int? rid) => rid switch
    {
        null => "unknown",
        Untrusted => "Untrusted",
        Low => "Low",
        Medium => "Medium",
        MediumPlus => "MediumPlus",
        High => "High",
        System => "System",
        >= Medium and < High => "Medium-class",
        >= High => "High-or-above",
        _ => "Low-class",
    };

    /// <summary>The capture runs at Medium, the level of a normal, non-elevated interactive process (Medium and Medium-plus).</summary>
    public static bool IsMedium(int? rid) => rid is >= Medium and < High;
}

/// <summary>
/// The identity gate of the capture. It needs a positive proof of a standard (Medium integrity) token: an elevated process, a High or System
/// token, a Low token, or a token that cannot be read are all refused. Nothing is done to make the process pass: no privilege is requested, no
/// ACL or authorisation is changed, and data this identity cannot read stays unavailable or partial as the tools' contracts say.
/// </summary>
internal static class IdentityGate
{
    public static IReadOnlyList<string> Problems(EnvironmentInfo env)
    {
        var problems = new List<string>();
        if (env.IsElevated)
        {
            problems.Add("the process is elevated; the capture must run non-elevated and never asks for elevation");
        }

        if (env.IntegrityRid is null)
        {
            problems.Add("the integrity level of the process token could not be read, so a non-elevated token is not proven");
        }
        else if (!IntegrityLevels.IsMedium(env.IntegrityRid))
        {
            problems.Add($"the process token has integrity level {env.IntegrityLevel} (0x{env.IntegrityRid:X4}); Medium is required");
        }

        if (env.IsAdministratorRole)
        {
            problems.Add("the token carries an enabled Administrators role (an elevated token); a standard token is required");
        }

        if (string.IsNullOrWhiteSpace(env.UserSid) || !env.UserSid.StartsWith("S-1-", StringComparison.Ordinal))
        {
            problems.Add("the SID of the current user could not be read");
        }

        return problems;
    }

    public static JsonObject ToJson(EnvironmentInfo env) => new()
    {
        ["user"] = env.UserName,
        ["sid"] = env.UserSid,
        ["integrityLevel"] = env.IntegrityLevel,
        ["integrityRid"] = env.IntegrityRid is { } rid ? $"0x{rid:X4}" : null,
        ["elevationType"] = env.ElevationType,
        ["elevated"] = env.IsElevated,
        ["administratorRole"] = env.IsAdministratorRole,
        ["gate"] = "Medium integrity, non-elevated, no Administrators role",
        ["gateProblems"] = new JsonArray(Problems(env).Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
    };
}

/// <summary>Reads the integrity level and elevation type of the current process token. Reading one's own token is not a collector query.</summary>
internal static class TokenReader
{
    private const int TokenElevationType = 18;
    private const int TokenIntegrityLevel = 25;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, int index);

    public static (int? IntegrityRid, string ElevationType) Read(IntPtr token) => (ReadIntegrity(token), ReadElevationType(token));

    private static int? ReadIntegrity(IntPtr token)
    {
        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var needed);
            if (needed <= 0)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _))
                {
                    return null;
                }

                var sid = Marshal.ReadIntPtr(buffer);   // TOKEN_MANDATORY_LABEL.Label.Sid
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return count == 0 ? null : Marshal.ReadInt32(GetSidSubAuthority(sid, count - 1));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException or ArgumentException)
        {
            return null;
        }
    }

    private static string ReadElevationType(IntPtr token)
    {
        try
        {
            var buffer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                return GetTokenInformation(token, TokenElevationType, buffer, sizeof(int), out _)
                    ? Marshal.ReadInt32(buffer) switch { 1 => "Default", 2 => "Full", 3 => "Limited", var other => $"unknown({other})" }
                    : "unknown";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return "unknown";
        }
    }
}

internal sealed record BinaryInfo(string FileName, string Sha256, string? InformationalVersion);

internal interface IEnvironmentProbe
{
    EnvironmentInfo Probe();
}

/// <summary>The manifests the tools declare, without constructing a tool or reading the machine.</summary>
internal interface IManifestSource
{
    ToolManifest ManifestOf(string tool);
}

/// <summary>The collectors. Only the `capture` command ever asks for one; `verify`, `scan` and `minimize` never do.</summary>
internal interface ICollectorSource
{
    ITool Create(string tool);
}

internal sealed class RealEnvironmentProbe : IEnvironmentProbe
{
    public EnvironmentInfo Probe()
    {
        var identity = WindowsIdentity.GetCurrent();
        var admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var binaries = new SortedDictionary<string, BinaryInfo>(StringComparer.Ordinal);
        foreach (var type in new[]
        {
            typeof(Program), typeof(ToolArguments), typeof(PolicyEngine), typeof(SystemToolManifests), typeof(WindowsSystemEventsTool),
        })
        {
            var assembly = type.Assembly;
            var path = assembly.Location;
            binaries[assembly.GetName().Name!] = new BinaryInfo(
                Path.GetFileName(path),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        }

        var (integrity, elevationType) = TokenReader.Read(identity.AccessToken.DangerousGetHandle());
        GC.KeepAlive(identity);
        return new EnvironmentInfo(
            identity.Name, Environment.MachineName, Environment.IsPrivilegedProcess, admin, RuntimeInformation.OSDescription,
            Environment.ProcessId, RuntimeInformation.FrameworkDescription, binaries, identity.User?.Value ?? string.Empty, integrity, elevationType);
    }
}

internal sealed class RealManifestSource : IManifestSource
{
    public ToolManifest ManifestOf(string tool) => tool switch
    {
        "system.events" => SystemToolManifests.Events("windows"),
        "system.stability" => SystemToolManifests.Stability("windows"),
        "system.crashes" => SystemToolManifests.Crashes("windows"),
        _ => throw new UnauthorisedQueryException($"tool '{tool}' is not on the capture allow-list."),
    };
}

/// <summary>Creates the real Windows collectors. A constructor only stores its dependencies; the machine is read when ExecuteAsync runs.</summary>
internal sealed class RealCollectorSource : ICollectorSource
{
    public ITool Create(string tool) => tool switch
    {
        "system.events" => new WindowsSystemEventsTool(),
        "system.stability" => new WindowsStabilityTool(),
        "system.crashes" => new WindowsCrashEvidenceTool(),
        _ => throw new UnauthorisedQueryException($"tool '{tool}' is not on the capture allow-list."),
    };
}

internal static class ReadOnlyPolicy
{
    /// <summary>The same strictly read-only policy as the campaigns: only Read-risk tools run automatically, everything above is forbidden.</summary>
    public const string Yaml = """
        # bOps replay capture: strictly read-only. Only Read-risk tools run; everything above is forbidden (Critical always).
        defaults:
          read: automatic
          low: forbidden
          medium: forbidden
          high: forbidden
          critical: forbidden
        """;

    public static IPolicyEngine Create() => new PolicyEngine(PolicyConfigLoader.Load(Yaml));
}

internal static class ManifestFingerprint
{
    public static string Of(ToolManifest manifest) => CapturePlan.Sha256(JsonSerializer.Serialize(manifest));
}

/// <summary>Defers the creation of the collector source until a query has passed every gate, so a refused capture never even asks for one.</summary>
internal sealed class LazyCollectorSource(Func<ICollectorSource> factory) : ICollectorSource
{
    private ICollectorSource? source;

    public ITool Create(string tool) => (source ??= factory()).Create(tool);
}
