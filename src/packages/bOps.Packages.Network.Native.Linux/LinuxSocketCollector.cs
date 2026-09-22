// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Linux;

/// <summary>
/// Reads <c>/proc/net/{tcp,tcp6,udp,udp6}</c> and maps socket inode to owning PID by a bounded
/// scan of <c>/proc/&lt;pid&gt;/fd</c> (V1.3-D, ADR-0035). A process directory this identity
/// cannot list, or the scan ceiling, leaves the affected sockets' PID <c>null</c> and
/// <c>pidMappingComplete</c> false — the gap is reported, never silently dropped.
/// </summary>
internal static class LinuxSocketCollector
{
    private static readonly string[] TcpStateNames =
    [
        "unknown", "established", "synSent", "synReceived", "finWait1", "finWait2", "timeWait",
        "close", "closeWait", "lastAck", "listen", "closing",
    ];

    internal static async Task<SocketsSnapshot> CollectAsync(CancellationToken ct)
    {
        var raw = new List<(SocketEntry Entry, long? Inode)>();
        raw.AddRange(await ParseFileAsync("/proc/net/tcp", "tcp", "ipv4", ct));
        raw.AddRange(await ParseFileAsync("/proc/net/tcp6", "tcp", "ipv6", ct));
        raw.AddRange(await ParseFileAsync("/proc/net/udp", "udp", "ipv4", ct));
        raw.AddRange(await ParseFileAsync("/proc/net/udp6", "udp", "ipv6", ct));

        var truncated = raw.Count > NetworkNativeLimits.CollectionScanCeiling;
        if (truncated)
        {
            raw = raw.Take(NetworkNativeLimits.CollectionScanCeiling).ToList();
        }

        var (inodeToPid, complete, detail) = BuildInodeMap(ct);

        var entries = raw.Select(item =>
        {
            if (item.Inode is not { } inode || !inodeToPid.TryGetValue(inode, out var pid))
            {
                return item.Entry;
            }

            return item.Entry with { Pid = pid, ProcessName = ResolveProcessName(pid) };
        }).ToArray();

        return new SocketsSnapshot(entries, complete, detail, truncated);
    }

    private static async Task<List<(SocketEntry Entry, long? Inode)>> ParseFileAsync(string path, string protocol, string addressFamily, CancellationToken ct)
    {
        var results = new List<(SocketEntry, long?)>();
        if (!File.Exists(path))
        {
            return results;
        }

        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(path, ct);
        }
        catch (IOException)
        {
            return results;
        }
        catch (UnauthorizedAccessException)
        {
            return results;
        }

        foreach (var line in lines.Skip(1))
        {
            ct.ThrowIfCancellationRequested();
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10)
            {
                continue;
            }

            var (localAddress, localPort) = ParseEndpoint(fields[1], addressFamily);
            var (remoteAddress, remotePort) = ParseEndpoint(fields[2], addressFamily);
            var stateCode = Convert.ToInt32(fields[3], 16);
            var inode = long.TryParse(fields[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInode) ? parsedInode : (long?)null;

            var isTcp = protocol == "tcp";
            var state = isTcp ? StateName(stateCode) : null;
            var reportedRemoteAddress = isTcp ? remoteAddress : null;
            var reportedRemotePort = isTcp ? remotePort : (int?)null;

            results.Add((new SocketEntry(protocol, addressFamily, localAddress, localPort, reportedRemoteAddress, reportedRemotePort, state, null, null), inode));
        }

        return results;
    }

    private static (string Address, int Port) ParseEndpoint(string field, string addressFamily)
    {
        var parts = field.Split(':');
        var addressHex = parts[0];
        var port = Convert.ToInt32(parts[1], 16);

        var groupCount = addressFamily == "ipv6" ? 4 : 1;
        var bytes = new byte[groupCount * 4];
        for (var group = 0; group < groupCount; group++)
        {
            var word = uint.Parse(addressHex.AsSpan(group * 8, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var wordBytes = BitConverter.GetBytes(word); // little-endian host order, matching /proc's kernel-native word
            Array.Copy(wordBytes, 0, bytes, group * 4, 4);
        }

        return (new IPAddress(bytes).ToString(), port);
    }

    private static string StateName(int code) => code >= 0 && code < TcpStateNames.Length ? TcpStateNames[code] : "unknown";

    private static (Dictionary<long, int> Map, bool Complete, string? Detail) BuildInodeMap(CancellationToken ct)
    {
        var map = new Dictionary<long, int>();
        var complete = true;
        var incompleteCount = 0;
        var scanned = 0;

        IEnumerable<string> processDirectories;
        try
        {
            processDirectories = Directory.EnumerateDirectories("/proc");
        }
        catch (IOException)
        {
            return (map, false, "Could not list /proc.");
        }

        foreach (var processDirectory in processDirectories)
        {
            ct.ThrowIfCancellationRequested();
            var pidText = Path.GetFileName(processDirectory);
            if (!int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            IEnumerable<string> fds;
            try
            {
                fds = Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd");
            }
            catch (UnauthorizedAccessException)
            {
                complete = false;
                incompleteCount++;
                continue;
            }
            catch (IOException)
            {
                continue; // the process exited between the listing and the read: not a permission gap
            }

            foreach (var fd in fds)
            {
                if (scanned++ > NetworkNativeLimits.CollectionScanCeiling)
                {
                    return (map, false, $"Stopped scanning file descriptors after {NetworkNativeLimits.CollectionScanCeiling} entries.");
                }

                string? target;
                try
                {
                    target = new FileInfo(fd).LinkTarget;
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                if (target is not null && target.StartsWith("socket:[", StringComparison.Ordinal) && target.EndsWith(']'))
                {
                    var inodeText = target[8..^1];
                    if (long.TryParse(inodeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var inode))
                    {
                        map.TryAdd(inode, pid);
                    }
                }
            }
        }

        var detail = complete ? null : $"{incompleteCount} process(es)' file descriptors were not listable by this identity.";
        return (map, complete, detail);
    }

    private static string? ResolveProcessName(int pid)
    {
        try
        {
            var commPath = $"/proc/{pid}/comm";
            return File.Exists(commPath) ? File.ReadAllText(commPath).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
