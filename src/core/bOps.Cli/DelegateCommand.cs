// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.Cli;

/// <summary>What <c>bops delegate</c> was asked to do.</summary>
internal enum DelegateAction
{
    Start,
    Status,
    Resume,
    Cancel,
    Reconcile,
}

/// <summary>A parsed <c>bops delegate</c> command line.</summary>
internal sealed record DelegateInvocation
{
    public required DelegateAction Action { get; init; }

    /// <summary>The run to act on, for every action but <see cref="DelegateAction.Start"/>.</summary>
    public Guid? RunId { get; init; }

    /// <summary>What to start, for <see cref="DelegateAction.Start"/>.</summary>
    public DelegationRequest? Request { get; init; }

    public string? IdempotencyKey { get; init; }

    /// <summary>The operator's decision, for <see cref="DelegateAction.Reconcile"/>.</summary>
    public ReconciliationAction? Decision { get; init; }

    public string? Note { get; init; }
}

/// <summary>Reads the arguments that follow <c>bops delegate</c>. Returns either the invocation or a message saying what is wrong with them.</summary>
internal static class DelegateArguments
{
    public const string Usage = """
        Usage: bops delegate "<objective>" [--skill <id> --capability <name> --target <label> --environment <label>
                                            [--blast-radius single|multiple|fleet] [--input <json-object>] [--dry-run]]
                                           [--max-steps <n>] [--max-tokens <n>] [--idempotency-key <key>]
               bops delegate status <run-id>
               bops delegate resume <run-id>
               bops delegate cancel <run-id>
               bops delegate reconcile <run-id> --accept|--abandon [--note <text>]
        Without --skill and its companions the run only diagnoses. Exit codes: 0 completed or diagnosed, 1 failed or a usage error,
        2 denied or blocked by policy, 3 rejected or abandoned by an operator, 4 requires reconciliation, 5 budget or deadline
        exceeded, 6 verification did not confirm, 10 not finished, 130 cancelled.
        """;

    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--skill", "--capability", "--target", "--environment", "--blast-radius", "--input", "--max-steps", "--max-tokens",
        "--idempotency-key", "--note",
    };

    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "--dry-run", "--accept", "--abandon" };

    public static (DelegateInvocation? Invocation, string? Error) Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            return (null, "Say what to delegate, or one of: status, resume, cancel, reconcile.");
        }

        var verb = args[0].ToLowerInvariant();
        return verb switch
        {
            "status" => ParseRunCommand(DelegateAction.Status, args),
            "resume" => ParseRunCommand(DelegateAction.Resume, args),
            "cancel" => ParseRunCommand(DelegateAction.Cancel, args),
            "reconcile" => ParseReconcile(args),
            _ => ParseStart(args),
        };
    }

    private static (DelegateInvocation?, string?) ParseRunCommand(DelegateAction action, string[] args)
    {
        if (args.Length != 2 || !Guid.TryParse(args[1], out var id) || id == Guid.Empty)
        {
            return (null, $"'{args[0]}' takes one run id.");
        }

        return (new DelegateInvocation { Action = action, RunId = id }, null);
    }

    private static (DelegateInvocation?, string?) ParseReconcile(string[] args)
    {
        if (args.Length < 3 || !Guid.TryParse(args[1], out var id) || id == Guid.Empty)
        {
            return (null, "'reconcile' takes a run id and --accept or --abandon.");
        }

        var (options, positional, error) = ReadOptions(args[2..]);
        if (error is not null || positional.Count > 0)
        {
            return (null, error ?? $"Unexpected '{positional[0]}'.");
        }

        var accept = options.ContainsKey("--accept");
        var abandon = options.ContainsKey("--abandon");
        if (accept == abandon)
        {
            return (null, "Say exactly one of --accept (the unsettled steps are done) and --abandon (end the run).");
        }

        return (
            new DelegateInvocation
            {
                Action = DelegateAction.Reconcile,
                RunId = id,
                Decision = accept ? ReconciliationAction.OperatorAcceptedDone : ReconciliationAction.OperatorAbandoned,
                Note = options.GetValueOrDefault("--note"),
            },
            null);
    }

    private static (DelegateInvocation?, string?) ParseStart(string[] args)
    {
        var (options, positional, error) = ReadOptions(args);
        if (error is not null)
        {
            return (null, error);
        }

        if (positional.Count == 0)
        {
            return (null, "Say what to delegate.");
        }

        if (options.ContainsKey("--accept") || options.ContainsKey("--abandon") || options.ContainsKey("--note"))
        {
            return (null, "--accept, --abandon and --note belong to 'reconcile'.");
        }

        var skill = options.GetValueOrDefault("--skill");
        var capability = options.GetValueOrDefault("--capability");
        var target = options.GetValueOrDefault("--target");
        var environment = options.GetValueOrDefault("--environment");
        var wantsChange = skill is not null || capability is not null || target is not null || environment is not null
            || options.ContainsKey("--input") || options.ContainsKey("--blast-radius") || options.ContainsKey("--dry-run");
        DelegationRemediation? remediation = null;
        if (wantsChange)
        {
            if (skill is null || capability is null || target is null || environment is null)
            {
                return (null, "A change needs --skill, --capability, --target and --environment together.");
            }

            var blast = BlastRadius.Single;
            if (options.GetValueOrDefault("--blast-radius") is { } blastText
                && !(Enum.TryParse(blastText, ignoreCase: true, out blast) && Enum.IsDefined(blast)))
            {
                return (null, $"--blast-radius is single, multiple or fleet, not '{blastText}'.");
            }

            ToolArguments input;
            try
            {
                input = options.GetValueOrDefault("--input") is { } json
                    ? ToolArguments.FromJson(JsonNode.Parse(json) as JsonObject ?? throw new JsonException("not an object"))
                    : ToolArguments.Empty;
            }
            catch (JsonException)
            {
                return (null, "--input must be a JSON object.");
            }

            remediation = new DelegationRemediation(
                skill, capability, new CapabilityRequest(input, target, environment, blast, options.ContainsKey("--dry-run")));
        }

        if (!TryCount(options, "--max-steps", out var maxSteps) || !TryCount(options, "--max-tokens", out var maxTokens))
        {
            return (null, "--max-steps and --max-tokens are whole numbers of at least 1.");
        }

        var authority = maxSteps is null && maxTokens is null ? null : new DelegationAuthorityRequest(MaxSteps: maxSteps, MaxTokens: maxTokens);
        return (
            new DelegateInvocation
            {
                Action = DelegateAction.Start,
                Request = new DelegationRequest(string.Join(' ', positional), authority, remediation),
                IdempotencyKey = options.GetValueOrDefault("--idempotency-key"),
            },
            null);
    }

    private static bool TryCount(Dictionary<string, string> options, string name, out int? value)
    {
        value = null;
        if (!options.TryGetValue(name, out var text))
        {
            return true;
        }

        if (!int.TryParse(text, out var parsed) || parsed < 1)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static (Dictionary<string, string> Options, List<string> Positional, string? Error) ReadOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
            }
            else if (Flags.Contains(arg))
            {
                options[arg] = string.Empty;
            }
            else if (ValueOptions.Contains(arg))
            {
                if (i + 1 >= args.Length)
                {
                    return (options, positional, $"{arg} needs a value.");
                }

                options[arg] = args[++i];
            }
            else
            {
                return (options, positional, $"Unknown option '{arg}'.");
            }
        }

        return (options, positional, null);
    }
}

