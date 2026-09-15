using bOps.Abstractions;

namespace bOps.Policy;

/// <summary>
/// Decides <see cref="PolicyMode"/> for a tool call from a loaded <see cref="PolicyConfig"/>.
/// This type never names a specific tool or package in code (agentic/01-architecture-rules.md,
/// rule A1) — every tool/package-specific decision comes from the data in
/// <paramref name="config"/> below, never from an identifier written here.
/// </summary>
public sealed class PolicyEngine(PolicyConfig config) : IPolicyEngine
{
    /// <inheritdoc />
    public PolicyDecision Evaluate(PolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var manifest = context.Manifest;

        // Rule S3: "a tool whose package cannot be identified" resolves to Forbidden. The normal
        // AgentRunner flow never reaches here for an unresolved tool (that is handled earlier, as
        // AuthorizationKind.UnknownTool, before a manifest even exists) — this is defense in
        // depth for a PolicyContext built any other way.
        if (context.Package == PackageId.Unknown)
        {
            return new PolicyDecision(PolicyMode.Forbidden, "The tool's package could not be identified.");
        }

        // Rule S3: Critical is forbidden and cannot be configured otherwise. Checked first, and
        // unconditionally, so nothing below — a tool override, a package ceiling — can ever
        // reach a different answer for a Critical-risk tool, even if PolicyConfigLoader's own
        // load-time rejection were somehow bypassed.
        if (manifest.Risk == RiskLevel.Critical)
        {
            return new PolicyDecision(PolicyMode.Forbidden,
                "Critical-risk actions are always forbidden; this cannot be configured otherwise.");
        }

        // Rule S3: per-package ceilings are applied after the per-tool decision and can only
        // lower it, never raise it — a tool whose risk exceeds its package's configured ceiling
        // is forbidden regardless of what a tool override or risk default would otherwise allow.
        if (config.PackageCeilings.TryGetValue(context.Package.Value, out var ceiling) && manifest.Risk > ceiling)
        {
            return new PolicyDecision(PolicyMode.Forbidden,
                $"'{manifest.Name}' is {manifest.Risk}-risk, which exceeds the configured ceiling ({ceiling}) for its package.");
        }

        if (config.ToolOverrides.TryGetValue(manifest.Name, out var toolMode))
        {
            return new PolicyDecision(toolMode, $"policy.yaml configures '{manifest.Name}' explicitly as {toolMode}.");
        }

        if (config.Defaults.TryGetValue(manifest.Risk, out var defaultMode))
        {
            return new PolicyDecision(defaultMode, $"policy.yaml's default for {manifest.Risk}-risk tools is {defaultMode}.");
        }

        // Rule S3: "a missing policy entry" resolves to Forbidden, never Automatic.
        return new PolicyDecision(PolicyMode.Forbidden,
            $"No policy entry covers {manifest.Risk}-risk tools; failing closed.");
    }
}
