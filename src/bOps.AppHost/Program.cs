IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// V0.5 (agentic/06-decisions.md, D-002): this AppHost orchestrates dependencies and test targets
// for local development ONLY — bOps itself is never containerized for its real work (a container
// cannot restart the host's systemd services). It never hosts bOps in production.
//
// This container gives a real Linux target for bOps.Packages.System.Linux.Tests and
// bOps.Packages.Filesystem.Tests when developing on a non-Linux machine — mirroring what CI's
// ubuntu-latest matrix entry already provides natively (agentic/04-testing-rules.md: "From V0.5
// the Aspire AppHost provides these targets, and the same composition is used locally and in
// CI"). Ollama and llama.cpp orchestration is explicitly out of scope for this version
// (agentic/00-project-spec.md: build the current roadmap version, not the next one) — nothing in
// this codebase yet needs a live model target for a test to pass.
builder.AddContainer("linux-test-target", "mcr.microsoft.com/dotnet/sdk")
    .WithImageTag("10.0")
    .WithBindMount("..", "/workspace", isReadOnly: false)
    .WithEntrypoint("tail")
    .WithArgs("-f", "/dev/null");

// V0.9 (D-002 explicitly names "from V0.9, the API and UI"): one `dotnet run` starts both
// bOps.Api and the Angular dev server together, instead of two separate shells. Both ports are
// pinned to match web/bops-ui/proxy.conf.json (which hardcodes http://localhost:5080 for /api)
// and .claude/launch.json (which expects the UI on 4200) — this does not replace either of those,
// it is simply a third way to start the same two processes.
var api = builder.AddProject<Projects.bOps_Api>("bops-api")
    .WithHttpEndpoint(port: 5080, name: "http");

// isProxied: false — `ng serve` always binds 4200 itself and does not read the PORT env var
// Aspire's proxy would otherwise inject; with proxying on, DCP tries to own port 4200 for its own
// listener and `ng serve` then fails to bind the same port ("Port 4200 is already in use"),
// confirmed by actually running this AppHost, not assumed.
builder.AddJavaScriptApp("bops-ui", "../../web/bops-ui", "start")
    .WithHttpEndpoint(port: 4200, isProxied: false)
    .WaitFor(api);

builder.Build().Run();
