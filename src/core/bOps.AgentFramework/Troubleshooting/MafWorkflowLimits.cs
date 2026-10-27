// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.AgentFramework.Troubleshooting;

/// <summary>
/// The limits of one MAF troubleshooting workflow run (C01-R23). Every limit has a conservative default and a hard maximum that a
/// configuration cannot exceed; a value above the maximum or below one is refused, never clamped.
/// </summary>
public sealed record MafWorkflowLimits
{
    /// <summary>The most investigation rounds a run may ever make, whatever is configured.</summary>
    public const int HardMaxInvestigationRounds = 4;

    /// <summary>The most tool calls a run may ever attempt, whatever is configured.</summary>
    public const int HardMaxToolCalls = 100;

    /// <summary>The most model calls a run may ever make, whatever is configured.</summary>
    public const int HardMaxModelCalls = 120;

    /// <summary>The longest a run may ever last, whatever is configured.</summary>
    public static readonly TimeSpan HardMaxDuration = TimeSpan.FromHours(1);

    /// <summary>The investigation rounds: the first one plus the follow-ups the reviewer may ask for. Default 2 (one follow-up).</summary>
    public int MaxInvestigationRounds { get; init; } = 2;

    /// <summary>The tool calls the whole run may attempt, executed or refused, across all agents. Default 24.</summary>
    public int MaxToolCalls { get; init; } = 24;

    /// <summary>The model calls the whole run may make across all agents. Default 40.</summary>
    public int MaxModelCalls { get; init; } = 40;

    /// <summary>The model tokens the whole run may use across all agents (known usage only). Default 200,000.</summary>
    public long MaxTokens { get; init; } = 200_000;

    /// <summary>The wall-clock time the whole run may take. Default 15 minutes.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The tool-calling iterations one agent may make in one turn before the framework stops its loop. Default 6.</summary>
    public int MaxToolIterationsPerAgentTurn { get; init; } = 6;

    /// <summary>Checks every limit against its hard maximum and returns this instance.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A limit is below one or above its hard maximum; the message names it.</exception>
    public MafWorkflowLimits Validate()
    {
        Check(nameof(MaxInvestigationRounds), MaxInvestigationRounds, HardMaxInvestigationRounds);
        Check(nameof(MaxToolCalls), MaxToolCalls, HardMaxToolCalls);
        Check(nameof(MaxModelCalls), MaxModelCalls, HardMaxModelCalls);
        Check(nameof(MaxToolIterationsPerAgentTurn), MaxToolIterationsPerAgentTurn, HardMaxToolCalls);
        if (MaxTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTokens), MaxTokens, "MaxTokens must be at least 1.");
        }

        if (MaxDuration <= TimeSpan.Zero || MaxDuration > HardMaxDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDuration), MaxDuration, $"MaxDuration must be positive and at most {HardMaxDuration}.");
        }

        return this;
    }

    private static void Check(string name, int value, int hardMax)
    {
        if (value < 1 || value > hardMax)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be between 1 and {hardMax}.");
        }
    }
}