/// <summary>
/// <c>bops delegate</c> (ADR-0030 section 9): starts a delegated run, or reads, resumes, cancels or reconciles a stored one, and
/// says how it ended in words and in the process exit code. It owns no policy: the runner decides everything, and this only
/// carries the operator's request in and the outcome out.
/// </summary>
internal sealed class DelegateCommand(DelegationRunner runner, IDelegationStore store, ActorIdentity actor, TextWriter output, TextWriter error)
{
    /// <summary>The exit code for how a run stands. Documented in <see cref="DelegateArguments.Usage"/>.</summary>
    internal static int ExitCodeFor(DelegationStatus status) => status switch
    {
        DelegationStatus.Completed or DelegationStatus.DiagnosisCompleted => 0,
        DelegationStatus.Denied or DelegationStatus.PolicyBlocked => 2,
        DelegationStatus.Rejected or DelegationStatus.Abandoned => 3,
        DelegationStatus.RequiresReconciliation => 4,
        DelegationStatus.BudgetExceeded or DelegationStatus.DeadlineExceeded => 5,
        DelegationStatus.VerificationFailed => 6,
        DelegationStatus.Cancelled => 130,
        DelegationStatus.Running or DelegationStatus.AwaitingApproval => 10,
        _ => 1,
    };

    public async Task<int> RunAsync(DelegateInvocation invocation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        try
        {
            DelegationRun run;
            switch (invocation.Action)
            {
                case DelegateAction.Start:
                    run = await runner.StartAsync(invocation.Request!, actor, idempotencyKey: invocation.IdempotencyKey, ct: ct);
                    break;
                case DelegateAction.Status:
                    run = await store.LoadAsync(invocation.RunId!.Value, ct) ?? throw new InvalidOperationException($"No delegation run {invocation.RunId} is stored.");
                    break;
                case DelegateAction.Resume:
                    run = await runner.ResumeAsync(invocation.RunId!.Value, actor, ct);
                    break;
                case DelegateAction.Cancel:
                    run = await runner.CancelAsync(invocation.RunId!.Value, actor, ct);
                    break;
                default:
                    run = await runner.ReconcileAsync(invocation.RunId!.Value, invocation.Decision!.Value, actor, invocation.Note, ct);
                    break;
            }

            await PrintAsync(run);
            return ExitCodeFor(run.Status);
        }
        catch (InvalidOperationException ex)
        {
            await error.WriteLineAsync(ex.Message);
            return 1;
        }
    }

