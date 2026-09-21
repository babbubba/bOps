// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;

namespace bOps.Api.Tests;

/// <summary>The UI polls several read-only endpoints; that must not exhaust the bucket that protects mutations.</summary>
public sealed class RateLimitTests
{
    [Fact]
    public async Task AuthenticatedReads_AreNotLimitedByTheStrictMutationBucket()
    {
        using var factory = new TestAppFactory { ChatModel = new QueueChatModel() };
        using var client = factory.CreateClient();

        // 250 > the 120-token strict bucket; well under the 600-token read bucket.
        for (var i = 0; i < 250; i++)
        {
            var response = await client.GetAsync(new Uri("/api/tools", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Mutations_KeepTheStrictBucket_EvenAfterManyReads()
    {
        using var factory = new TestAppFactory { ChatModel = new QueueChatModel() };
        using var client = factory.CreateClient();

        for (var i = 0; i < 150; i++)
        {
            var response = await client.PostAsJsonAsync("/api/agents/tasks", new StartTaskRequest(""));
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Assert.True(i >= 120, $"limited too early, at request {i}");
                return;
            }
        }

        Assert.Fail("the strict bucket never rejected a mutation");
    }
}
