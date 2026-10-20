// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
#pragma warning disable CA1031 // Observe failures, then rethrow unchanged.
using System.Net;
using bOps.Abstractions;

namespace bOps.AgentFramework.Tests;

/// <summary>One instance per condition, immediately before its sole transport handler. No payloads or inferred usage.</summary>
internal sealed class BenchmarkHttpBudget(HttpMessageHandler transport) : DelegatingHandler(transport)
{
    private readonly object gate = new();
    private readonly List<BenchmarkHttpAttempt> attempts = [];
    private int sent;
    private int refused;

    internal int Sent { get { lock (gate) return sent; } }
    internal int Refused { get { lock (gate) return refused; } }
    internal BenchmarkHttpAttempt[] Attempts { get { lock (gate) return attempts.ToArray(); } }

    internal static HttpMessageHandler CreateTransport() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false, // A redirect must never create an uncounted request below this boundary.
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int attempt;
        lock (gate)
        {
            if (sent >= KnowledgeBenchmark.MaximumCalls)
            {
                refused++;
                attempts.Add(new(DateTimeOffset.UtcNow, null, "BudgetExhausted", null));
                // Unknown is non-retryable in the existing broker. The export retains the independent budget cause.
                throw new ModelProtocolException("Benchmark HTTP budget exhausted before dispatch.")
                    { FailureKind = ModelFailureKind.Unknown };
            }
            attempt = ++sent;
        }
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            Record(new(DateTimeOffset.UtcNow, attempt, "ResponseHeadersReceived", (int)response.StatusCode));
            return response;
        }
        catch (OperationCanceledException)
        {
            Record(new(DateTimeOffset.UtcNow, attempt, "CancelledOrTimedOut", null));
            throw; // Only the broker/adapter knows which deadline or cancellation fired.
        }
        catch (Exception)
        {
            Record(new(DateTimeOffset.UtcNow, attempt, "TransportFailure", null));
            throw;
        }
    }

    private void Record(BenchmarkHttpAttempt attempt)
    {
        lock (gate) attempts.Add(attempt);
    }
}

internal sealed record BenchmarkHttpAttempt(DateTimeOffset TimestampUtc, int? HttpAttempt, string Outcome, int? ProviderStatusCode);