    private async Task PrintAsync(DelegationRun run)
    {
        await output.WriteLineAsync($"Delegation {run.Id}: {run.Status}");
        await output.WriteLineAsync($"  {Explain(run)}");
        await output.WriteLineAsync($"  Objective: {run.Objective}");
        foreach (var role in run.Roles)
        {
            await output.WriteLineAsync(
                $"  {role.Agent.Role,-12} {role.Status,-9} {role.Consumed.Steps} steps, {role.Consumed.Tokens} tokens"
                + (role.Verification is { } verification ? $", verification {verification.Status}" : string.Empty));
        }

        if (run.PlanHash is not null)
        {
            await output.WriteLineAsync(
                $"  Plan {run.PlanHash}" + (run.Approval is { } approval ? $", approved by {approval.Approver.Id}" : ", not approved"));
        }

        foreach (var entry in run.Journal)
        {
            var how = entry.Reconciliation is { } settled ? $"{settled.Action}" : entry.Outcome is { } outcome ? $"{outcome.Kind}" : "outcome not known";
            await output.WriteLineAsync($"  Step {entry.StepIndex} {entry.ToolName}: {how}");
        }

        if (run.Denial is { } denial)
        {
            await output.WriteLineAsync($"  Refused on {denial.Dimension}: {denial.Reason}");
        }
        else if (run.ErrorMessage is not null)
        {
            await output.WriteLineAsync($"  {run.ErrorMessage}");
        }

        var next = NextStep(run);
        if (next is not null)
        {
            await output.WriteLineAsync($"  Next: {next}");
        }
    }

    private static string Explain(DelegationRun run) => run.Status switch
    {
        DelegationStatus.Completed => "The approved plan ran and independent verification confirmed it.",
        DelegationStatus.DiagnosisCompleted => "The diagnosis finished; no change was made.",
        DelegationStatus.Denied => "Delegation was refused: no authority could be granted for a role. Nothing ran.",
        DelegationStatus.PolicyBlocked => "Policy or the granted authority forbade a step of the approved plan.",
        DelegationStatus.Rejected => "The plan was rejected. Nothing was changed.",
        DelegationStatus.Abandoned => "An operator abandoned the run.",
        DelegationStatus.RequiresReconciliation => "A step may or may not have taken effect and could not be settled. It is never retried; an operator decides.",
        DelegationStatus.BudgetExceeded => "The step or token budget was exhausted.",
        DelegationStatus.DeadlineExceeded => "The deadline passed.",
        DelegationStatus.VerificationFailed => "Verification did not confirm the outcome. The change is not confirmed.",
        DelegationStatus.Cancelled => "The run was cancelled.",
        DelegationStatus.Running => "The run has not finished: a process may be executing it, or it was interrupted.",
        DelegationStatus.AwaitingApproval => "The run waits for a human decision on its plan.",
        _ => "The run failed.",
    };

    private static string? NextStep(DelegationRun run) => run.Status switch
    {
        DelegationStatus.Running => $"bops delegate resume {run.Id} (if no process is running it), or bops delegate cancel {run.Id}",
        DelegationStatus.AwaitingApproval => $"bops delegate resume {run.Id}",
        DelegationStatus.RequiresReconciliation => $"bops delegate reconcile {run.Id} --accept (the steps above are done) or --abandon",
        _ => null,
    };
}
