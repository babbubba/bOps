// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Extensions.AI;
using BopsChatRole = bOps.Abstractions.ChatRole;
using MafChatRole = Microsoft.Extensions.AI.ChatRole;

namespace bOps.AgentFramework;

/// <summary>Strict, lossless mapping at the bOps/MAF conversation boundary.</summary>
internal static class MafChatTurnMapper
{
    /// <summary>
    /// AF-TOOLROUTE-01: the reserved <see cref="AIContent.AdditionalProperties"/> keys that carry a call's adapter rejection across the
    /// boundary. Without them a rejected raw name (for example <c>system_crashes</c>, never offered) came back as an ordinary
    /// canonical call: provider adapters then sent it verbatim and let it collide with the offered tool's alias, so the same tool
    /// was published under a different wire name from one request to the next.
    /// </summary>
    internal const string ToolNameErrorProperty = "bops.toolNameError";

    internal const string ArgumentsErrorProperty = "bops.argumentsError";

    internal static FunctionCallContent ToMafFunctionCall(ModelToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var content = new FunctionCallContent(
            call.Id,
            call.ToolName,
            JsonSerializer.Deserialize<Dictionary<string, object?>>(call.Arguments.ToJson())
                ?? new Dictionary<string, object?>(StringComparer.Ordinal));
        if (call.ToolNameError is not null || call.ArgumentsError is not null)
        {
            content.AdditionalProperties = [];
            if (call.ToolNameError is { } nameError)
            {
                content.AdditionalProperties[ToolNameErrorProperty] = nameError;
            }

            if (call.ArgumentsError is { } argumentsError)
            {
                content.AdditionalProperties[ArgumentsErrorProperty] = argumentsError;
            }
        }

        return content;
    }

    internal static IReadOnlyList<ChatMessage> ToMaf(IEnumerable<ChatTurn> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);
        var messages = new List<ChatMessage>();
        foreach (var turn in turns)
        {
            ArgumentNullException.ThrowIfNull(turn);
            ValidateBopsTurn(turn);
            var contents = new List<AIContent>();
            if (turn.Content is not null)
            {
                contents.Add(new TextContent(turn.Content));
            }

            if (turn.ToolCalls is { Count: > 0 })
            {
                contents.AddRange(turn.ToolCalls.Select(ToMafFunctionCall));
            }

            if (turn.Role == BopsChatRole.Tool)
            {
                contents.Add(new FunctionResultContent(turn.ToolCallId!, turn.Content));
                contents.RemoveAll(content => content is TextContent);
            }

            messages.Add(new ChatMessage(ToMafRole(turn.Role), contents));
        }

        return messages;
    }

    internal static IReadOnlyList<ChatTurn> FromMaf(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages.SelectMany(FromMafMessage).ToArray();
    }

    private static IEnumerable<ChatTurn> FromMafMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var role = FromMafRole(message.Role);
        var textItems = message.Contents.OfType<TextContent>().Select(content => content.Text).ToArray();
        var unsupported = message.Contents.Where(content => content is not TextContent
            && content is not FunctionCallContent
            && content is not FunctionResultContent).ToArray();
        if (unsupported.Length > 0)
        {
            throw new NotSupportedException(
                $"MAF content type '{unsupported[0].GetType().Name}' is not representable by the bOps model contract.");
        }

        var text = textItems.Length == 0 ? null : string.Join('\n', textItems);
        var functionCalls = message.Contents.OfType<FunctionCallContent>().ToArray();
        var functionResults = message.Contents.OfType<FunctionResultContent>().ToArray();

        if (role == BopsChatRole.Assistant)
        {
            if (functionResults.Length > 0)
            {
                throw new NotSupportedException("An assistant MAF message cannot be mapped with function-result content.");
            }

            yield return new ChatTurn
            {
                Role = role,
                Content = text,
                ToolCalls = functionCalls.Length == 0 ? null : functionCalls.Select(FromMafFunctionCall).ToArray(),
            };
            yield break;
        }

        if (role == BopsChatRole.Tool)
        {
            if (functionCalls.Length > 0 || functionResults.Length == 0 || text is not null)
            {
                throw new NotSupportedException(
                    "A MAF tool message must contain one or more function results and no unrelated content.");
            }

            foreach (var result in functionResults)
            {
                yield return new ChatTurn
                {
                    Role = BopsChatRole.Tool,
                    ToolCallId = result.CallId,
                    Content = SerializeFunctionResult(result.Result),
                };
            }

            yield break;
        }

        if (functionCalls.Length > 0 || functionResults.Length > 0)
        {
            throw new NotSupportedException("Function content is supported only on assistant and tool MAF messages.");
        }

        yield return new ChatTurn { Role = role, Content = text };
    }

    private static void ValidateBopsTurn(ChatTurn turn)
    {
        if (turn.Role == BopsChatRole.Assistant)
        {
            if (turn.ToolCallId is not null)
            {
                throw new NotSupportedException("An assistant bOps turn cannot carry a tool-result call id.");
            }
            return;
        }

        if (turn.Role == BopsChatRole.Tool)
        {
            if (string.IsNullOrEmpty(turn.ToolCallId) || turn.ToolCalls is { Count: > 0 })
            {
                throw new NotSupportedException("A bOps tool turn must carry one result call id and no tool calls.");
            }
            return;
        }

        if (turn.ToolCallId is not null || turn.ToolCalls is { Count: > 0 })
        {
            throw new NotSupportedException("Only assistant and tool bOps turns may carry tool protocol content.");
        }
    }

    private static ModelToolCall FromMafFunctionCall(FunctionCallContent call)
    {
        var node = JsonSerializer.SerializeToNode(call.Arguments);
        if (node is not JsonObject arguments)
        {
            throw new NotSupportedException("MAF function-call arguments must map to a JSON object.");
        }

        return new ModelToolCall(call.CallId, call.Name, ToolArguments.FromJson(arguments))
        {
            ToolNameError = RejectionOf(call, ToolNameErrorProperty),
            ArgumentsError = RejectionOf(call, ArgumentsErrorProperty),
        };
    }

    private static string? RejectionOf(FunctionCallContent call, string key) =>
        call.AdditionalProperties?.TryGetValue(key, out var value) == true
            ? value switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => throw new NotSupportedException($"MAF function-call property '{key}' must be a string."),
            }
            : null;

    private static string? SerializeFunctionResult(object? result) => result switch
    {
        null => null,
        string text => text,
        _ => JsonSerializer.Serialize(result),
    };

    private static MafChatRole ToMafRole(BopsChatRole role) => role switch
    {
        BopsChatRole.System => MafChatRole.System,
        BopsChatRole.User => MafChatRole.User,
        BopsChatRole.Assistant => MafChatRole.Assistant,
        BopsChatRole.Tool => MafChatRole.Tool,
        _ => throw new NotSupportedException($"bOps role '{role}' is not representable by MAF."),
    };

    private static BopsChatRole FromMafRole(MafChatRole role)
    {
        if (role == MafChatRole.System) return BopsChatRole.System;
        if (role == MafChatRole.User) return BopsChatRole.User;
        if (role == MafChatRole.Assistant) return BopsChatRole.Assistant;
        if (role == MafChatRole.Tool) return BopsChatRole.Tool;
        throw new NotSupportedException($"MAF role '{role}' is not representable by the bOps model contract.");
    }
}
