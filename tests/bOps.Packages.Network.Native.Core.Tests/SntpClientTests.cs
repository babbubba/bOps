// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Core.Tests;

public sealed class SntpClientTests
{
    private static readonly DateTime NtpEpoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Parse_ValidReply_ComputesOffsetAndRoundTrip()
    {
        // One-way delay 20ms (RTT 40ms), server clock 50ms ahead, instantaneous server turnaround:
        // offset = ((T2-T1)+(T3-T4))/2 = θ regardless of delay, per RFC 4330.
        var t1 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var t4 = t1.AddMilliseconds(40);
        var serverTime = t1.AddMilliseconds(70); // δ(20) + θ(50)

        var reply = BuildPacket(leapIndicator: 0, version: 4, mode: 4, stratum: 1, receive: serverTime, transmit: serverTime);

        var result = SntpClient.Parse("time.example", reply, t1, t4);

        Assert.True(result.Valid);
        Assert.Equal(1, result.Stratum);
        Assert.Equal(4, result.Version);
        Assert.NotNull(result.OffsetMilliseconds);
        Assert.InRange(result.OffsetMilliseconds!.Value, 40, 60);
        Assert.NotNull(result.RoundTripMilliseconds);
        Assert.InRange(result.RoundTripMilliseconds!.Value, 30, 50);
    }

    [Fact]
    public void Parse_UnsynchronizedLeapIndicator_IsInvalid()
    {
        var t1 = DateTimeOffset.UtcNow;
        var reply = BuildPacket(leapIndicator: 3, version: 4, mode: 4, stratum: 1, receive: t1, transmit: t1);

        var result = SntpClient.Parse("time.example", reply, t1, t1.AddMilliseconds(10));

        Assert.False(result.Valid);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void Parse_KissOfDeath_IsInvalid()
    {
        var t1 = DateTimeOffset.UtcNow;
        var reply = BuildPacket(leapIndicator: 0, version: 4, mode: 4, stratum: 0, receive: t1, transmit: t1, kissCode: "RATE");

        var result = SntpClient.Parse("time.example", reply, t1, t1.AddMilliseconds(10));

        Assert.False(result.Valid);
        Assert.Contains("RATE", result.ErrorMessage);
    }

    [Fact]
    public void Parse_WrongMode_IsInvalid()
    {
        var t1 = DateTimeOffset.UtcNow;
        var reply = BuildPacket(leapIndicator: 0, version: 4, mode: 3, stratum: 1, receive: t1, transmit: t1);

        var result = SntpClient.Parse("time.example", reply, t1, t1.AddMilliseconds(10));

        Assert.False(result.Valid);
    }

    [Fact]
    public void Parse_ShortPacket_IsInvalid()
    {
        var result = SntpClient.Parse("time.example", new byte[10], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        Assert.False(result.Valid);
        Assert.NotNull(result.ErrorMessage);
    }

    private static byte[] BuildPacket(int leapIndicator, int version, int mode, byte stratum, DateTimeOffset receive, DateTimeOffset transmit, string? kissCode = null)
    {
        var packet = new byte[48];
        packet[0] = (byte)((leapIndicator << 6) | (version << 3) | mode);
        packet[1] = stratum;

        if (kissCode is not null)
        {
            System.Text.Encoding.ASCII.GetBytes(kissCode.PadRight(4, '\0')).CopyTo(packet, 12);
        }

        WriteTimestamp(packet.AsSpan(32, 8), receive);
        WriteTimestamp(packet.AsSpan(40, 8), transmit);
        return packet;
    }

    private static void WriteTimestamp(Span<byte> destination, DateTimeOffset value)
    {
        var elapsed = value.UtcDateTime - NtpEpoch;
        var seconds = (uint)elapsed.TotalSeconds;
        var fraction = (uint)((elapsed.TotalSeconds - Math.Floor(elapsed.TotalSeconds)) * uint.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(destination[..4], seconds);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], fraction);
    }
}
