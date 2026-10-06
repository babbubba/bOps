// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Policy;
using bOps.Runtime;

namespace bOps.Cli;

/// <summary>The policy this CLI process loaded (ADR-0044 section 4), with the loader's message kept for the local operator only.</summary>
/// <param name="Config">The loaded configuration (the safe default with no file, everything forbidden after a failed load).</param>
/// <param name="State">Why it is what it is.</param>
/// <param name="Path">The absolute path it was read from.</param>
/// <param name="LoadError">The loader's message after a failed load (key path and line), shown locally; never sent anywhere.</param>
internal sealed record CliPolicy(PolicyConfig Config, PolicyLoadState State, string Path, string? LoadError);

/// <summary>
/// The configuration commands of <c>bops delegate</c> (ADR-0044 sections 10–12): <c>readiness</c>, <c>profiles init --read-only</c>
/// and <c>profiles check</c>. They decide nothing themselves — readiness and drift are the runtime's functions and the profiles are
/// the policy generator's — and they need no model provider. Nothing here prints an existing policy file's contents.
/// </summary>
internal sealed class DelegateSetupCommand(
    CliPolicy policy, IReadOnlyList<ToolManifest> availableTools, TimeProvider timeProvider, TextWriter output, TextWriter error)
{
    /// <summary>The exit code of <c>profiles check</c> when only informational drift was found.</summary>
    internal const int InformationalDrift = 8;

    /// <summary>A test seam for the overwrite re-check (ADR-0044 section 10.1 condition 5).</summary>
    internal Action? BeforeOverwriteRecheck { get; init; }

    public async Task<int> RunAsync(DelegateInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        return invocation.Action switch
        {
            DelegateAction.Readiness => await ReadinessAsync(invocation.Remediation),
            DelegateAction.ProfilesInit => await InitAsync(invocation.Write, invocation.Overwrite),
            DelegateAction.ProfilesCheck => await CheckAsync(),
            _ => throw new InvalidOperationException($"'{invocation.Action}' is not a configuration command."),
        };
    }

    // ---- readiness ----

    private async Task<int> ReadinessAsync(bool remediation)
    {
        var readiness = DelegationReadinessEvaluator.Evaluate(
            new PolicyRoleProfileSource(policy.Config), policy.State, availableTools, remediation, timeProvider.GetUtcNow());

        await output.WriteLineAsync($"Request shape: {(remediation ? "remediation (all four roles required)" : "diagnosis only (Discovery and Diagnostic required)")}");
        foreach (var role in readiness.Roles)
        {
            var label = role.State switch
            {
                RoleReadinessState.Ready => "ready",
                RoleReadinessState.Missing => "missing",
                RoleReadinessState.NotRequired => "not required",
                _ => "not usable",
            };
            var dimension = role.Dimension is { } d ? $" [{d}]" : string.Empty;
            var reason = role.Reason is { } r ? $" — {r}" : string.Empty;
            await output.WriteLineAsync($"{role.Role}: {label}{dimension}{reason}");
        }

        await output.WriteLineAsync($"Ready: {(readiness.Ready ? "yes" : "no")} (the required role profiles are {(readiness.Ready ? string.Empty : "not ")}usable for this type of delegation)");
        await WritePolicyLineAsync();
        await output.WriteLineAsync($"Profile drift: {readiness.ProfileDriftCount}");
        if (remediation)
        {
            await output.WriteLineAsync("The selected change, target, environment and input are validated when you submit.");
        }

        return readiness.Ready ? 0 : 2;
    }

    // ---- profiles check ----

    private async Task<int> CheckAsync()
    {
        var items = ProfileDrift.Evaluate(new PolicyRoleProfileSource(policy.Config), policy.State, availableTools, timeProvider.GetUtcNow());

        await WritePolicyLineAsync();
        foreach (var item in items)
        {
            await output.WriteLineAsync(item.Role is { } role
                ? $"{role}: {item.Kind}{(item.Subject is { } subject ? " " + subject : string.Empty)}"
                : $"(policy): {item.Kind}");
        }

        await output.WriteLineAsync($"Profile drift: {items.Count}");
        if (items.Count == 0)
        {
            return 0;
        }

        return items.Any(ProfileDrift.Blocks) ? 2 : InformationalDrift;
    }

    // ---- profiles init --read-only ----

    private async Task<int> InitAsync(bool write, bool overwrite)
    {
        GeneratedReadOnlyPolicy generated;
        try
        {
            generated = ReadOnlyProfileGenerator.Generate(availableTools, timeProvider);
        }
        catch (ProfileGenerationException ex)
        {
            await error.WriteLineAsync(ex.Message);
            return 1;
        }

        if (!write)
        {
            await error.WriteLineAsync($"Policy file: {policy.Path}");
            await error.WriteLineAsync($"Read tools listed: {generated.Tools.Count}");
            await error.WriteLineAsync("Review it, then run again with --write.");
            await output.WriteAsync(generated.Document);
            return 0;
        }

        if (!File.Exists(policy.Path))
        {
            try
            {
                PolicyFileWriter.CreateNew(policy.Path, generated.Document);
            }
            catch (IOException) when (File.Exists(policy.Path))
            {
                return await RefuseAsync("a policy file appeared while it was being written; it was left untouched", generated, wholeFile: false);
            }

            await output.WriteLineAsync($"Wrote {policy.Path}");
            await WriteRestartReminderAsync();
            return 0;
        }

        if (!overwrite)
        {
            return await RefuseAsync("a policy file already exists and is never overwritten silently", generated, wholeFile: false);
        }

        string existing;
        try
        {
            existing = await File.ReadAllTextAsync(policy.Path, new System.Text.UTF8Encoding(false, true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return await RefuseAsync("condition 1: the existing file could not be read", generated, wholeFile: true);
        }

        if (ReadOnlyProfileGenerator.CheckOverwritable(existing) is { } failed)
        {
            return await RefuseAsync(failed, generated, wholeFile: true);
        }

        var replaced = PolicyFileWriter.ReplaceIfUnchanged(
            policy.Path, existing, ReadOnlyProfileGenerator.CheckOverwritable, generated.Document, BeforeOverwriteRecheck);
        if (replaced is not null)
        {
            return await RefuseAsync(replaced, generated, wholeFile: true);
        }

        await output.WriteLineAsync($"Replaced {policy.Path}, refreshing the read-only tool list. Comments in the replaced file were not kept.");
        await WriteRestartReminderAsync();
        return 0;
    }

    private async Task<int> RefuseAsync(string why, GeneratedReadOnlyPolicy generated, bool wholeFile)
    {
        await error.WriteLineAsync($"{policy.Path}: refused — {why}. Nothing was written.");
        await error.WriteLineAsync(wholeFile
            ? "The generated configuration follows, for manual review:"
            : "The generated delegation section follows, for a manual merge:");
        await output.WriteAsync(wholeFile ? generated.Document : generated.DelegationFragment);
        return 1;
    }

    private async Task WriteRestartReminderAsync()
    {
        await output.WriteLineAsync("bOps.Api reads the policy only when it starts: restart bOps.Api to apply it, and configure it with the same Policy:FilePath.");
        await output.WriteLineAsync("Next: bops delegate profiles check, then bops delegate readiness.");
    }

    private async Task WritePolicyLineAsync()
    {
        var state = policy.State switch
        {
            PolicyLoadState.NoFile => "noFile",
            PolicyLoadState.Loaded => "loaded",
            _ => "loadFailed",
        };
        await output.WriteLineAsync($"Policy: {policy.Path} ({state})");
        if (policy.State == PolicyLoadState.LoadFailed && policy.LoadError is { } message)
        {
            await output.WriteLineAsync($"  {message}");
        }
    }
}
