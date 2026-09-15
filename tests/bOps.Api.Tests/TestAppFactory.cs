using bOps.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace bOps.Api.Tests;

/// <summary>
/// Boots the real <c>bOps.Api</c> composition root (<see cref="Program"/>) against an isolated
/// temp directory for its audit log/task store/policy file, with an optional test-double
/// <see cref="IChatModel"/> and <see cref="IPolicyEngine"/> substituted in place of the real ones
/// (set before the first request — <see cref="WebApplicationFactory{TEntryPoint}"/> builds the
/// host lazily). "Test the composition, not the logic" (agentic/04-testing-rules.md) — this drives
/// the actual HTTP surface, not a reimplementation of it.
/// </summary>
internal sealed class TestAppFactory : WebApplicationFactory<Program>
{
    public string TempDirectory { get; } = Directory.CreateTempSubdirectory("bops-api-tests-").FullName;

    public IChatModel? ChatModel { get; set; }

    public IPolicyEngine? PolicyEngine { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:FilePath"] = Path.Combine(TempDirectory, "audit.jsonl"),
                ["Memory:FilePath"] = Path.Combine(TempDirectory, "tasks.db"),
                ["Policy:FilePath"] = Path.Combine(TempDirectory, "policy.yaml"),
            });
        });

        builder.ConfigureServices(services =>
        {
            if (ChatModel is { } chatModel)
            {
                services.AddSingleton(chatModel);
            }

            if (PolicyEngine is { } policyEngine)
            {
                services.AddSingleton(policyEngine);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && Directory.Exists(TempDirectory))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup only — a file still briefly locked by SQLite's pooled
                // connection teardown must never fail the test that already passed.
            }
        }
    }
}
