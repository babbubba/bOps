// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.AgentFramework.Tests.PromptAssembly;

/// <summary>
/// PA-1, level A: the provider requests the product assembles today, frozen as a baseline. Every request is produced by the real
/// runner, broker, MAF context projector and OpenAI-compatible adapter against an offline upstream, and compared as the JSON body
/// the adapter sent (see <see cref="PaCompose"/>). The files under <c>Golden/pa1/</c> describe what the prompts say NOW, including
/// the defects PRA-R2, PRA-R3, PRA-R4 and PRA-P1; a later change of a prompt must change them on purpose, in the same commit.
/// </summary>
[Collection("MAF summarization telemetry")]
public sealed class RequestAssemblyGoldenTests
{
    public static TheoryData<string> Cases => [.. PaCases.All.Select(item => item.Id)];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheAssembledRequestsMatchTheBaseline(string id)
    {
        var item = PaCases.Get(id);
        var run = await item.Run();

        Assert.Equal(0, run.Unconsumed);
        PaGolden.Verify(item.File, PaGolden.Document(item.Id, item.Title, run, item.Picks));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheScenarioIsReproducible_TwoRunsGiveTheSameBytes(string id)
    {
        var item = PaCases.Get(id);
        var first = await item.Run();
        var second = await item.Run();

        Assert.Equal(first.Bodies, second.Bodies);
    }
}
