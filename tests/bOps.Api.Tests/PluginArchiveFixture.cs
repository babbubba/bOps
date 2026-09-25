// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.PluginHost;

namespace bOps.Api.Tests;

/// <summary>
/// Builds REAL signed plugin archives for the lifecycle API tests without a project reference to the sample plugin: a tiny managed
/// tool-provider assembly is emitted at test time, its manifest is written, the package is signed through the production
/// <see cref="PluginPackageSignature"/> and zipped. The emitted entry type's constructor appends one byte to a marker file, so a
/// test can prove — from the filesystem, not from an HTTP status — whether plugin code ever executed.
/// </summary>
internal sealed class PluginArchiveFixture : IDisposable
{
    internal const string PluginId = "acme.api-plugin";
    internal const string Publisher = "Acme";
    internal const string KeyId = "test-key";
    private const string AssemblyFileName = "Acme.ApiPlugin.dll";
    private const string EntryType = "Acme.ApiPlugin.Provider";

    private readonly RSA _key = RSA.Create(2048);

    /// <summary>Writes the local publisher trust store the host consults (existing <c>Plugins:TrustStorePath</c> mechanism).</summary>
    internal void WriteTrust(string trustStorePath, PackageTrustLevel level = PackageTrustLevel.Community, bool trusted = true)
    {
        var entries = trusted
            ? new[] { new PluginPublisherTrust(Publisher, KeyId, _key.ExportSubjectPublicKeyInfoPem(), level) }
            : [];
        File.WriteAllText(trustStorePath, JsonSerializer.Serialize(entries));
    }

    /// <summary>A signed archive. Bytes are stable for a given call, so a "same request" can be replayed byte for byte.</summary>
    internal byte[] Build(string version, string markerPath, bool throwOnActivate = false, string id = PluginId, bool sign = true, string? notes = null)
    {
        var directory = Directory.CreateTempSubdirectory("bops-api-plugin-source-").FullName;
        try
        {
            EmitAssembly(Path.Combine(directory, AssemblyFileName), markerPath, throwOnActivate);
            var manifest = new JsonObject
            {
                ["SchemaVersion"] = PluginManifestValidator.SupportedSchemaVersion,
                ["Id"] = id,
                ["Publisher"] = Publisher,
                ["Version"] = version,
                ["MinHostAbstractionsVersion"] = "1.1.0",
                ["EntryAssembly"] = AssemblyFileName,
                ["EntryType"] = EntryType,
                ["DeclaredCapabilities"] = new JsonArray(),
                ["Dependencies"] = new JsonArray(),
                ["MaxDeclaredRisk"] = "Low",
            };
            File.WriteAllText(Path.Combine(directory, "bops-plugin.json"), manifest.ToJsonString());
            if (notes is not null)
            {
                File.WriteAllText(Path.Combine(directory, "notes.txt"), notes);
            }

            if (sign)
            {
                PluginPackageSignature.Sign(directory, Publisher, KeyId, _key.ExportPkcs8PrivateKeyPem());
            }

            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var path in Directory.GetFiles(directory).Order(StringComparer.Ordinal))
                {
                    using var entry = zip.CreateEntry(Path.GetFileName(path)).Open();
                    entry.Write(File.ReadAllBytes(path));
                }
            }

            return stream.ToArray();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void EmitAssembly(string path, string markerPath, bool throwOnActivate)
    {
        var builder = new PersistedAssemblyBuilder(new AssemblyName("Acme.ApiPlugin") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = builder.DefineDynamicModule("Acme.ApiPlugin");
        var type = module.DefineType(EntryType, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, typeof(object), [typeof(IToolProvider)]);

        var constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldstr, markerPath);
        il.Emit(OpCodes.Ldstr, "x");
        il.Emit(OpCodes.Call, typeof(File).GetMethod(nameof(File.AppendAllText), [typeof(string), typeof(string)])!);
        if (throwOnActivate)
        {
            il.Emit(OpCodes.Ldstr, "activation-sentinel-failure");
            il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor([typeof(string)])!);
            il.Emit(OpCodes.Throw);
        }
        else
        {
            il.Emit(OpCodes.Ret);
        }

        var getTools = type.DefineMethod(
            nameof(IToolProvider.GetTools),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            typeof(IEnumerable<ITool>),
            Type.EmptyTypes);
        var body = getTools.GetILGenerator();
        body.Emit(OpCodes.Call, typeof(Array).GetMethod(nameof(Array.Empty))!.MakeGenericMethod(typeof(ITool)));
        body.Emit(OpCodes.Ret);
        type.DefineMethodOverride(getTools, typeof(IToolProvider).GetMethod(nameof(IToolProvider.GetTools))!);

        type.CreateType();
        builder.Save(path);
    }

    public void Dispose() => _key.Dispose();
}
