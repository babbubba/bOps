// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.DiagnosticEvaluation;

// BOPS-0126: dotnet run --project tests/bOps.DiagnosticEvaluation -- --campaign <campaign dir> [--keys <keys dir>]
// Reads a finished A/B/C/D campaign without changing it and writes auto-evaluation/<blindCode>.json and auto-evaluation/manifest.json.
string? campaign = null;
var keys = Path.Combine(AppContext.BaseDirectory, "keys");
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--campaign" when i + 1 < args.Length:
            campaign = args[++i];
            break;
        case "--keys" when i + 1 < args.Length:
            keys = args[++i];
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

if (campaign is null || !File.Exists(Path.Combine(campaign, "campaign.json")))
{
    Console.Error.WriteLine("Usage: bOps.DiagnosticEvaluation --campaign <campaign directory with campaign.json> [--keys <directory>]");
    return 2;
}

var summary = await CampaignEvaluator.RunAsync(campaign, keys, CancellationToken.None).ConfigureAwait(false);
foreach (var line in summary)
{
    Console.WriteLine(line);
}

return 0;
