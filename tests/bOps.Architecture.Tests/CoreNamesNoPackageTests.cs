// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace bOps.Architecture.Tests;

/// <summary>
/// Rule A1: <c>bOps.Runtime</c>, <c>bOps.Policy</c> and <c>bOps.Audit</c> must contain zero
/// literal references to a package-specific tool, provider, or platform detail. "Mechanically
/// checkable and it is checked in CI" (agentic/01-architecture-rules.md) — this is that check.
/// It scans each assembly's compiled string literals directly via
/// <see cref="System.Reflection.Metadata"/>, rather than via reflection over loaded types, so a
/// literal buried in a private method body (a log message, an exception string) is caught too,
/// not only public API surface.
/// </summary>
public sealed class CoreNamesNoPackageTests
{
    /// <summary>
    /// Terms rule A1 names explicitly as examples of what the core must never contain, plus the
    /// concrete package/provider/OS identifiers this repository actually ships as of V0.6. Not
    /// exhaustive by construction — a new package's own identifier should be added here when it
    /// ships — but every entry here is a real one this codebase would otherwise be able to leak.
    /// </summary>
    private static readonly string[] ForbiddenTerms =
    [
        "docker.restart", "docker.stop", "docker.start", "docker.logs", "docker.inspect",
        "docker.containers", "docker.images", "docker.networks",
        "docker.image.inspect", "docker.image.pull", "docker.image.tag", "docker.image.remove", "docker.build",
        "docker.volumes", "docker.volume.inspect", "docker.volume.create", "docker.volume.remove",
        "service.status", "service.restart", "service.start", "service.stop",
        "/proc", "/sys", "systemctl", "servicecontroller", "performancecounter",
        "openrouter", "ollama", "llamacpp", "llama.cpp", "anthropic", "deepseek",
        "fs.write", "fs.delete", "fs.read", "fs.list", "fs.stat",
        "network.ping", "network.dns", "network.interfaces", "network.connections",
        "process.list", "process.inspect", "process.stop", "process.kill",
        "process.metrics", "process.tree", "process.modules",
        "system.events", "system.apps", "system.devices",
    ];

    public static TheoryData<string, Type> CoreAssemblyMarkers => new()
    {
        { "bOps.Runtime", typeof(Runtime.AgentRunner) },
        { "bOps.Policy", typeof(Policy.PolicyEngine) },
        { "bOps.Audit", typeof(Audit.JsonLinesAuditSink) },
        { "bOps.Memory", typeof(Memory.SqliteTaskStore) },
    };

    [Theory]
    [MemberData(nameof(CoreAssemblyMarkers))]
    public void CoreAssembly_ContainsNoPackageSpecificStringLiterals(string assemblyName, Type marker)
    {
        ArgumentNullException.ThrowIfNull(marker);

        var offending = FindForbiddenStringLiterals(marker.Assembly.Location).ToList();

        Assert.True(offending.Count == 0,
            $"{assemblyName} (rule A1 — the core never names a package, tool or provider) contains:\n{string.Join('\n', offending)}");
    }

    private static IEnumerable<string> FindForbiddenStringLiterals(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();

        foreach (var typeHandle in metadataReader.TypeDefinitions)
        {
            var type = metadataReader.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadataReader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue; // abstract, extern, or otherwise bodiless
                }

                var body = peReader.GetMethodBody(method.RelativeVirtualAddress);
                foreach (var literal in ExtractStringLiterals(body.GetILContent(), metadataReader))
                {
                    foreach (var term in ForbiddenTerms)
                    {
                        if (literal.Contains(term, StringComparison.OrdinalIgnoreCase))
                        {
                            yield return $"  \"{term}\" found inside literal: \"{Truncate(literal)}\"";
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Scans raw IL for every <c>ldstr</c> instruction (opcode <c>0x72</c>) and resolves its
    /// 4-byte User String token. There is no metadata table listing string literals directly —
    /// they only exist as operands to <c>ldstr</c> in method bodies — so walking the IL is the
    /// standard way to enumerate them. Deliberately not a full IL disassembler: it does not track
    /// other opcodes' operand lengths, so a byte sequence that only coincidentally matches
    /// <c>0x72</c> followed by a well-formed User String token (top byte <c>0x70</c>) could, in
    /// principle, be misread — resolving it is wrapped defensively and any failure is simply
    /// skipped, which only risks under-reporting a string this test would already flag by its
    /// correctly aligned counterpart appearing elsewhere, never a false failure.
    /// </summary>
    private static IEnumerable<string> ExtractStringLiterals(ImmutableArray<byte> il, MetadataReader metadataReader)
    {
        for (var i = 0; i + 5 <= il.Length; i++)
        {
            if (il[i] != 0x72)
            {
                continue;
            }

            var token = BinaryPrimitives.ReadUInt32LittleEndian(il.AsSpan(i + 1, 4));
            if ((token & 0xFF000000) != 0x70000000)
            {
                continue;
            }

            string? value = null;
            try
            {
                value = metadataReader.GetUserString(MetadataTokens.UserStringHandle((int)(token & 0x00FFFFFF)));
            }
            catch (BadImageFormatException)
            {
            }

            if (value is not null)
            {
                yield return value;
            }
        }
    }

    private static string Truncate(string value) => value.Length <= 120 ? value : string.Concat(value.AsSpan(0, 120), "...");
}
