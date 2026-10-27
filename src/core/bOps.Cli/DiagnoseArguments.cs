// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace bOps.Cli;

/// <summary>What <c>bops diagnose</c> was asked to do (C01-R23).</summary>
/// <param name="Goal">The operator's description of the problem.</param>
/// <param name="MaxRounds">An override of the investigation-round limit, or <c>null</c> for the default.</param>
/// <param name="MaxToolCalls">An override of the tool-call limit, or <c>null</c> for the default.</param>
/// <param name="MaxTokens">An override of the token limit, or <c>null</c> for the default.</param>
/// <param name="TimeoutMinutes">An override of the time limit in minutes, or <c>null</c> for the default.</param>
internal sealed record DiagnoseInvocation(string Goal, int? MaxRounds, int? MaxToolCalls, long? MaxTokens, int? TimeoutMinutes);

/// <summary>
/// The arguments of <c>bops diagnose "&lt;goal&gt;" --workflow maf [--max-rounds N] [--max-tool-calls N] [--max-tokens N] [--timeout-minutes N]</c>.
/// <c>--workflow</c> is required and names the one workflow that exists, so a future workflow is a new value and never a change of what
/// an existing command does. A limit given here can only tighten what the hard maxima allow; the validation of the value is the limits' own.
/// </summary>
internal static class DiagnoseArguments
{
    internal const string Usage =
        "Usage: bops diagnose \"<goal>\" --workflow maf [--max-rounds <N>] [--max-tool-calls <N>] [--max-tokens <N>] [--timeout-minutes <N>]";

    /// <summary>Parses the arguments after <c>diagnose</c>. Exactly one of the result and the error is <c>null</c>.</summary>
    internal static (DiagnoseInvocation? Invocation, string? Error) Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var goal = new List<string>();
        string? workflow = null;
        int? rounds = null;
        int? toolCalls = null;
        long? tokens = null;
        int? timeout = null;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                goal.Add(argument);
                continue;
            }

            if (index + 1 >= args.Count)
            {
                return (null, $"'{argument}' needs a value.");
            }

            var value = args[++index];
            switch (argument.ToLowerInvariant())
            {
                case "--workflow":
                    workflow = value;
                    break;
                case "--max-rounds":
                    if (!TryPositive(value, out var parsedRounds)) { return (null, Invalid(argument, value)); }
                    rounds = parsedRounds;
                    break;
                case "--max-tool-calls":
                    if (!TryPositive(value, out var parsedCalls)) { return (null, Invalid(argument, value)); }
                    toolCalls = parsedCalls;
                    break;
                case "--max-tokens":
                    if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedTokens) || parsedTokens < 1) { return (null, Invalid(argument, value)); }
                    tokens = parsedTokens;
                    break;
                case "--timeout-minutes":
                    if (!TryPositive(value, out var parsedTimeout)) { return (null, Invalid(argument, value)); }
                    timeout = parsedTimeout;
                    break;
                default:
                    return (null, $"Unknown option '{argument}'.");
            }
        }

        if (!string.Equals(workflow, "maf", StringComparison.OrdinalIgnoreCase))
        {
            return (null, workflow is null ? "'--workflow maf' is required." : $"Unknown workflow '{workflow}'; the only one is 'maf'.");
        }

        var text = string.Join(' ', goal).Trim();
        return text.Length == 0
            ? (null, "A goal is required: the problem to diagnose.")
            : (new DiagnoseInvocation(text, rounds, toolCalls, tokens, timeout), null);
    }

    private static bool TryPositive(string value, out int parsed) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed >= 1;

    private static string Invalid(string option, string value) => $"'{option}' needs a positive whole number (found '{value}').";
}
