// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// A failure of the diagnosis archive. The message is always a closed code and never carries a path, an inner message or any
/// diagnosis text, so it can be logged or exported.
/// </summary>
#pragma warning disable CA1032 // Closed-code exception: no message/inner-exception constructors by design.
internal sealed class ArchiveException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}
#pragma warning restore CA1032

/// <summary>
/// Wraps the archive's data key so that the key file is useless to anyone who only has the disk. This is the confidentiality layer of the
/// archive: the sealed records are AES-256-GCM (<see cref="VaultCipher"/>, the project's own cipher), and the key is bound to the
/// operator by one of the protectors below.
/// </summary>
internal interface IArchiveKeyProtector
{
    /// <summary>Stable name recorded in the key file and checked on open (<c>windows-dpapi-current-user</c>, <c>operator-secret</c>).</summary>
    string Name { get; }

    JsonObject Protect(byte[] dataKey);

    byte[] Unprotect(JsonObject wrapped);
}

/// <summary>
/// Portable protector: the key-encryption key is HKDF-SHA256 of an operator-supplied secret (the vault's own derivation), the wrapping is
/// the vault's AES-256-GCM. The secret is never written anywhere by the harness; losing it loses the archive.
/// </summary>
internal sealed class OperatorSecretKeyProtector : IArchiveKeyProtector
{
    private const string Context = "know-bench.archive-key";
    private readonly string secret;

    internal OperatorSecretKeyProtector(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 16)
            throw new ArchiveException("OperatorSecretTooShort");
        this.secret = secret;
    }

    public string Name => "operator-secret";

    public JsonObject Protect(byte[] dataKey)
    {
        var kek = VaultCipher.DeriveKey(secret);
        try
        {
            var sealedKey = VaultCipher.Encrypt(kek, Context, 1, Convert.ToBase64String(dataKey));
            return new JsonObject { ["nonce"] = sealedKey.NonceBase64, ["ciphertext"] = sealedKey.CiphertextBase64, ["tag"] = sealedKey.TagBase64 };
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    public byte[] Unprotect(JsonObject wrapped)
    {
        var kek = VaultCipher.DeriveKey(secret);
        try
        {
            var text = VaultCipher.Decrypt(kek, Context, 1, new VaultSealedValue(
                wrapped["nonce"]!.GetValue<string>(), wrapped["ciphertext"]!.GetValue<string>(), wrapped["tag"]!.GetValue<string>()));
            return Convert.FromBase64String(text);
        }
        catch (Exception ex) when (ex is VaultCorruptedException or FormatException or InvalidOperationException or NullReferenceException)
        {
            throw new ArchiveException("KeyUnavailable");
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }
}

/// <summary>
/// Windows DPAPI, <b>CurrentUser</b> scope (no machine scope): the key can only be unwrapped by the same Windows account on the same
/// machine profile. Called through <c>crypt32</c> directly so the project adds no package dependency; isolated here so the rest of the
/// harness stays platform-neutral.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDpapiKeyProtector : IArchiveKeyProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("bops.know-bench.diagnosis-archive.key.v1");

    public string Name => "windows-dpapi-current-user";

    public JsonObject Protect(byte[] dataKey) =>
        new() { ["blob"] = Convert.ToBase64String(Transform(dataKey, protect: true)) };

    public byte[] Unprotect(JsonObject wrapped)
    {
        try { return Transform(Convert.FromBase64String(wrapped["blob"]!.GetValue<string>()), protect: false); }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or NullReferenceException or ArchiveException)
        { throw new ArchiveException("KeyUnavailable"); }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var data = Marshal.AllocHGlobal(input.Length);
        var entropy = Marshal.AllocHGlobal(Entropy.Length);
        var output = default(NativeMethods.DataBlob);
        try
        {
            Marshal.Copy(input, 0, data, input.Length);
            Marshal.Copy(Entropy, 0, entropy, Entropy.Length);
            var inBlob = new NativeMethods.DataBlob { Size = input.Length, Data = data };
            var entropyBlob = new NativeMethods.DataBlob { Size = Entropy.Length, Data = entropy };
            const int uiForbidden = 0x1;
            var ok = protect
                ? NativeMethods.CryptProtectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, uiForbidden, out output)
                : NativeMethods.CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, uiForbidden, out output);
            if (!ok) throw new ArchiveException("KeyUnavailable");
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                var zero = new byte[output.Size];
                Marshal.Copy(zero, 0, output.Data, zero.Length);
                _ = NativeMethods.LocalFree(output.Data);
            }

            Marshal.FreeHGlobal(data);
            Marshal.FreeHGlobal(entropy);
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct DataBlob
        {
            internal int Size;
            internal IntPtr Data;
        }

#pragma warning disable SYSLIB1054 // The test project does not enable unsafe code, which LibraryImport requires; the signatures are blittable.
        [DllImport("crypt32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CryptProtectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved,
            IntPtr prompt, int flags, out DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved,
            IntPtr prompt, int flags, out DataBlob dataOut);

        [DllImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr LocalFree(IntPtr memory);
#pragma warning restore SYSLIB1054
    }
}

/// <summary>
/// Operating-system access restriction for the archive and the review package. This is a different guarantee from encryption and from a
/// hash: it limits <i>who may open the files at all</i>. On Windows the directory gets a protected DACL (no inheritance from the parent)
/// granting only the current user; files inherit it. On Unix, directories are 0700 and files 0600. Existing locations are verified, never
/// silently loosened or "repaired".
/// </summary>
internal static class ArchiveAccess
{
    internal static void CreateRestrictedDirectory(string path)
    {
        if (Directory.Exists(path) || File.Exists(path)) throw new ArchiveException("LocationOccupied");
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (parent is null || !Directory.Exists(parent)) throw new ArchiveException("ParentMissing");
        if (OperatingSystem.IsWindows()) CreateWindowsDirectory(path);
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        RequireRestricted(path);
    }

    /// <summary>Opens a new file for writing; fails if it exists. Windows files inherit the restricted directory DACL; Unix files are 0600.</summary>
    internal static FileStream CreateNewFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    /// <summary>Throws <see cref="ArchiveException"/> (<c>PermissionsTooBroad</c>) when anyone but the current user could access the item.</summary>
    internal static void RequireRestricted(string path)
    {
        if (!IsRestricted(path)) throw new ArchiveException("PermissionsTooBroad");
    }

    internal static bool IsRestricted(string path)
    {
        if (OperatingSystem.IsWindows()) return IsRestrictedWindows(path);
        var others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(path) & others) == 0;
    }

    /// <summary>Gives every file in the tree the same timestamps, so creation order cannot tell a reviewer which arm ran first.</summary>
    internal static void NormalizeTimestamps(string directory, DateTime utc)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetCreationTimeUtc(file, utc);
            File.SetLastWriteTimeUtc(file, utc);
            File.SetLastAccessTimeUtc(file, utc);
        }

        foreach (var sub in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).Append(directory))
        {
            Directory.SetCreationTimeUtc(sub, utc);
            Directory.SetLastWriteTimeUtc(sub, utc);
            Directory.SetLastAccessTimeUtc(sub, utc);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CreateWindowsDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsRestrictedWindows(string path)
    {
        var self = WindowsIdentity.GetCurrent().User!;
        FileSystemSecurity security;
        if (Directory.Exists(path))
        {
            var directory = new DirectoryInfo(path);
            security = directory.GetAccessControl();
            if (!security.AreAccessRulesProtected) return false;
        }
        else security = new FileInfo(path).GetAccessControl();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        var any = false;
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (!self.Equals(rule.IdentityReference)) return false;
            any = true;
        }

        return any;
    }
}
