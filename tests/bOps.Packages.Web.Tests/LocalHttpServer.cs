// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;

namespace bOps.Packages.Web.Tests;

/// <summary>
/// A real loopback HTTP server for the attack tests ADR-0028 calls for (redirect chains, oversized
/// and compressed bodies, slow responses) — a recorded fixture cannot exercise the real socket path
/// <see cref="SsrfSafeConnectCallback"/> connects through.
/// </summary>
internal sealed class LocalHttpServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Func<HttpListenerContext, Task> _handler;
    private readonly CancellationTokenSource _cts = new();

    public LocalHttpServer(Func<HttpListenerContext, Task> handler)
    {
        _handler = handler;
        Port = GetFreePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public Uri BaseUri => new($"http://127.0.0.1:{Port}/");

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await _handler(context);
                }
                catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException)
                {
                    // Client disconnected or the server is shutting down mid-response; nothing to assert on here.
                }
                finally
                {
                    context.Response.OutputStream.Close();
                }
            });
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        _cts.Dispose();
    }
}
