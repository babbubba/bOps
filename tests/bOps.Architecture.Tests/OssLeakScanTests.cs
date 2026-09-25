// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace bOps.Architecture.Tests;

/// <summary>
/// V1.3-M8 OSS leak scan (ADR-0036, <c>agentic/_tasks/2026-09-22-v1.3-m-execution-packets.md</c>).
/// The public repository owns only product-neutral contracts and lifecycle mechanics: no commercial
/// tier or price logic, payment provider, private token or licence format, private key material,
/// commercial prompts or fixtures, and no Coordinator or Portal implementation may exist in the
/// public code trees.
/// <para>
/// The scan is deliberately targeted rather than a word blacklist. It reads code, tests, samples,
/// scripts, workflows and the Angular sources — never <c>docs/</c> or <c>agentic/</c>, which state
/// these exclusions in prose — and matches identifiers that only a private product implementation
/// would carry.
/// </para>
/// </summary>
public sealed class OssLeakScanTests
{
    private static readonly (string Rule, Regex Pattern)[] Rules =
    [
        ("payment provider", new Regex(@"\b(stripe|paypal|paddle|braintree|lemon\s?squeezy|adyen|chargebee|fastspring)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("private commercial repository", new Regex(@"bOps\.Commercial", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("commercial SKU", new Regex(@"\bSKU[-_ ]?[A-Z0-9]{2,}\b|\bskuId\b|\bsku_id\b|\bplanSku\b", RegexOptions.Compiled)),
        ("commercial tier or plan logic", new Regex(@"\b(community|pro|enterprise|commercial)[ _-]?(tier|plan|edition)\b|\b(Tier|Plan)(Community|Pro|Enterprise|Commercial)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("price", new Regex(@"[$€£]\s?\d+([.,]\d{1,2})?\s*(/|per)\s*(mo|month|yr|year|seat|node)\b|\bpriceId\b|\bunitPrice\b|\bmonthlyPrice\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("private token or licence format", new Regex(@"\blicen[cs]e[ _-]?(key|token|blob|file)Format\b|\bbops[-_]lic[-_](v\d|token)\b|\bBOPS[-_]LICENSE[-_]", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("private key material", new Regex(@"-----BEGIN (RSA |EC |ED25519 |OPENSSH |ENCRYPTED )?PRIVATE KEY-----", RegexOptions.Compiled)),
        ("commercial prompts or playbooks", new Regex(@"\b(dba[ _-]?playbook|commercial[ _-]?playbook|premium[ _-]?prompt)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("private Coordinator or Portal implementation", new Regex(@"\b(bOps\.)?(Coordinator|Portal)(Service|Client|Host|Api)\b|\bControlPlane\.Private\b", RegexOptions.Compiled)),
    ];

    private static readonly string[] ScannedRoots = ["src", "tests", "samples", "scripts", ".github", Path.Combine("web", "bops-ui", "src")];

    private static readonly string[] ScannedExtensions = [".cs", ".csproj", ".props", ".json", ".ts", ".html", ".css", ".ps1", ".yml", ".yaml", ".xml", ".txt", ".md"];

    private static readonly string[] SkippedSegments = ["bin", "obj", "node_modules", "dist", "TestResults", ".angular"];

    [Fact]
    public void PublicCodeTrees_ContainNoPrivateProductOrBusinessIdentifiers()
    {
        var offending = new List<string>();
        foreach (var file in PublicFiles())
        {
            var text = File.ReadAllText(file);
            foreach (var (rule, pattern) in Rules)
            {
                var match = pattern.Match(text);
                if (match.Success)
                {
                    offending.Add($"  [{rule}] '{match.Value}' in {Path.GetRelativePath(RepositoryRoot()!, file)}");
                }
            }
        }

        Assert.True(
            offending.Count == 0,
            "V1.3-M8 — the public repository must hold only product-neutral entitlement and lifecycle mechanics:\n" + string.Join('\n', offending));
    }

    [Fact]
    public void TheScan_ReachesTheCodeTreesAndTheAngularSources()
    {
        var files = PublicFiles().Select(file => file.Replace('\\', '/')).ToList();

        Assert.True(files.Count > 400, $"Only {files.Count} files were scanned; the repository root was not found.");
        Assert.Contains(files, file => file.EndsWith("/src/core/bOps.PluginHost/PluginLifecycleService.cs", StringComparison.Ordinal));
        Assert.Contains(files, file => file.EndsWith("/tests/bOps.Architecture.Tests/EntitlementContractTests.cs", StringComparison.Ordinal));
        Assert.Contains(files, file => file.Contains("/web/bops-ui/src/app/", StringComparison.Ordinal) && file.EndsWith(".ts", StringComparison.Ordinal));
        Assert.DoesNotContain(files, file => file.Contains("/docs/", StringComparison.Ordinal) || file.Contains("/agentic/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("payment provider", "var checkout = new StripeClient();")]
    [InlineData("private commercial repository", "<ProjectReference Include=\"..\\bOps.Commercial\\x.csproj\" />")]
    [InlineData("commercial SKU", "const sku = 'SKU-PRO12';")]
    [InlineData("commercial tier or plan logic", "if (license.Tier == PlanEnterprise) Allow();")]
    [InlineData("price", "label = '$49 per month';")]
    [InlineData("private token or licence format", "const string Prefix = \"BOPS-LICENSE-\";")]
    [InlineData("private key material", "-----BEGIN PRIVATE KEY-----")]
    [InlineData("commercial prompts or playbooks", "var p = LoadDbaPlaybook(\"dba-playbook\");")]
    [InlineData("private Coordinator or Portal implementation", "services.AddSingleton<CoordinatorClient>();")]
    public void EveryRule_ActuallyMatchesTheLeakItIsNamedAfter(string rule, string sample)
    {
        var pattern = Rules.Single(candidate => candidate.Rule == rule).Pattern;

        Assert.Matches(pattern, sample);
    }

    [Theory]
    [InlineData("A neutral entitlement decision carries a reason code and a source category.")]
    [InlineData("The community may contribute plugins; see CONTRIBUTING.")]
    [InlineData("The Control Plane protocol is versioned separately.")]
    [InlineData("EstimatedCostUsd: an estimated cost, when the provider reports pricing.")]
    public void TheRules_DoNotFlagNeutralVocabulary(string sample)
    {
        Assert.DoesNotContain(Rules, rule => rule.Pattern.IsMatch(sample));
    }

    private static IEnumerable<string> PublicFiles()
    {
        var root = RepositoryRoot();
        if (root is null)
        {
            yield break;
        }

        foreach (var scannedRoot in ScannedRoots)
        {
            var directory = Path.Combine(root, scannedRoot);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (segments.Any(segment => SkippedSegments.Contains(segment, StringComparer.Ordinal))
                    || string.Equals(Path.GetFileName(file), "packages.lock.json", StringComparison.Ordinal)
                    || string.Equals(Path.GetFileName(file), "package-lock.json", StringComparison.Ordinal)
                    || string.Equals(Path.GetFileName(file), nameof(OssLeakScanTests) + ".cs", StringComparison.Ordinal)
                    || !ScannedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static string? RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "bOps.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    }
}
