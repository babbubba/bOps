// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace bOps.Packages.Network.Native.Core;

/// <summary>
/// A minimal typed DNS client over UDP/53, used only when <c>network.dns_query</c> is given an
/// explicit <c>server</c>. Only the A, AAAA and PTR query types and the IN class are ever sent —
/// there is no raw query type, class or option (agentic/03-security-rules.md rule S1's spirit:
/// a typed, enumerable surface, never an arbitrary one). Not a general-purpose DNS library: it
/// implements exactly enough of RFC 1035 to send one question and parse one reply, including
/// compression-pointer following in names.
/// </summary>
public static class DnsUdpClient
{
    private const int HeaderLength = 12;

    /// <summary>Sends one A/AAAA/PTR query to <paramref name="server"/> and returns the parsed answers.</summary>
    public static async Task<DnsQueryResult> QueryAsync(string name, string recordType, string server, int timeoutMilliseconds, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordType);
        ArgumentException.ThrowIfNullOrWhiteSpace(server);

        var startedAt = DateTimeOffset.UtcNow;
        using var client = new UdpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMilliseconds));

        try
        {
            var queryName = recordType == "PTR" ? ToReverseLookupName(name) : name;
            var queryType = QueryTypeCode(recordType);
            var transactionId = (ushort)RandomNumberGenerator.GetInt32(ushort.MinValue, ushort.MaxValue + 1);
            var request = BuildQuery(transactionId, queryName, queryType);

            client.Connect(server, 53);
            await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);

            var response = await client.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            var elapsed = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            var answers = ParseResponse(response.Buffer, transactionId, recordType);
            return new DnsQueryResult(name, recordType, server, answers, elapsed, Success: true, ErrorMessage: null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var elapsed = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            return new DnsQueryResult(name, recordType, server, [], elapsed, Success: false, ErrorMessage: $"Timed out after {timeoutMilliseconds} ms.");
        }
        catch (SocketException ex)
        {
            var elapsed = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            return new DnsQueryResult(name, recordType, server, [], elapsed, Success: false, ErrorMessage: ex.Message);
        }
        catch (DnsProtocolException ex)
        {
            var elapsed = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            return new DnsQueryResult(name, recordType, server, [], elapsed, Success: false, ErrorMessage: ex.Message);
        }
    }

    private static string ToReverseLookupName(string address)
    {
        var parsed = IPAddress.Parse(address);
        return parsed.AddressFamily == AddressFamily.InterNetwork
            ? string.Join('.', parsed.GetAddressBytes().Reverse()) + ".in-addr.arpa"
            : string.Join('.', ToNibbles(parsed.GetAddressBytes())) + ".ip6.arpa";
    }

    private static IEnumerable<string> ToNibbles(byte[] bytes)
    {
        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            yield return (bytes[i] & 0x0F).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
            yield return (bytes[i] >> 4).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static ushort QueryTypeCode(string recordType) => recordType switch
    {
        "A" => 1,
        "AAAA" => 28,
        "PTR" => 12,
        _ => throw new ArgumentOutOfRangeException(nameof(recordType), recordType, "Only A, AAAA and PTR are supported."),
    };

    internal static byte[] BuildQuery(ushort transactionId, string name, ushort queryType)
    {
        var labels = name.Trim('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        var buffer = new List<byte>(HeaderLength + name.Length + 16);

        Span<byte> header = stackalloc byte[HeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(header[0..], transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100); // recursion desired
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1); // qdcount
        buffer.AddRange(header.ToArray());

        foreach (var label in labels)
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }

        buffer.Add(0); // root label

        Span<byte> question = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(question[0..], queryType);
        BinaryPrimitives.WriteUInt16BigEndian(question[2..], 1); // IN class
        buffer.AddRange(question.ToArray());

        return buffer.ToArray();
    }

    internal static IReadOnlyList<DnsAnswer> ParseResponse(byte[] response, ushort expectedTransactionId, string recordType)
    {
        if (response.Length < HeaderLength)
        {
            throw new DnsProtocolException("Response shorter than a DNS header.");
        }

        var transactionId = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(0, 2));
        if (transactionId != expectedTransactionId)
        {
            throw new DnsProtocolException("Response transaction id does not match the query.");
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2));
        var responseCode = flags & 0x000F;
        if (responseCode != 0)
        {
            throw new DnsProtocolException($"Server returned RCODE {responseCode}.");
        }

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(4, 2));
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6, 2));

        var offset = HeaderLength;
        for (var i = 0; i < questionCount; i++)
        {
            SkipName(response, ref offset);
            offset += 4; // qtype + qclass
        }

        var answers = new List<DnsAnswer>();
        for (var i = 0; i < answerCount; i++)
        {
            if (offset >= response.Length)
            {
                break;
            }

            var name = ReadName(response, ref offset);
            if (offset + 10 > response.Length)
            {
                throw new DnsProtocolException("Truncated resource record.");
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset, 2));
            offset += 2;
            offset += 2; // class
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(offset, 4));
            offset += 4;
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset, 2));
            offset += 2;

            if (offset + dataLength > response.Length)
            {
                throw new DnsProtocolException("Resource record data extends past the message.");
            }

            var data = type switch
            {
                1 when dataLength == 4 => new IPAddress(response.AsSpan(offset, 4).ToArray()).ToString(),
                28 when dataLength == 16 => new IPAddress(response.AsSpan(offset, 16).ToArray()).ToString(),
                12 => ReadName(response, ref offset, dataLength),
                _ => null,
            };

            if (type != 12)
            {
                offset += dataLength;
            }

            if (data is not null)
            {
                answers.Add(new DnsAnswer(name, recordType, data, (int)Math.Min(ttl, int.MaxValue)));
            }
        }

        return answers;
    }

    private static void SkipName(byte[] message, ref int offset)
    {
        while (offset < message.Length)
        {
            var length = message[offset];
            if (length == 0)
            {
                offset++;
                return;
            }

            if ((length & 0xC0) == 0xC0)
            {
                offset += 2;
                return;
            }

            offset += 1 + length;
        }
    }

    private static string ReadName(byte[] message, ref int offset)
    {
        var jumped = false;
        var cursor = offset;
        var labels = new List<string>();
        var guard = 0;

        while (guard++ < 128)
        {
            if (cursor >= message.Length)
            {
                throw new DnsProtocolException("Name extends past the message.");
            }

            var length = message[cursor];
            if (length == 0)
            {
                cursor++;
                if (!jumped)
                {
                    offset = cursor;
                }

                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= message.Length)
                {
                    throw new DnsProtocolException("Compression pointer extends past the message.");
                }

                var pointer = ((length & 0x3F) << 8) | message[cursor + 1];
                if (!jumped)
                {
                    offset = cursor + 2;
                }

                jumped = true;
                cursor = pointer;
                continue;
            }

            if (cursor + 1 + length > message.Length)
            {
                throw new DnsProtocolException("Label extends past the message.");
            }

            labels.Add(Encoding.ASCII.GetString(message, cursor + 1, length));
            cursor += 1 + length;
        }

        return string.Join('.', labels);
    }

    private static string ReadName(byte[] message, ref int offset, int dataLength)
    {
        var start = offset;
        var name = ReadName(message, ref offset);
        offset = start + dataLength;
        return name;
    }
}

/// <summary>A DNS reply that could not be parsed as a well-formed response to the query sent.</summary>
public sealed class DnsProtocolException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public DnsProtocolException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public DnsProtocolException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and <paramref name="innerException"/>.</summary>
    public DnsProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
