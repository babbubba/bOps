// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.PluginHost.Tests;

/// <summary>
/// ADR-0020: the manifest gate fails loud, before anything is loaded — never coerces a
/// malformed or incompatible manifest into "probably fine".
/// </summary>
public sealed class PluginManifestValidatorTests : IDisposable
{
    private readonly DirectoryInfo _pluginDir = Directory.CreateTempSubdirectory("bops-plugin-validator-");

    public void Dispose() => _pluginDir.Delete(recursive: true);

    private static readonly Version HostVersion = new(0, 10, 0);

    private static PluginManifest ValidManifest() =>
        new(
            SchemaVersion: PluginManifestValidator.SupportedSchemaVersion,
            Id: "acme.sample-plugin",
            Publisher: "Acme",
            Version: "1.0.0",
            MinHostAbstractionsVersion: "0.10.0",
            EntryAssembly: "AcmeSamplePlugin.dll",
            EntryType: "Acme.SamplePlugin.SampleToolProvider",
            DeclaredCapabilities: ["sample"],
            Dependencies: [],
            MaxDeclaredRisk: RiskLevel.Low);

    private void WriteEntryAssembly(string fileName) =>
        File.WriteAllText(Path.Combine(_pluginDir.FullName, fileName), "not a real assembly, existence is all that is checked here");

    [Fact]
    public void Validate_AcceptsAWellFormedManifest()
    {
        WriteEntryAssembly("AcmeSamplePlugin.dll");

        PluginManifestValidator.Validate(ValidManifest(), _pluginDir.FullName, HostVersion);
    }

    [Fact]
    public void Validate_RejectsAnUnsupportedSchemaVersion()
    {
        WriteEntryAssembly("AcmeSamplePlugin.dll");
        var manifest = ValidManifest() with { SchemaVersion = 999 };

        var ex = Assert.Throws<PluginValidationException>(() => PluginManifestValidator.Validate(manifest, _pluginDir.FullName, HostVersion));
        Assert.Contains("schema version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Acme Sample Plugin")]
    [InlineData("ACME.SAMPLE")]
    [InlineData(".leading-dot")]
    public void Validate_RejectsAMalformedId(string id)
    {
        WriteEntryAssembly("AcmeSamplePlugin.dll");
        var manifest = ValidManifest() with { Id = id };

        var ex = Assert.Throws<PluginValidationException>(() => PluginManifestValidator.Validate(manifest, _pluginDir.FullName, HostVersion));
        Assert.Contains("id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsAnIdClaimingTheReservedBopsPrefix()
    {
        WriteEntryAssembly("AcmeSamplePlugin.dll");
        var manifest = ValidManifest() with { Id = "bops.packages.docker" };

        var ex = Assert.Throws<PluginValidationException>(() => PluginManifestValidator.Validate(manifest, _pluginDir.FullName, HostVersion));
        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsAnEmptyPublisher()
    {
        WriteEntryAssembly("AcmeSamplePlugin.dll");
        var manifest = ValidManifest() with { Publisher = " " };

        var ex = Assert.Throws<PluginValidationException>(() => PluginManifestValidator.Validate(manifest, _pluginDir.FullName, HostVersion));
        Assert.Contains("publisher", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("1")]
    public void Validate_RejectsAMalformedVersion(string version)
    {
        WriteEntryAssembly("AcmeSamplePlugin.dll");
        var manifest = ValidManifest() with { Version = version };

        Assert.Throws<PluginValidationException>(() => PluginManifestValidator.Validate(manifest, _pluginDir.FullName, HostVersion));
    }

    [Fact]
    public void Validate_RejectsAHostVersionOlderThanTheDeclaredMinimum()
    {
        WriteEntryAssembly("AcmeSamplePlugin.dll");
        var manifest = ValidManifest() with { MinHostAbstractionsVersion = "99.0.0" };

        var ex = Assert.Throws<PluginValidationException>(() => PluginManifestValidator.Validate(manifest, _pluginDir.FullName, HostVersion));
        Assert.Contains("bOps.Abstractions", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AcceptsAHostVersionEqualToTheDeclaredMinimum()
    {
        WriteEntryAssembly("AcmeSamplePlugin.dll");
        var manifest = ValidManifest() with { MinHostAbstractionsVersion = "0.10.0" };

        PluginManifestValidator.Validate(manifest, _pluginDir.FullName, HostVersion);
    }

    [Fact]
    public void Validate_RejectsAMissingEntryAssemblyFile()
    {
        var manifest = ValidManifest();

        var ex = Assert.Throws<PluginValidationException>(() => PluginManifestValidator.Validate(manifest, _pluginDir.FullName, HostVersion));
        Assert.Contains("entry assembly", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsAnEmptyEntryType()
    {
        WriteEntryAssembly("AcmeSamplePlugin.dll");
        var manifest = ValidManifest() with { EntryType = "" };

        var ex = Assert.Throws<PluginValidationException>(() => PluginManifestValidator.Validate(manifest, _pluginDir.FullName, HostVersion));
        Assert.Contains("entry type", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsConflictingDependencyVersions()
    {
        WriteEntryAssembly("AcmeSamplePlugin.dll");
        var manifest = ValidManifest() with
        {
            Dependencies =
            [
                new PluginDependency("Newtonsoft.Json", "13.0.3"),
                new PluginDependency("newtonsoft.json", "12.0.0"),
            ],
        };

        var ex = Assert.Throws<PluginValidationException>(() => PluginManifestValidator.Validate(manifest, _pluginDir.FullName, HostVersion));
        Assert.Contains("Newtonsoft.Json", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllowsTheSameDependencyListedTwiceAtTheSameVersion()
    {
        WriteEntryAssembly("AcmeSamplePlugin.dll");
        var manifest = ValidManifest() with
        {
            Dependencies =
            [
                new PluginDependency("Newtonsoft.Json", "13.0.3"),
                new PluginDependency("Newtonsoft.Json", "13.0.3"),
            ],
        };

        PluginManifestValidator.Validate(manifest, _pluginDir.FullName, HostVersion);
    }

    [Fact]
    public void Validate_RejectsAnUndefinedMaxDeclaredRisk()
    {
        WriteEntryAssembly("AcmeSamplePlugin.dll");
        var manifest = ValidManifest() with { MaxDeclaredRisk = (RiskLevel)999 };

        Assert.Throws<PluginValidationException>(() => PluginManifestValidator.Validate(manifest, _pluginDir.FullName, HostVersion));
    }
}
