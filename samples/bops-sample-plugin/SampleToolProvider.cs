using bOps.Abstractions;

namespace Acme.SamplePlugin;

/// <summary>
/// The plugin's entry type, as named by <c>EntryType</c> in <c>bops-plugin.json</c>. The loader
/// activates exactly one instance of this via <c>ActivatorUtilities</c>, against a container
/// exposing only the A10 list — this constructor takes nothing, so it works unchanged whether
/// that list ever grows.
/// </summary>
public sealed class SampleToolProvider : IToolProvider
{
    public IEnumerable<ITool> GetTools() => [new SampleEchoTool()];
}
