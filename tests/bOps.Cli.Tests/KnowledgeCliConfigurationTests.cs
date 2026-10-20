// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

// CA2000: ownership of every world passes to the test, which disposes it.
#pragma warning disable CA2000

using bOps.AgentFramework;
using bOps.Cli;
using bOps.Knowledge.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Cli.Tests;

/// <summary>
/// ADR-0058: the <c>Knowledge</c> section is opt-in and strict. Disabled (or absent) it changes nothing; enabled, a value that cannot
/// work is an error that stops the command, never a quiet run without the knowledge the operator asked for. Offline, real files.
/// </summary>
public sealed class KnowledgeCliConfigurationTests
{
    [Fact]
    public void WithNoSectionOrWithEnabledFalse_TheHostIsNull_EvenWhenEverythingElseIsMisconfigured()
    {
        using var world = new CliKnowledgeWorld();

        Assert.Null(CliKnowledge.CreateHost(CliKnowledgeWorld.Configuration([]), world.Audit, TimeProvider.System, NullLogger.Instance));
        Assert.Null(world.Host(world.Settings(enabled: false)));

        var broken = world.Settings(enabled: false);
        broken["Knowledge:PacksDirectory"] = Path.Combine(world.Root, "does-not-exist");
        broken.Remove("Knowledge:TrustStorePath");
        Assert.Null(world.Host(broken)); // nothing is validated, opened or created while it is disabled
        Assert.False(Directory.Exists(world.SnapshotsDirectory));
    }

    [Fact]
    public void ASectionThatIsValid_ComposesAHost_WithoutReadingAnyPackOrCreatingASnapshot()
    {
        using var world = new CliKnowledgeWorld();

        var host = world.Host(world.Settings());

        Assert.NotNull(host);
        Assert.False(Directory.Exists(world.SnapshotsDirectory)); // packs are read and snapshotted per task, at admission
    }

