// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace bOps.Packages.Network.Native.Core;

/// <summary>
/// A minimal SNTP/NTP client over UDP/123 (RFC 4330 client mode), used by
/// <c>network.ntp_probe</c>. Sends exactly one request and computes offset and round-trip time
/// from the classic four timestamps; never a full NTP association, never a source of system time.
/// </summary>
public static class SntpClient
{
    private const int PacketLength = 48;
    private static readonly DateTime NtpEpoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Sends one SNTP request to <paramref name="host"/> and returns the parsed result.</summary>
    public static async Task<NtpProbeResult> ProbeAsync(string host, int timeoutMilliseconds, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        using var client = new UdpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMilliseconds));

        var request = new byte[PacketLength];
        request[0] = 0b00_100_011; // LI = 0 (no warning), VN = 4, Mode = 3 (client)

        try
        {
            client.Connect(host, 123);
            var t1 = DateTimeOffset.UtcNow;
            WriteTimestamp(request.AsSpan(40, 8), t1);
            await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);

            var response = await client.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            var t4 = DateTimeOffset.UtcNow;

            return Parse(host, response.Buffer, t1, t4);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new NtpProbeResult(host, DateTimeOffset.UtcNow, null, null, null, null, null, Valid: false, $"Timed out after {timeoutMilliseconds} ms.");
        }
        catch (SocketException ex)
        {
            return new NtpProbeResult(host, DateTimeOffset.UtcNow, null, null, null, null, null, Valid: false, ex.Message);
        }
    }

    internal static NtpProbeResult Parse(string host, byte[] response, DateTimeOffset t1, DateTimeOffset t4)
    {
        var localUtc = t4;

        if (response.Length < PacketLength)
        {
            return new NtpProbeResult(host, localUtc, null, null, null, null, null, Valid: false, "Reply shorter than an NTP packet (48 bytes).");
        }

        var leapIndicator = (response[0] >> 6) & 0x3;
        var version = (response[0] >> 3) & 0x7;
        var mode = response[0] & 0x7;
        var stratum = response[1];

        if (mode != 4)
        {
            return new NtpProbeResult(host, localUtc, null, null, null, null, stratum, Valid: false, $"Reply mode {mode} is not 4 (server).");
        }

        if (stratum == 0)
        {
            var kissCode = Encoding.ASCII.GetString(response, 12, 4).TrimEnd('\0');
            return new NtpProbeResult(host, localUtc, null, null, null, 0, version, Valid: false, $"Kiss-of-death reply (code {kissCode}).");
        }

        var t2 = ReadTimestamp(response.AsSpan(32, 8));
        var t3 = ReadTimestamp(response.AsSpan(40, 8));

        if (leapIndicator == 3)
        {
            return new NtpProbeResult(host, localUtc, t3, null, null, stratum, version, Valid: false, "Server reports an unsynchronized clock (leap indicator alarm).");
        }

        var offsetMs = (((t2 - t1) + (t3 - t4)) / 2).TotalMilliseconds;
        var roundTripMs = ((t4 - t1) - (t3 - t2)).TotalMilliseconds;

        return new NtpProbeResult(host, localUtc, t3, offsetMs, Math.Max(0, roundTripMs), stratum, version, Valid: true, null);
    }

    private static void WriteTimestamp(Span<byte> destination, DateTimeOffset value)
    {
        var elapsed = value.UtcDateTime - NtpEpoch;
        var seconds = (uint)elapsed.TotalSeconds;
        var fraction = (uint)((elapsed.TotalSeconds - Math.Floor(elapsed.TotalSeconds)) * uint.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(destination[..4], seconds);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], fraction);
    }

    private static DateTimeOffset ReadTimestamp(ReadOnlySpan<byte> source)
    {
        var seconds = BinaryPrimitives.ReadUInt32BigEndian(source[..4]);
        var fraction = BinaryPrimitives.ReadUInt32BigEndian(source[4..]);
        var milliseconds = seconds * 1000.0 + fraction / (double)uint.MaxValue * 1000.0;
        return new DateTimeOffset(NtpEpoch, TimeSpan.Zero).AddMilliseconds(milliseconds);
    }
}
