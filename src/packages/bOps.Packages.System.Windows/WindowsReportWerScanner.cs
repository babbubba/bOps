// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace bOps.Packages.Sys.Windows;

/// <summary>The needed keys of one Report.wer file, and how the scan ended.</summary>
/// <param name="Values">The needed keys found, by name.</param>
/// <param name="BytesRead">The bytes read from the file.</param>
/// <param name="ReachedCap">True when the byte cap stopped the scan before the end of the file and before the header ended.</param>
internal sealed record ReportWerScan(IReadOnlyDictionary<string, string> Values, long BytesRead, bool ReachedCap)
{
    /// <summary>True when the scan found the key a crash record cannot do without (<c>EventType</c>).</summary>
    public bool HasNeededKeys => Values.ContainsKey("EventType");
}

/// <summary>
/// A bounded streaming key scan of an official Report.wer file (ADR-0032 HARDEN-7 amendment §4), replacing the old whole-file read
/// that skipped every file above 32 KiB. At most <see cref="PerFileBytes"/> bytes of a file are read, line by line, keeping only the
/// needed keys; the scan stops at the end of the file, at the cap, or as soon as the report header has ended (the first
/// <c>DynamicSig[</c> or <c>LoadedModule[</c> line after <c>EventType</c>), so a large file costs little more than a small one. The
/// file is decoded from its byte-order mark (Report.wer is normally UTF-16), else as UTF-8.
/// </summary>
internal static class WindowsReportWerScanner
{
    /// <summary>The most bytes read from one Report.wer.</summary>
    internal const int PerFileBytes = 256 * 1024;

    /// <summary>The most Report.wer bytes read by one call, over every directory.</summary>
    internal const long PerCallBytes = 32L * 1024 * 1024;

    private const int MaximumValueCharacters = 2_048;

    private static readonly HashSet<string> NeededKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "EventType", "EventTime", "ReportIdentifier", "IntegratorReportIdentifier", "NsAppName", "Response.LegacyBucketId",
        "Sig[0].Value", "Sig[1].Value", "Sig[2].Value", "Sig[3].Value", "Sig[4].Value",
        "Sig[5].Value", "Sig[6].Value", "Sig[7].Value", "Sig[8].Value", "Sig[9].Value",
    };

    /// <summary>Scans <paramref name="stream"/>, reading at most <paramref name="byteCap"/> bytes.</summary>
    internal static ReportWerScan Scan(Stream stream, int byteCap)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(byteCap);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bounded = new BoundedReadStream(stream, byteCap);
        using var reader = new StreamReader(bounded, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: true, bufferSize: 4_096, leaveOpen: true);
        var headerEnded = false;
        while (reader.ReadLine() is { } line)
        {
            if (bounded.HitCap && reader.Peek() < 0)
            {
                // The cap cut this last line: a value it carries may be incomplete, so it is not used.
                break;
            }

            if (values.ContainsKey("EventType")
                && (line.StartsWith("DynamicSig[", StringComparison.Ordinal) || line.StartsWith("LoadedModule[", StringComparison.Ordinal)))
            {
                headerEnded = true;
                break;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (!NeededKeys.Contains(key))
            {
                continue;
            }

            var value = line[(separator + 1)..].Trim();
            values.TryAdd(key, value.Length > MaximumValueCharacters ? value[..MaximumValueCharacters] : value);
        }

        return new ReportWerScan(values, bounded.BytesRead, !headerEnded && bounded.HitCap);
    }

    /// <summary>The <c>EventTime</c> of a scanned report (a FILETIME), when present and valid.</summary>
    internal static DateTimeOffset? EventTime(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!long.TryParse(values.GetValueOrDefault("EventTime"), NumberStyles.None, CultureInfo.InvariantCulture, out var fileTime) || fileTime <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromFileTime(fileTime).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>A read-only view of the first bytes of a stream that reports whether more data lay beyond the cap.</summary>
    private sealed class BoundedReadStream(Stream inner, int cap) : Stream
    {
        public long BytesRead { get; private set; }

        public bool HitCap { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var left = cap - BytesRead;
            if (left <= 0)
            {
                // Peek one byte to tell "the file ended exactly at the cap" from "the cap cut it".
                if (!HitCap && inner.ReadByte() >= 0)
                {
                    HitCap = true;
                }

                return 0;
            }

            var read = inner.Read(buffer, offset, (int)Math.Min(count, left));
            BytesRead += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
