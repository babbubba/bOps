// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Claims;
using System.Text.Json;
using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

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
        group.MapGet("/readiness", Readiness).RequireAuthorization(ApiAuthorization.ViewerPolicy);
        group.MapGet("/{id:guid}", ReadAsync).RequireAuthorization(ApiAuthorization.ViewerPolicy);
        group.MapPost("/{id:guid}/cancel", CancelAsync).RequireAuthorization(ApiAuthorization.OperatorPolicy);
        group.MapPost("/{id:guid}/resume", ResumeAsync).RequireAuthorization(ApiAuthorization.OperatorPolicy);
        group.MapPost("/{id:guid}/reconcile", ReconcileAsync).RequireAuthorization(ApiAuthorization.AdministratorPolicy);
        group.MapGet("/approvals", ListApprovalsAsync).RequireAuthorization(ApiAuthorization.ApproverPolicy);
        group.MapPost("/{id:guid}/approval", RespondAsync).RequireAuthorization(ApiAuthorization.ApproverPolicy);
    }

    /// <summary>
    /// <c>GET /api/delegations/readiness?remediation=false|true</c> (ADR-0044 sections 5 and 6): the runtime's readiness evaluator
    /// for that request shape over this host's profiles, policy load state and available tools. Reads configuration, executes and
    /// writes nothing, so it is not audited. Only <c>remediation</c> is read; it defaults to <c>false</c>.
    /// </summary>
    private static IResult Readiness(
        HttpContext http, IRoleProfileSource profiles, LoadedPolicy policy, IToolRegistry tools, TimeProvider timeProvider)
    {
        http.Response.Headers.CacheControl = "no-store";
        var values = http.Request.Query["remediation"];
        bool remediation;
        if (values.Count == 0)
        {
            remediation = false;
        }
        else if (values.Count == 1 && string.Equals(values[0], "true", StringComparison.OrdinalIgnoreCase))
        {
            remediation = true;
        }
        else if (values.Count == 1 && string.Equals(values[0], "false", StringComparison.OrdinalIgnoreCase))
        {
            remediation = false;
        }
        else
        {
            return Results.BadRequest(new { message = "'remediation' is true or false." });
        }

        var readiness = DelegationReadinessEvaluator.Evaluate(profiles, policy.State, tools.GetAvailableManifests(), remediation, timeProvider.GetUtcNow());
        return Results.Ok(DelegationReadinessView.From(readiness));
    }

    /// <summary>
    /// The plans waiting for a person, each with the persisted limitation metadata of its run's Discovery and Diagnostic roles
    /// (ADR-0044 section 16), joined by delegation id. A run that cannot be loaded is shown as "limitations unavailable", never as
    /// "no limitations".
    /// </summary>
    private static async Task<IResult> ListApprovalsAsync(ApiPlanApprovalProvider approvals, IDelegationStore store, ILoggerFactory loggers, HttpContext http)
    {
        var pending = approvals.ListPending();
        var views = new List<PendingPlanApproval>(pending.Count);
        foreach (var plan in pending)
        {
            DelegationRun? run;
            try
            {
                run = await store.LoadAsync(plan.DelegationId, http.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                loggers.CreateLogger("bOps.Api.Delegations").LogWarning(ex, "Delegation {DelegationId}: its run could not be loaded for the approval view", plan.DelegationId);
                run = null;
            }

            views.Add(DelegationViews.WithLimitations(plan, run));
        }

        return Results.Ok(views);
    }

    private static readonly JsonDocumentOptions StrictBody = new() { AllowDuplicateProperties = false };

    private static async Task<IResult> StartAsync(
        HttpContext http, DelegationLauncher launcher, ISkillRegistry skills, IOptions<JsonOptions> json, ClaimsPrincipal principal)
    {
        var key = http.Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (key is { Length: > MaximumIdempotencyKeyLength })
        {
            return Results.BadRequest(new { message = $"Idempotency-Key must not exceed {MaximumIdempotencyKeyLength} characters." });
        }

        if (!http.Request.HasJsonContentType())
        {
            return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        // ADR-0044 section 9.3 (review N-8): the only change to binding is that a property repeated anywhere in the body, input
        // included, is refused instead of first- or last-wins. Casing, unknown members and numbers bind exactly as before,
        // through the same serializer options the endpoint used.
        StartDelegationRequest? request;
        try
        {
            using var body = await JsonDocument.ParseAsync(http.Request.Body, StrictBody, http.RequestAborted);
            request = body.RootElement.Deserialize<StartDelegationRequest>(json.Value.SerializerOptions);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { message = "The request body is not strict JSON: it is malformed, or a property is repeated.", code = "malformed_json" });
        }

        if (request is null)
        {
            return Results.BadRequest(new { message = "The request body must be a JSON object.", code = "malformed_json" });
        }

        var (delegation, error) = ToRequest(request);
        if (delegation is null)
        {
            return Results.BadRequest(new { message = error });
        }

        // ADR-0044 section 9.2: an unknown Capability or input that does not conform to its schema is refused here, before a run
        // exists and before any model call. The runner checks again, and the preparation path once more before Capability code.
        if (delegation.Remediation is { } change
            && CapabilityRequestValidator.Check(skills, change.SkillId, change.CapabilityName, change.Request.Input) is { } refused)
        {
            return refused.Code == CapabilityRequestValidator.UnknownCapability
                ? Results.BadRequest(new { message = refused.Message, code = refused.Code })
                : Results.BadRequest(new { message = refused.Message, code = refused.Code, parameter = refused.Parameter });
        }

        Guid? id;
        try
        {
            id = await launcher.TryStartAsync(delegation, AgentsEndpoints.ApiActor(principal),
                string.IsNullOrWhiteSpace(key) ? null : key, http.RequestAborted);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ProviderNotSupportedException)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
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
        DelegationResumeResult result;
        try
        {
            result = await launcher.TryResumeAsync(id, AgentsEndpoints.ApiActor(principal), http.RequestAborted);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ProviderNotSupportedException)
        {
            return Results.Conflict(new { message = ex.Message, code = "pinned_provider_unavailable" });
        }
        return result switch
        {
            DelegationResumeResult.Started => Results.Accepted($"/api/delegations/{id}",
                new DelegationAcceptedResponse(id)),
            DelegationResumeResult.StartedLegacyMigrated => Results.Accepted($"/api/delegations/{id}",
                new DelegationAcceptedResponse(id, true)),
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
