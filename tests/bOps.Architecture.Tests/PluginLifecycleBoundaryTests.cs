// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace bOps.Architecture.Tests;

/// <summary>
/// V1.3-M8 (ADR-0020, ADR-0037): the lifecycle service is the single mutation and loading authority
/// for plugins. Candidate bytes are untrusted until validation completes, validation only reads
/// assembly metadata, and no first-party surface may reach the low-level <c>PluginManager</c>
/// mutations directly. Read from the source tree, like the other boundary tests, so it also covers
/// projects this test assembly does not reference.
/// </summary>
public sealed class PluginLifecycleBoundaryTests
{
    private static readonly Regex AssemblyLoading = new(
        @"LoadFromAssemblyPath\(|LoadFromStream\(|LoadFromAssemblyName\(|Assembly\.Load(From|File)?\(|Assembly\.UnsafeLoadFrom\(",
        RegexOptions.Compiled);

    private static readonly Regex ManagerMutation = new(
        @"\b(_?pluginManager|_?manager|_?plugins|_?pluginHost)\s*\.\s*(Install|Enable|Disable|Remove|Activate|Deactivate|LoadAllEnabled|LoadEnabled)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void CandidateAssemblies_AreLoadedOnlyByThePluginManagerAndItsLoadContext()
    {
        var loaders = Sources()
            .Where(file => AssemblyLoading.IsMatch(StripComments(File.ReadAllText(file))))
            .Select(RelativeName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["src/core/bOps.PluginHost/PluginLoadContext.cs", "src/core/bOps.PluginHost/PluginManager.cs"],
            loaders);
    }

    [Fact]
    public void CandidateValidation_ReadsMetadataOnly_AndNeverLoadsTheAssembly()
    {
        var inspector = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "core", "bOps.PluginHost", "PluginCandidateInspector.cs"));

        Assert.Contains("new PEReader(", inspector, StringComparison.Ordinal);
        Assert.DoesNotMatch(AssemblyLoading, StripComments(inspector));
        Assert.DoesNotContain("Activator.", inspector, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagerActivationAndMutation_AreReachedOnlyThroughTheLifecycleService()
    {
        var offenders = Sources()
            .Where(file => !RelativeName(file).StartsWith("src/core/bOps.PluginHost/", StringComparison.Ordinal))
            .SelectMany(file => ManagerMutation.Matches(StripComments(File.ReadAllText(file))).Select(match => $"  {match.Value.Trim()} in {RelativeName(file)}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "ADR-0037 — first-party API/CLI/UI code must mutate plugins only through PluginLifecycleService:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void WithinThePluginHost_OnlyTheLifecycleServiceActivatesOrDeactivatesPlugins()
    {
        var callers = Sources()
            .Where(file => RelativeName(file).StartsWith("src/core/bOps.PluginHost/", StringComparison.Ordinal)
                && !RelativeName(file).EndsWith("/PluginManager.cs", StringComparison.Ordinal))
            .Where(file => Regex.IsMatch(StripComments(File.ReadAllText(file)), @"\b_manager\s*\.\s*(Activate|Deactivate|LoadEnabled)\s*\("))
            .Select(RelativeName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(callers);
        Assert.All(callers, file => Assert.StartsWith("src/core/bOps.PluginHost/PluginLifecycleService", file, StringComparison.Ordinal));
    }

    [Fact]
    public void TheApiExposesNoPluginDeleteRoute_AndTheCliRemoveVerbOnlyRefuses()
    {
        var api = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "core", "bOps.Api", "PluginLifecycleEndpoints.cs"));
        var catalog = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "core", "bOps.Api", "PluginCatalogEndpoints.cs"));
        var cli = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "core", "bOps.Cli", "PluginCommand.cs"));

        Assert.DoesNotContain("MapDelete", api + catalog, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"(lifecycle|_lifecycle|manager)\s*\.\s*(Remove|Delete|Uninstall)\w*\s*\(", RegexOptions.IgnoreCase), StripComments(cli));
        Assert.Contains("Remove refused", cli, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScan_ActuallyReachesThePluginHostAndTheFirstPartySurfaces()
    {
        var files = Sources().Select(RelativeName).ToList();

        Assert.Contains("src/core/bOps.PluginHost/PluginManager.cs", files);
        Assert.Contains("src/core/bOps.PluginHost/PluginLifecycleService.cs", files);
        Assert.Contains("src/core/bOps.Api/PluginLifecycleEndpoints.cs", files);
        Assert.Contains("src/core/bOps.Cli/PluginCommand.cs", files);
    }

    [Theory]
    [InlineData("var a = loadContext.LoadFromAssemblyPath(path);")]
    [InlineData("Assembly.LoadFrom(path);")]
    public void TheLoaderPattern_MatchesTheApisItGuards(string sample) => Assert.Matches(AssemblyLoading, sample);

    [Theory]
    [InlineData("pluginManager.Enable(id);")]
    [InlineData("_manager.Install(dir);")]
    [InlineData("manager.Remove(id)")]
    public void TheMutationPattern_MatchesTheCallsItGuards(string sample) => Assert.Matches(ManagerMutation, sample);

    [Fact]
    public void TheMutationPattern_IgnoresReadOnlyCalls() => Assert.DoesNotMatch(ManagerMutation, "pluginManager.List(); manager.IsActivated(id);");

    private static string StripComments(string source) =>
        Regex.Replace(source, @"//.*?$|/\*.*?\*/", string.Empty, RegexOptions.Multiline | RegexOptions.Singleline);

    private static IEnumerable<string> Sources()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        return Directory
            .EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string RelativeName(string file) => Path.GetRelativePath(RepositoryRoot(), file).Replace('\\', '/');

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "bOps.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root (bOps.slnx) was not found.");
    }
}
