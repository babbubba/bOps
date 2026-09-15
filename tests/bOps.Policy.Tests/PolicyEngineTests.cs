// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Policy.Tests;

/// <summary>agentic/03-security-rules.md, rule S3: policy fails closed at every seam.</summary>
public sealed class PolicyEngineTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");
    private static readonly PackageId Package = new("bops.packages.test");

    private static ToolManifest Manifest(string name, RiskLevel risk) => new()
    {
        Name = name,
        Description = "A test tool.",
        Risk = risk,
        Platforms = ["windows"],
        Requires = [],
        Parameters = [],
        Verification = risk == RiskLevel.Read ? null : new VerificationSpec("test.read", [], "n/a"),
        Package = Package,
    };

    private static PolicyContext Context(ToolManifest manifest, PackageTrustLevel trust = PackageTrustLevel.Official) =>
        new(NodeId.Local, manifest.Package, trust, manifest, ToolArguments.Empty, Actor);

    [Fact]
    public void Evaluate_ReturnsForbidden_ForCriticalRisk_EvenIfConfigTriesToAllowIt()
    {
        // The loader would reject this configuration outright (PolicyConfigLoaderTests), but the
        // engine enforces the invariant itself too — defense in depth (rule S3).
        var config = new PolicyConfig(
            defaults: new Dictionary<RiskLevel, PolicyMode> { [RiskLevel.Critical] = PolicyMode.Automatic },
            toolOverrides: new Dictionary<string, PolicyMode>(),
            packageCeilings: new Dictionary<string, RiskLevel>());
        var engine = new PolicyEngine(config);

        var decision = engine.Evaluate(Context(Manifest("fs.delete", RiskLevel.Critical)));

        Assert.Equal(PolicyMode.Forbidden, decision.Mode);
    }

    [Fact]
    public void Evaluate_ReturnsForbidden_WhenThePackageIsUnknown()
    {
        var engine = new PolicyEngine(PolicyConfig.SafeDefault);
        var manifest = new ToolManifest
        {
            Name = "system.cpu",
            Description = "n/a",
            Risk = RiskLevel.Read,
            Platforms = ["windows"],
            Requires = [],
            Parameters = [],
            Package = PackageId.Unknown,
        };

        var decision = engine.Evaluate(Context(manifest));

        Assert.Equal(PolicyMode.Forbidden, decision.Mode);
    }

    [Fact]
    public void Evaluate_UsesTheRiskDefault_WhenNoToolOverrideExists()
    {
        var engine = new PolicyEngine(PolicyConfig.SafeDefault);

        var decision = engine.Evaluate(Context(Manifest("system.cpu", RiskLevel.Read)));

        Assert.Equal(PolicyMode.Automatic, decision.Mode);
    }

    [Fact]
    public void Evaluate_PrefersTheToolOverride_OverTheRiskDefault()
    {
        var config = new PolicyConfig(
            defaults: new Dictionary<RiskLevel, PolicyMode> { [RiskLevel.Read] = PolicyMode.Automatic },
            toolOverrides: new Dictionary<string, PolicyMode> { ["system.cpu"] = PolicyMode.Approval },
            packageCeilings: new Dictionary<string, RiskLevel>());
        var engine = new PolicyEngine(config);

        var decision = engine.Evaluate(Context(Manifest("system.cpu", RiskLevel.Read)));

        Assert.Equal(PolicyMode.Approval, decision.Mode);
    }

    [Fact]
    public void Evaluate_ReturnsForbidden_WhenNoDefaultCoversTheRiskLevel()
    {
        var config = new PolicyConfig(
            defaults: new Dictionary<RiskLevel, PolicyMode>(), // nothing configured
            toolOverrides: new Dictionary<string, PolicyMode>(),
            packageCeilings: new Dictionary<string, RiskLevel>());
        var engine = new PolicyEngine(config);

        var decision = engine.Evaluate(Context(Manifest("system.cpu", RiskLevel.Read)));

        Assert.Equal(PolicyMode.Forbidden, decision.Mode);
    }

    [Fact]
    public void Evaluate_AppliesThePackageCeiling_ForcingForbidden_WhenRiskExceedsIt()
    {
        var config = new PolicyConfig(
            defaults: new Dictionary<RiskLevel, PolicyMode> { [RiskLevel.High] = PolicyMode.Approval },
            toolOverrides: new Dictionary<string, PolicyMode>(),
            packageCeilings: new Dictionary<string, RiskLevel> { [Package.Value] = RiskLevel.Medium });
        var engine = new PolicyEngine(config);

        var decision = engine.Evaluate(Context(Manifest("service.restart", RiskLevel.High)));

        Assert.Equal(PolicyMode.Forbidden, decision.Mode);
    }

    [Fact]
    public void Evaluate_PackageCeiling_CannotMakeADecisionMorePermissive()
    {
        // agentic/04-testing-rules.md, "Policy engine": "a package ceiling that tries to raise
        // one (must not)". A ceiling can only forbid a risk level it does not cover — it is never
        // itself a source of permission, so a *high* ceiling must not override an otherwise
        // Forbidden decision (rule S3: ceilings can only lower a decision, never raise it).
        var config = new PolicyConfig(
            defaults: new Dictionary<RiskLevel, PolicyMode>(), // no default at all -> Forbidden
            toolOverrides: new Dictionary<string, PolicyMode>(),
            packageCeilings: new Dictionary<string, RiskLevel> { [Package.Value] = RiskLevel.Critical }); // generous ceiling
        var engine = new PolicyEngine(config);

        var decision = engine.Evaluate(Context(Manifest("service.restart", RiskLevel.High)));

        Assert.Equal(PolicyMode.Forbidden, decision.Mode);
    }

    [Fact]
    public void Evaluate_PackageCeiling_DoesNotAffectARiskWithinIt()
    {
        var config = new PolicyConfig(
            defaults: new Dictionary<RiskLevel, PolicyMode> { [RiskLevel.Low] = PolicyMode.Automatic },
            toolOverrides: new Dictionary<string, PolicyMode>(),
            packageCeilings: new Dictionary<string, RiskLevel> { [Package.Value] = RiskLevel.Medium });
        var engine = new PolicyEngine(config);

        var decision = engine.Evaluate(Context(Manifest("network.ping", RiskLevel.Low)));

        Assert.Equal(PolicyMode.Automatic, decision.Mode);
    }

    [Fact]
    public void SafeDefault_IsAutomaticForReadAndLow_ApprovalForMediumAndHigh_ForbiddenForCritical()
    {
        var engine = new PolicyEngine(PolicyConfig.SafeDefault);

        Assert.Equal(PolicyMode.Automatic, engine.Evaluate(Context(Manifest("t", RiskLevel.Read))).Mode);
        Assert.Equal(PolicyMode.Automatic, engine.Evaluate(Context(Manifest("t", RiskLevel.Low))).Mode);
        Assert.Equal(PolicyMode.Approval, engine.Evaluate(Context(Manifest("t", RiskLevel.Medium))).Mode);
        Assert.Equal(PolicyMode.Approval, engine.Evaluate(Context(Manifest("t", RiskLevel.High))).Mode);
        Assert.Equal(PolicyMode.Forbidden, engine.Evaluate(Context(Manifest("t", RiskLevel.Critical))).Mode);
    }

    [Fact]
    public void AllForbidden_ForbidsEveryRiskLevel()
    {
        var engine = new PolicyEngine(PolicyConfig.AllForbidden);

        foreach (var risk in Enum.GetValues<RiskLevel>())
        {
            Assert.Equal(PolicyMode.Forbidden, engine.Evaluate(Context(Manifest("t", risk))).Mode);
        }
    }
}
