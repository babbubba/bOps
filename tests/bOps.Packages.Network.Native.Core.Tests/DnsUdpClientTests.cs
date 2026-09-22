// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Core.Tests;

public sealed class DnsUdpClientTests
{
    [Fact]
    public void BuildQuery_EncodesTransactionIdAndLabels()
    {
        var query = DnsUdpClient.BuildQuery(0x1234, "www.example.com", 1);

        Assert.Equal(0x12, query[0]);
        Assert.Equal(0x34, query[1]);
        Assert.Equal(1, query[5]); // qdcount low byte

        // "www" label: length 3 then 'w','w','w'
        Assert.Equal(3, query[12]);
        Assert.Equal((byte)'w', query[13]);
    }

    [Fact]
    public void ParseResponse_ReadsOneARecord()
    {
        var message = BuildMessage(
            transactionId: 0xABCD,
            questionName: "example.com",
            answers: [(name: PointerToQuestion, type: 1, ttl: 300, data: new byte[] { 93, 184, 216, 34 })]);

        var answers = DnsUdpClient.ParseResponse(message, 0xABCD, "A");

        var answer = Assert.Single(answers);
        Assert.Equal("93.184.216.34", answer.Data);
        Assert.Equal(300, answer.TtlSeconds);
    }

    [Fact]
    public void ParseResponse_FollowsCompressionPointerInName()
    {
        var message = BuildMessage(
            transactionId: 1,
            questionName: "example.com",
            answers: [(name: PointerToQuestion, type: 1, ttl: 60, data: new byte[] { 1, 2, 3, 4 })]);

        var answers = DnsUdpClient.ParseResponse(message, 1, "A");

        Assert.Equal("example.com", answers[0].Name);
    }

    [Fact]
    public void ParseResponse_RejectsMismatchedTransactionId()
    {
        var message = BuildMessage(transactionId: 5, questionName: "example.com", answers: []);

        Assert.Throws<DnsProtocolException>(() => DnsUdpClient.ParseResponse(message, 6, "A"));
    }

    [Fact]
    public void ParseResponse_RejectsShortMessage()
    {
        Assert.Throws<DnsProtocolException>(() => DnsUdpClient.ParseResponse(new byte[4], 0, "A"));
    }

    [Fact]
    public void ParseResponse_RejectsNonZeroResponseCode()
    {
        var message = BuildMessage(transactionId: 1, questionName: "example.com", answers: [], responseCode: 3);

        Assert.Throws<DnsProtocolException>(() => DnsUdpClient.ParseResponse(message, 1, "A"));
    }

    // A marker meaning "encode a compression pointer to offset 12 (the question name)" rather than a literal label sequence.
    private const string PointerToQuestion = "\0POINTER\0";

    private static byte[] BuildMessage(ushort transactionId, string questionName, (string name, ushort type, uint ttl, byte[] data)[] answers, int responseCode = 0)
    {
        var buffer = new List<byte>();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header[0..], transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)(0x8000 | responseCode));
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], (ushort)answers.Length);
        buffer.AddRange(header.ToArray());

        foreach (var label in questionName.Split('.'))
        {
            buffer.Add((byte)label.Length);
            buffer.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }

        buffer.Add(0);
        Span<byte> question = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(question[0..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(question[2..], 1);
        buffer.AddRange(question.ToArray());

        foreach (var (name, type, ttl, data) in answers)
        {
            if (name == PointerToQuestion)
            {
                buffer.Add(0xC0);
                buffer.Add(0x0C); // pointer to offset 12, the start of the question name
            }
            else
            {
                foreach (var label in name.Split('.'))
                {
                    buffer.Add((byte)label.Length);
                    buffer.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
                }

                buffer.Add(0);
            }

            var record = new byte[10];
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(0), type);
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(4), ttl);
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(8), (ushort)data.Length);
            buffer.AddRange(record);
            buffer.AddRange(data);
        }

        return buffer.ToArray();
    }
}
