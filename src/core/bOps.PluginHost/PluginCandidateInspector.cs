// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using bOps.Abstractions;

namespace bOps.PluginHost;

/// <summary>
/// Metadata-only structural inspection of a staged entry assembly (ADR-0037 step 13). It reads the
/// PE/CLI metadata tables and never loads the assembly into an <see cref="System.Runtime.Loader.AssemblyLoadContext"/>,
/// so no plugin-controlled code can run before an explicit enable.
/// </summary>
internal static class PluginCandidateInspector
{
    private static readonly string[] ModelRole = ["IModelProviderPackage"];
    private static readonly string[] ToolSkillRoles = ["IToolProvider", "ISkillProvider"];

    /// <summary>Returns a sanitized failure reason, or <c>null</c> when the candidate is structurally acceptable.</summary>
    public static string? Inspect(string candidateDirectory, PluginManifest manifest)
    {
        var root = Path.GetFullPath(candidateDirectory);
        var assemblyPath = Path.GetFullPath(Path.Combine(root, manifest.EntryAssembly));
        if (!assemblyPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(assemblyPath))
        {
            return "The entry assembly is not present in the plugin package.";
        }

        try
        {
            using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return "The entry assembly is not a managed assembly.";
            }

            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly)
            {
                return "The entry assembly does not contain an assembly manifest.";
            }

            foreach (var handle in metadata.TypeDefinitions)
            {
                var definition = metadata.GetTypeDefinition(handle);
                if (!string.Equals(FullName(metadata, definition), manifest.EntryType, StringComparison.Ordinal))
                {
                    continue;
                }

                var roles = definition.GetInterfaceImplementations()
                    .Select(implementation => InterfaceName(metadata, metadata.GetInterfaceImplementation(implementation).Interface))
                    .Where(name => name is not null)
                    .ToHashSet(StringComparer.Ordinal);
                return roles.Overlaps(ModelRole) && roles.Overlaps(ToolSkillRoles)
                    ? "The entry type combines a model provider with tool/Skill roles."
                    : null;
            }

            return "The declared entry type is not present in the entry assembly.";
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or IOException)
        {
            return "The entry assembly metadata could not be read.";
        }
    }

    private static string FullName(MetadataReader metadata, TypeDefinition definition)
    {
        var name = metadata.GetString(definition.Name);
        var declaring = definition.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return $"{FullName(metadata, metadata.GetTypeDefinition(declaring))}+{name}";
        }

        var ns = metadata.GetString(definition.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    private static string? InterfaceName(MetadataReader metadata, EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeReference => metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)handle).Name),
        HandleKind.TypeDefinition => metadata.GetString(metadata.GetTypeDefinition((TypeDefinitionHandle)handle).Name),
        _ => null,
    };
}