    [Theory]
    [InlineData("missing-packs-directory", "PacksDirectory")]
    [InlineData("packs-directory-absent-on-disk", "PacksDirectory")]
    [InlineData("missing-snapshots-directory", "SnapshotsDirectory")]
    [InlineData("snapshots-inside-packs", "separate directories")]
    [InlineData("packs-inside-snapshots", "separate directories")]
    [InlineData("missing-trust-store", "TrustStorePath")]
    [InlineData("trust-store-absent-on-disk", "TrustStorePath")]
    [InlineData("trust-store-not-json", "not a valid trust store")]
    [InlineData("trust-store-with-private-key", "not a valid trust store")]
    [InlineData("trust-store-without-keys", "lists no publisher key")]
    [InlineData("no-grants", "Knowledge:Grants")]
    [InlineData("grant-bad-version", "Knowledge:Grants:0")]
    [InlineData("grant-latest", "Knowledge:Grants:0")]
    [InlineData("grant-experience-kind", "Knowledge packs only")]
    [InlineData("grant-skill-kind", "Knowledge packs only")]
    [InlineData("grant-unknown-kind", "Kind")]
    [InlineData("grant-duplicate", "twice")]
    [InlineData("no-binding", "Knowledge:Binding")]
    [InlineData("binding-bad-pack-id", "Knowledge:Binding")]
    [InlineData("budget-zero-reads", "MaxReads")]
    [InlineData("budget-too-many-reads", "MaxReads")]
    [InlineData("budget-too-large-read", "MaxCharactersPerRead")]
    [InlineData("budget-too-small-read", "MaxCharactersPerRead")]
    public void WhenEnabled_AnUnusableValueStopsTheCommand_NamingTheKey(string defect, string expectedInMessage)
    {
        using var world = new CliKnowledgeWorld();
        var settings = world.Settings();
        switch (defect)
        {
            case "missing-packs-directory": settings.Remove("Knowledge:PacksDirectory"); break;
            case "packs-directory-absent-on-disk": settings["Knowledge:PacksDirectory"] = Path.Combine(world.Root, "nowhere"); break;
            case "missing-snapshots-directory": settings.Remove("Knowledge:SnapshotsDirectory"); break;
            case "snapshots-inside-packs": settings["Knowledge:SnapshotsDirectory"] = Path.Combine(world.PacksDirectory, "snapshots"); break;
            case "packs-inside-snapshots": settings["Knowledge:SnapshotsDirectory"] = world.Root; break;
            case "missing-trust-store": settings.Remove("Knowledge:TrustStorePath"); break;
            case "trust-store-absent-on-disk": settings["Knowledge:TrustStorePath"] = Path.Combine(world.Root, "no-trust.json"); break;
            case "trust-store-not-json": File.WriteAllText(world.TrustStorePath, "this is not json"); break;
            case "trust-store-with-private-key":
                // Built at run time, so the repository holds no key-shaped text: a trust store that carries one must be refused.
                File.WriteAllText(world.TrustStorePath,
                    "{\"schemaVersion\":1,\"keys\":[{\"publisher\":\"acme\",\"keyId\":\"acme-2026\",\"publicKeyPem\":\"-----BEGIN " + "PRIVATE KEY-----\\nAAAA\\n-----END " + "PRIVATE KEY-----\",\"revoked\":false}],\"revokedPublishers\":[]}");
                break;
            case "trust-store-without-keys": File.WriteAllText(world.TrustStorePath, "{\"schemaVersion\":1,\"keys\":[],\"revokedPublishers\":[]}"); break;
            case "no-grants":
                settings.Remove("Knowledge:Grants:0:PackId");
                settings.Remove("Knowledge:Grants:0:Version");
                settings.Remove("Knowledge:Grants:0:Kind");
                break;
            case "grant-bad-version": settings["Knowledge:Grants:0:Version"] = "1.0"; break;
            case "grant-latest": settings["Knowledge:Grants:0:Version"] = "latest"; break;
            case "grant-experience-kind": settings["Knowledge:Grants:0:Kind"] = "Experience"; break;
            case "grant-skill-kind": settings["Knowledge:Grants:0:Kind"] = "Skill"; break;
            case "grant-unknown-kind": settings["Knowledge:Grants:0:Kind"] = "Everything"; break;
            case "grant-duplicate":
                settings["Knowledge:Grants:1:PackId"] = CliKnowledgeWorld.PackId;
                settings["Knowledge:Grants:1:Version"] = CliKnowledgeWorld.PackVersion;
                settings["Knowledge:Grants:1:Kind"] = "Knowledge";
                break;
            case "no-binding":
                settings.Remove("Knowledge:Binding:PackId");
                settings.Remove("Knowledge:Binding:Version");
                break;
            case "binding-bad-pack-id": settings["Knowledge:Binding:PackId"] = "Not A Pack"; break;
            case "budget-zero-reads": settings["Knowledge:Budget:MaxReads"] = "0"; break;
            case "budget-too-many-reads": settings["Knowledge:Budget:MaxReads"] = (LocalKnowledgeComposition.MaxReadsCeiling + 1).ToString(System.Globalization.CultureInfo.InvariantCulture); break;
            case "budget-too-large-read": settings["Knowledge:Budget:MaxCharactersPerRead"] = (LocalKnowledgeComposition.MaxCharactersCeiling + 1).ToString(System.Globalization.CultureInfo.InvariantCulture); break;
            case "budget-too-small-read": settings["Knowledge:Budget:MaxCharactersPerRead"] = "10"; break;
            default: throw new InvalidOperationException(defect);
        }

        var failure = Assert.Throws<LocalKnowledgeConfigurationException>(() => world.Host(settings));

        Assert.Contains(expectedInMessage, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN", failure.Message, StringComparison.Ordinal); // key material is never echoed
    }

    [Theory]
    [InlineData("Knowledge:Enabeld", "true")] // a misspelt key must not read as "disabled"
    [InlineData("Knowledge:Tenant", "tenant-b")] // there is no way to name a tenant
    [InlineData("Knowledge:PrivateKeyPath", "signing.pem")] // the runtime holds no signing key and has no key setting
    [InlineData("Knowledge:Grant:0:PackId", "x")]
    [InlineData("Knowledge:Budget:MaxRead", "3")]
    [InlineData("Knowledge:Binding:Latest", "true")]
    public void AnUnknownKey_IsAnError_WhetherOrNotKnowledgeIsEnabled(string key, string value)
    {
        using var world = new CliKnowledgeWorld();
        foreach (var enabled in new[] { true, false })
        {
            var settings = world.Settings(enabled);
            settings[key] = value;

            Assert.Throws<LocalKnowledgeConfigurationException>(() => world.Host(settings));
        }
    }

    [Fact]
    public void ANonBooleanEnabled_IsAnError_NotADisabledSection()
    {
        using var world = new CliKnowledgeWorld();
        var settings = world.Settings();
        settings["Knowledge:Enabled"] = "maybe";

        Assert.Throws<LocalKnowledgeConfigurationException>(() => world.Host(settings));
    }

    [Fact]
    public void TheShippedDefaultConfiguration_IsDisabled_AndNamesNoPackPathOrKey()
    {
        var shipped = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        using var document = System.Text.Json.JsonDocument.Parse(shipped);
        var section = document.RootElement.GetProperty("Knowledge");

        Assert.False(section.GetProperty("Enabled").GetBoolean());
        Assert.Equal(["Enabled"], section.EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("PUBLIC KEY", shipped, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", shipped, StringComparison.Ordinal);
    }

}
