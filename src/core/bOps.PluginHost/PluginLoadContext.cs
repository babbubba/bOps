// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.Loader;
using bOps.Abstractions;

namespace bOps.PluginHost;

/// <summary>
/// One isolated, collectible load context per plugin (ADR-0020). A plugin's own dependencies
/// resolve from its own folder via <see cref="AssemblyDependencyResolver"/>; the one deliberate
/// exception is <c>bOps.Abstractions</c> itself, which is never loaded a second time — this
/// falls through to the copy the host's default context already has loaded, so the plugin and
/// the host always talk through the exact same contract types. Collectible so
/// <see cref="AssemblyLoadContext.Unload"/> is a real disable, not merely "not called."
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string AbstractionsAssemblyName = typeof(PackageId).Assembly.GetName().Name!;

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string mainAssemblyPath)
        : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, AbstractionsAssemblyName, StringComparison.Ordinal))
        {
            return null;
        }

        var resolvedPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return resolvedPath is not null ? LoadFromAssemblyPath(resolvedPath) : null;
    }
}
