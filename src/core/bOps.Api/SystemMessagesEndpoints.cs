// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Api;

/// <summary>One system message, as an operator reads it. <c>Metadata</c> is the bounded, secret-refusing object the runtime wrote.</summary>
internal sealed record SystemMessageView(
    Guid Id,
    DateTimeOffset TimestampUtc,
    string Node,
    string Source,
    string Severity,
    string Code,
    string Message,
    JsonObject Metadata,
    Guid? TaskId,
    string? ComponentType,
    string? ComponentId);

/// <summary>One page of <c>GET /api/system-messages</c>, newest first.</summary>
internal sealed record SystemMessagePageView(IReadOnlyList<SystemMessageView> Items, string? NextCursor);

/// <summary>
/// Maps <c>GET /api/system-messages</c> (ADR-0049 section 8): the read-only operational inbox. Viewer role. Filters
/// <c>fromUtc</c>, <c>toUtc</c> (inclusive), <c>severity</c> (exact), <c>contains</c> (case-insensitive) combine with AND; ordering is
/// <c>timestamp DESC, id DESC</c> and pagination is an opaque keyset <c>cursor</c> (never an offset), 50 per page by default, 200 at most.
/// Any invalid input is a 400 that names the parameter and never echoes the value.
/// </summary>
internal static class SystemMessagesEndpoints
{
    internal static void MapSystemMessagesEndpoints(this WebApplication app) =>
        app.MapGet("/api/system-messages", async (HttpContext http, ISystemMessageStore store, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                if (!TryParseQuery(http.Request.Query, out var query, out var problem))
                {
                    return Results.BadRequest(new { message = problem });
                }

                SystemMessagePage page;
                try
                {
                    page = await store.QueryAsync(query!, ct);
                }
                catch (ArgumentException)
                {
                    // Only a cursor the store cannot read survives the checks above.
                    return Results.BadRequest(new { message = "'cursor' is not a valid cursor." });
                }

                return Results.Ok(new SystemMessagePageView([.. page.Items.Select(ToView)], page.NextCursor));
            })
            .RequireAuthorization(ApiAuthorization.ViewerPolicy);

    internal static bool TryParseQuery(IQueryCollection parameters, out SystemMessageQuery? query, out string? problem)
    {
        query = null;
        problem = null;

        if (!TryDate(parameters, "fromUtc", out var from) || !TryDate(parameters, "toUtc", out var to))
        {
            problem = "'fromUtc' and 'toUtc' must be ISO 8601 date-times.";
            return false;
        }

        if (from is { } f && to is { } t && f > t)
        {
            problem = "'fromUtc' must not be later than 'toUtc'.";
            return false;
        }

        SystemMessageSeverity? severity = null;
        if (Single(parameters, "severity") is { Length: > 0 } severityText)
        {
            if (!Enum.TryParse<SystemMessageSeverity>(severityText, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed) || char.IsAsciiDigit(severityText[0]))
            {
                problem = "'severity' must be one of Information, Warning, Error, Critical.";
                return false;
            }

            severity = parsed;
        }

        var contains = Single(parameters, "contains");
        if (contains is { Length: 0 })
        {
            contains = null;
        }

        if (contains is { Length: > SystemMessageQuery.MaxTextLength })
        {
            problem = $"'contains' must be at most {SystemMessageQuery.MaxTextLength} characters.";
            return false;
        }

        var pageSize = SystemMessageQuery.DefaultPageSize;
        if (Single(parameters, "pageSize") is { Length: > 0 } pageSizeText
            && (!int.TryParse(pageSizeText, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize)
                || pageSize is < 1 or > SystemMessageQuery.MaxPageSize))
        {
            problem = $"'pageSize' must be an integer 1–{SystemMessageQuery.MaxPageSize}.";
            return false;
        }

        var cursor = Single(parameters, "cursor");
        query = new SystemMessageQuery
        {
            FromUtc = from,
            ToUtc = to,
            Severity = severity,
            Text = contains,
            PageSize = pageSize,
            Cursor = string.IsNullOrEmpty(cursor) ? null : cursor,
        };
        return true;
    }

    /// <summary>The first value of a scalar parameter, or <c>null</c> when it is absent.</summary>
    private static string? Single(IQueryCollection parameters, string name) =>
        parameters.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

    private static bool TryDate(IQueryCollection parameters, string name, out DateTimeOffset? value)
    {
        value = null;
        if (Single(parameters, name) is not { Length: > 0 } text)
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static SystemMessageView ToView(SystemMessage message) => new(
        message.Id,
        message.TimestampUtc,
        message.Node.Value,
        message.Source,
        message.Severity.ToString(),
        message.Code,
        message.Message,
        message.Metadata.ToJson(),
        message.TaskId,
        message.ComponentType?.ToString(),
        message.ComponentId);
}
