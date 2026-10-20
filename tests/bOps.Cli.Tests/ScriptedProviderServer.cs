// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace bOps.Cli.Tests;

/// <summary>
/// A scripted OpenAI-compatible endpoint on the loopback interface of this machine, so the real <c>bops</c> process can be run end to end
/// with its real provider adapter and nothing live: it answers each request from a queue, keeps every request body for assertion, and
/// answers anything unscripted with an HTTP 500 that fails the task visibly. It is a test double of a model, not a model.
/// </summary>
internal sealed class ScriptedProviderServer : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly Queue<Func<JsonNode, string>> script;
    private readonly Task loop;
    private readonly object gate = new();

    internal ScriptedProviderServer(IEnumerable<Func<JsonNode, string>> script)
    {
        this.script = new Queue<Func<JsonNode, string>>(script);
        int port;
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)probe.LocalEndPoint!).Port;
        }

        BaseUrl = $"http://127.0.0.1:{port}/v1";
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        loop = Task.Run(ServeAsync);
    }

    internal string BaseUrl { get; }

    internal List<JsonNode> Requests { get; } = [];

    /// <summary>The name a tool has on the wire in this request, found by what it is called (the wire alias of a dotted name is the adapter's business).</summary>
    internal static string? WireNameContaining(JsonNode request, string fragment) =>
        (request["tools"] as JsonArray)?.Select(tool => tool?["function"]?["name"]?.GetValue<string>())
            .FirstOrDefault(name => name is not null && name.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    internal static string Text(string content) => Completion(new JsonObject { ["role"] = "assistant", ["content"] = content }, "stop");

    internal static string ToolCall(string id, string wireName, JsonObject arguments) => Completion(
        new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = null,
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = wireName, ["arguments"] = arguments.ToJsonString() },
            }),
        },
        "tool_calls");

    private static string Completion(JsonObject message, string finishReason) => new JsonObject
    {
        ["model"] = "scripted",
        ["choices"] = new JsonArray(new JsonObject { ["message"] = message, ["finish_reason"] = finishReason }),
        ["usage"] = new JsonObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 10 },
    }.ToJsonString();

    private async Task ServeAsync()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = JsonNode.Parse(await reader.ReadToEndAsync())!;
            Func<JsonNode, string>? next;
            lock (gate)
            {
                Requests.Add(body);
                next = script.Count > 0 ? script.Dequeue() : null;
            }

            var status = next is null ? 500 : 200;
            var payload = Encoding.UTF8.GetBytes(next is null ? "{\"error\":{\"message\":\"unscripted request\"}}" : next(body));
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload);
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        listener.Close();
        try
        {
            loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The listener was closed under the loop; nothing else to wait for.
        }
    }
}
