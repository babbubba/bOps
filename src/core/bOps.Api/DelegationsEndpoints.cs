// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Claims;
using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.Api;

/// <summary>
/// Maps <c>/api/delegations</c> (ADR-0030 section 9): start, list, read, cancel, resume and reconcile a delegated run, and answer
/// the approval of its plan. Who may do what is the role, checked before anything runs: viewing needs the viewer role; starting,
/// cancelling and resuming the operator role; deciding a plan the approver role; reconciling a step whose outcome is not known the
/// administrator role. The decision is always that of the authenticated principal; the body never names who decides. The runtime
/// still refuses a decider that is an agent or the runtime itself.
/// </summary>
internal static class DelegationsEndpoints
{
    private const int MaximumIdempotencyKeyLength = 128;
    private const int DefaultLimit = 50;
    private const int MaximumLimit = 100;

    internal static void MapDelegationsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/delegations");

        group.MapPost("/", StartAsync).RequireAuthorization(ApiAuthorization.OperatorPolicy);
        group.MapGet("/", ListAsync).RequireAuthorization(ApiAuthorization.ViewerPolicy);
        group.MapGet("/{id:guid}", ReadAsync).RequireAuthorization(ApiAuthorization.ViewerPolicy);
        group.MapPost("/{id:guid}/cancel", CancelAsync).RequireAuthorization(ApiAuthorization.OperatorPolicy);
        group.MapPost("/{id:guid}/resume", ResumeAsync).RequireAuthorization(ApiAuthorization.OperatorPolicy);
        group.MapPost("/{id:guid}/reconcile", ReconcileAsync).RequireAuthorization(ApiAuthorization.AdministratorPolicy);
        group.MapGet("/approvals", (ApiPlanApprovalProvider approvals) => Results.Ok(approvals.ListPending()))
            .RequireAuthorization(ApiAuthorization.ApproverPolicy);
        group.MapPost("/{id:guid}/approval", RespondAsync).RequireAuthorization(ApiAuthorization.ApproverPolicy);
    }

    private static async Task<IResult> StartAsync(
        StartDelegationRequest request, DelegationLauncher launcher, ClaimsPrincipal principal, HttpContext http)
    {
        var key = http.Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (key is { Length: > MaximumIdempotencyKeyLength })
        {
            return Results.BadRequest(new { message = $"Idempotency-Key must not exceed {MaximumIdempotencyKeyLength} characters." });
        }

        var (delegation, error) = ToRequest(request);
        if (delegation is null)
        {
            return Results.BadRequest(new { message = error });
        }

        var id = await launcher.TryStartAsync(delegation, AgentsEndpoints.ApiActor(principal), string.IsNullOrWhiteSpace(key) ? null : key, http.RequestAborted);
        return id is null
            ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
            : Results.Accepted($"/api/delegations/{id}", new DelegationAcceptedResponse(id.Value));
    }

    private static async Task<IResult> ListAsync(string? status, int? limit, IDelegationStore store, DelegationLauncher launcher, ApiPlanApprovalProvider approvals, HttpContext http)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaximumLimit);
        IReadOnlyList<DelegationRun> runs;
        if (status is null)
        {
            runs = await store.ListRecentAsync(take, http.RequestAborted);
        }
        else if (Enum.TryParse<DelegationStatus>(status, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            runs = [.. (await store.ListByStatusAsync(parsed, http.RequestAborted)).Take(take)];
        }
        else
        {
            return Results.BadRequest(new { message = $"Unknown status '{status}'." });
        }

        return Results.Ok(runs.Select(run => DelegationViews.From(run, launcher.IsRunning(run.Id), approvals.IsPending(run.Id))));
    }

    private static async Task<IResult> ReadAsync(Guid id, IDelegationStore store, DelegationLauncher launcher, ApiPlanApprovalProvider approvals, HttpContext http)
    {
        var run = await store.LoadAsync(id, http.RequestAborted);
        return run is null
            ? Results.NotFound(new { message = $"No delegation run with id '{id}'." })
            : Results.Ok(DelegationViews.From(run, launcher.IsRunning(id), approvals.IsPending(id)));
    }

    private static async Task<IResult> CancelAsync(
        Guid id, IDelegationStore store, DelegationLauncher launcher, DelegationRunner runner, ApiPlanApprovalProvider approvals, ClaimsPrincipal principal, HttpContext http)
    {
        var actor = AgentsEndpoints.ApiActor(principal);
        if (await store.LoadAsync(id, http.RequestAborted) is null)
        {
            return Results.NotFound(new { message = $"No delegation run with id '{id}'." });
        }

        try
        {
            // A run this host is executing is stopped where it is; one a crash left Running has nothing executing it, and is closed in the store.
            if (!await launcher.TryCancelRunningAsync(id, actor, http.RequestAborted))
            {
                await runner.CancelAsync(id, actor, http.RequestAborted);
            }
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { message = ex.Message });
        }

        var run = await store.LoadAsync(id, http.RequestAborted);
        return run is null ? Results.NotFound() : Results.Ok(DelegationViews.From(run, launcher.IsRunning(id), approvals.IsPending(id)));
    }

    private static async Task<IResult> ResumeAsync(Guid id, DelegationLauncher launcher, ClaimsPrincipal principal, HttpContext http)
    {
        var result = await launcher.TryResumeAsync(id, AgentsEndpoints.ApiActor(principal), http.RequestAborted);
        return result switch
        {
            DelegationResumeResult.Started => Results.Accepted($"/api/delegations/{id}", new DelegationAcceptedResponse(id)),
            DelegationResumeResult.NotFound => Results.NotFound(new { message = $"No delegation run with id '{id}'." }),
            DelegationResumeResult.AlreadyRunning => Results.Conflict(new { message = "This host is already executing the run." }),
            DelegationResumeResult.NotResumable => Results.Conflict(new { message = "Only a run that is running or waiting for its plan's approval can be resumed." }),
            _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
        };
    }

    private static async Task<IResult> ReconcileAsync(
        Guid id, ReconcileDelegationRequest request, DelegationRunner runner, DelegationLauncher launcher, ApiPlanApprovalProvider approvals, ClaimsPrincipal principal, HttpContext http)
    {
        ReconciliationAction action;
        if (string.Equals(request.Decision, "accept", StringComparison.OrdinalIgnoreCase))
        {
            action = ReconciliationAction.OperatorAcceptedDone;
        }
        else if (string.Equals(request.Decision, "abandon", StringComparison.OrdinalIgnoreCase))
        {
            action = ReconciliationAction.OperatorAbandoned;
        }
        else
        {
            return Results.BadRequest(new { message = "'decision' is 'accept' (the unsettled steps are done) or 'abandon' (end the run)." });
        }

        try
        {
            var run = await runner.ReconcileAsync(id, action, AgentsEndpoints.ApiActor(principal), request.Note, http.RequestAborted);
            return Results.Ok(DelegationViews.From(run, launcher.IsRunning(id), approvals.IsPending(id)));
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message.StartsWith("No delegation run", StringComparison.Ordinal)
                ? Results.NotFound(new { message = ex.Message })
                : Results.Conflict(new { message = ex.Message });
        }
    }

    private static IResult RespondAsync(Guid id, RespondToPlanApprovalRequest request, ApiPlanApprovalProvider approvals, ClaimsPrincipal principal)
    {
        if (string.IsNullOrWhiteSpace(request.PlanHash))
        {
            return Results.BadRequest(new { message = "'planHash' is required: say which plan you are deciding." });
        }

        return approvals.TryRespond(id, request.PlanHash, request.Approved, request.Note, AgentsEndpoints.ApiActor(principal)) switch
        {
            PlanApprovalResponse.Delivered => Results.NoContent(),
            PlanApprovalResponse.HashMismatch => Results.Conflict(new { message = "The plan waiting for a decision is not the one whose hash was given. Nothing was decided." }),
            _ => Results.NotFound(new { message = "No plan of this run is waiting for a decision." }),
        };
    }

    private static (DelegationRequest? Request, string? Error) ToRequest(StartDelegationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Objective))
        {
            return (null, "'objective' is required.");
        }

        if (request.MaxSteps is < 1 || request.MaxTokens is < 1)
        {
            return (null, "'maxSteps' and 'maxTokens' are whole numbers of at least 1.");
        }

        DelegationRemediation? remediation = null;
        if (request.Remediation is { } change)
        {
            if (string.IsNullOrWhiteSpace(change.SkillId) || string.IsNullOrWhiteSpace(change.CapabilityName)
                || string.IsNullOrWhiteSpace(change.Target) || string.IsNullOrWhiteSpace(change.Environment))
            {
                return (null, "A change needs 'skillId', 'capabilityName', 'target' and 'environment'.");
            }

            var blast = BlastRadius.Single;
            if (change.BlastRadius is { } blastText
                && !(Enum.TryParse(blastText, ignoreCase: true, out blast) && Enum.IsDefined(blast) && !int.TryParse(blastText, out _)))
            {
                return (null, $"'blastRadius' is single, multiple or fleet, not '{blastText}'.");
            }

            var input = change.Input is null ? ToolArguments.Empty : ToolArguments.FromJson(change.Input);
            remediation = new DelegationRemediation(
                change.SkillId, change.CapabilityName, new CapabilityRequest(input, change.Target, change.Environment, blast, change.DryRun));
        }

        var authority = request.MaxSteps is null && request.MaxTokens is null
            ? null
            : new DelegationAuthorityRequest(MaxSteps: request.MaxSteps, MaxTokens: request.MaxTokens);
        return (new DelegationRequest(request.Objective, authority, remediation), null);
    }
}
