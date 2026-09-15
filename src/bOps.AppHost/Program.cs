var builder = DistributedApplication.CreateBuilder(args);

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

builder.Build().Run();
