// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using bOps.Abstractions;
using bOps.Hosting;
using bOps.Packages.Docker;
using bOps.Packages.Filesystem;
using bOps.Packages.Web;

namespace bOps.Architecture.Tests;

/// <summary>
/// HARDEN-6 / ADR-0022: a bound the model must respect is a typed <see cref="ToolParameter"/> constraint, never prose alone.
/// A description that states a numeric range ("1-500", "1..2000", "from 1 to 1000", "2 to 128 letters") must be backed by the
/// matching constraint, and the constraints the first-party manifests declare are pinned to a reviewed snapshot so one can
/// neither appear nor disappear unnoticed.
/// </summary>
public sealed partial class ToolParameterConstraintParityTests
{
    [GeneratedRegex(@"(?<![\w.-])(?<lo>\d+)\s*(?:-|\.\.|to|through)\s*(?<hi>\d+)(?![\w-]|\.\d)", RegexOptions.CultureInvariant)]
    private static partial Regex RangePattern();

    [GeneratedRegex(@"up to (?<hi>\d+) characters", RegexOptions.CultureInvariant)]
    private static partial Regex UpToCharactersPattern();

    [Fact]
    public void EveryRangeStatedInAParameterDescription_HasTheMatchingTypedConstraint()
    {
        var violations = FindViolations(Parameters());

        Assert.True(violations.Count == 0, "Ranges stated only in prose:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void TheParityCheck_FlagsARangeStatedOnlyInProse_AndAcceptsOneThatIsBacked()
    {
        var prose = new ToolParameter("limit", ToolParameterType.Integer, "Maximum rows (1-500, default 50).", Required: false);
        var backed = prose with { Minimum = 1, Maximum = 500 };
        var wrongBound = prose with { Minimum = 1, Maximum = 400 };
        var name = new ToolParameter("volume", ToolParameterType.String, "The name: 2 to 128 letters.");

        Assert.Single(FindViolations([("t", prose)]));
        Assert.Empty(FindViolations([("t", backed)]));
        Assert.Single(FindViolations([("t", wrongBound)]));
        Assert.Single(FindViolations([("t", name)]));
        Assert.Empty(FindViolations([("t", name with { MinLength = 2, MaxLength = 128 })]));
    }

    [Theory]
    [InlineData("Maximum rows (1-2000, default 200).", 1, 2000)]
    [InlineData("Maximum returned items, 1..2000.", 1, 2000)]
    [InlineData("Maximum rows, 1 through 1000. Defaults to 100.", 1, 1000)]
    [InlineData("Maximum relations, from 1 to 1000. Defaults to 100.", 1, 1000)]
    [InlineData("Lookback in minutes, 1..10080.", 1, 10080)]
    [InlineData("Local port 1-65535", 1, 65535)]
    public void TheRangePattern_RecognisesTheNotationsTheManifestsUse(string description, long low, long high)
    {
        var match = RangePattern().Match(description);

        Assert.True(match.Success, description);
        Assert.Equal((low, high), (long.Parse(match.Groups["lo"].Value, CultureInfo.InvariantCulture), long.Parse(match.Groups["hi"].Value, CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("Milliseconds to wait for a reply. Defaults to 4000, capped at 10000.")]
    [InlineData("ISO language code, e.g. 'en' or 'en-US'.")]
    [InlineData("0 (off), 1 (moderate) or 2 (strict). Defaults to the instance.")]
    public void TheRangePattern_DoesNotMistakeOtherNumbersForARange(string description) =>
        Assert.False(RangePattern().IsMatch(description), description);

    [Fact]
    public void SystemCrashes_SinceMinutes_IsConstrainedToTheDocumentedWindow()
    {
        var parameter = Parameters().Single(entry => entry.Tool == "system.crashes" && entry.Parameter.Name == "sinceMinutes").Parameter;

        Assert.Equal(ToolParameterType.Integer, parameter.Type);
        Assert.Equal(1, parameter.Minimum);
        Assert.Equal(10080, parameter.Maximum);
    }

    private static List<string> FindViolations(IEnumerable<(string Tool, ToolParameter Parameter)> entries)
    {
        var violations = new List<string>();
        foreach (var (tool, parameter) in entries)
        {
            var match = RangePattern().Match(parameter.Description);
            if (match.Success)
            {
                var low = long.Parse(match.Groups["lo"].Value, CultureInfo.InvariantCulture);
                var high = long.Parse(match.Groups["hi"].Value, CultureInfo.InvariantCulture);
                var (actualLow, actualHigh) = parameter.Type switch
                {
                    ToolParameterType.Integer or ToolParameterType.Number => ((double?)parameter.Minimum, parameter.Maximum),
                    ToolParameterType.String or ToolParameterType.Path => (parameter.MinLength, parameter.MaxLength),
                    ToolParameterType.PathList => (parameter.MinItems, parameter.MaxItems),
                    _ => ((double?)null, (double?)null),
                };
                if (actualLow != low || actualHigh != high)
                {
                    violations.Add($"{tool}.{parameter.Name}: description states {low}..{high} but the constraint is {actualLow?.ToString(CultureInfo.InvariantCulture) ?? "none"}..{actualHigh?.ToString(CultureInfo.InvariantCulture) ?? "none"}");
                }
            }

            var upTo = UpToCharactersPattern().Match(parameter.Description);
            if (upTo.Success && parameter.MaxLength != int.Parse(upTo.Groups["hi"].Value, CultureInfo.InvariantCulture))
            {
                violations.Add($"{tool}.{parameter.Name}: description states up to {upTo.Groups["hi"].Value} characters but MaxLength is {parameter.MaxLength?.ToString(CultureInfo.InvariantCulture) ?? "none"}");
            }
        }

        return violations;
    }

    [Fact]
    public void EveryDeclaredConstraint_IsApplicableToItsParameterTypeAndConsistent()
    {
        foreach (var (tool, parameter) in Parameters())
        {
            if (parameter.Minimum is not null || parameter.Maximum is not null)
            {
                Assert.True(parameter.Type is ToolParameterType.Integer or ToolParameterType.Number, $"{tool}.{parameter.Name}: numeric bound on {parameter.Type}");
                Assert.True(parameter.Minimum is null || parameter.Maximum is null || parameter.Minimum <= parameter.Maximum, $"{tool}.{parameter.Name}: Minimum > Maximum");
            }

            if (parameter.MinLength is not null || parameter.MaxLength is not null)
            {
                Assert.True(parameter.Type is ToolParameterType.String or ToolParameterType.Path, $"{tool}.{parameter.Name}: length bound on {parameter.Type}");
                Assert.True(parameter.MinLength is null || parameter.MaxLength is null || parameter.MinLength <= parameter.MaxLength, $"{tool}.{parameter.Name}: MinLength > MaxLength");
            }

            if (parameter.MinItems is not null || parameter.MaxItems is not null)
            {
                Assert.True(parameter.Type == ToolParameterType.PathList, $"{tool}.{parameter.Name}: item bound on {parameter.Type}");
                Assert.True(parameter.MinItems is null || parameter.MaxItems is null || parameter.MinItems <= parameter.MaxItems, $"{tool}.{parameter.Name}: MinItems > MaxItems");
            }
        }
    }

    [Fact]
    public void FirstPartyConstraints_MatchTheReviewedSnapshot()
    {
        var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Snapshots", "tool-parameter-constraints.txt"))
            .Where(line => line.Length > 0)
            .ToArray();

        Assert.Equal(expected, Describe());
    }

    /// <summary>Not a check: with <c>BOPS_WRITE_CONSTRAINTS_TO</c> set it (re)writes the reviewed snapshot. Without it, it does nothing.</summary>
    [Fact]
    public void WriteSnapshot_WhenAsked()
    {
        var target = Environment.GetEnvironmentVariable("BOPS_WRITE_CONSTRAINTS_TO");
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        File.WriteAllText(target, string.Join('\n', Describe()) + "\n", new UTF8Encoding(false));
    }

    private static string[] Describe() =>
        Parameters()
            .Where(entry => entry.Parameter.Minimum is not null || entry.Parameter.Maximum is not null
                || entry.Parameter.MinLength is not null || entry.Parameter.MaxLength is not null
                || entry.Parameter.MinItems is not null || entry.Parameter.MaxItems is not null)
            .Select(entry =>
            {
                var parameter = entry.Parameter;
                var builder = new StringBuilder($"{entry.Tool}.{parameter.Name} | {parameter.Type}");
                Append(builder, "minimum", parameter.Minimum);
                Append(builder, "maximum", parameter.Maximum);
                Append(builder, "minLength", parameter.MinLength);
                Append(builder, "maxLength", parameter.MaxLength);
                Append(builder, "minItems", parameter.MinItems);
                Append(builder, "maxItems", parameter.MaxItems);
                return builder.ToString();
            })
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static void Append(StringBuilder builder, string name, double? value)
    {
        if (value is { } bound)
        {
            builder.Append(CultureInfo.InvariantCulture, $" | {name}={bound}");
        }
    }

    private static (string Tool, ToolParameter Parameter)[] Parameters()
    {
        var filesystemProvider = new FilesystemToolProvider(new FilesystemPathPolicy([], []));
        using var webProvider = new WebToolProvider(new WebFetchOptions(), new WebSearchOptions());
        var registrations = FirstPartyToolComposition.Create(new FirstPartyToolCompositionOptions(
            filesystemProvider,
            webProvider,
            new DockerClientFactory(),
            new DockerBuildOptions(),
            new DockerVolumeOptions()));

        return registrations
            .SelectMany(registration => registration.Tool.Manifest.Parameters.Select(parameter => (Tool: registration.Tool.Manifest.Name, Parameter: parameter)))
            .ToArray();
    }
}
