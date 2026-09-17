// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace bOps.Packages.Web.Tests;

/// <summary>
/// A real loopback HTTP server for the attack tests ADR-0028 calls for (redirect chains, oversized
/// and compressed bodies, slow responses) — a recorded fixture cannot exercise the real socket path
/// <see cref="SsrfSafeConnectCallback"/> connects through. Built on Kestrel, the same server
/// <c>bOps.Api</c> itself runs: <see cref="System.Net.HttpListener"/> does not reliably dispatch
/// requests on Linux in this environment, discovered when the earlier HttpListener-based version
/// passed on Windows CI but returned a generic 404 for every request on Ubuntu CI.
/// </summary>
internal sealed class LocalHttpServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private LocalHttpServer(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    public static async Task<LocalHttpServer> StartAsync(RequestDelegate handler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        // WebApplication.Run(string?) — the blocking host-start overload — otherwise shadows the
        // IApplicationBuilder.Run(RequestDelegate) extension this test server actually needs.
        ((IApplicationBuilder)app).Run(handler);

        await app.StartAsync();
        var address = app.Urls.First();
        var port = new Uri(address).Port;
        return new LocalHttpServer(app, port);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
