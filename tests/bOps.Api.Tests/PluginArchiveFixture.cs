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
    private const string CheckType = "Acme.ApiPlugin.ServiceCheck";

    /// <summary>The prerequisite the emitted plugin contributes when built <c>withCheck</c>; it always reports <c>Available</c>.</summary>
    internal const string ContributedPrerequisite = "acme.api-service";

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
    internal byte[] Build(string version, string markerPath, bool throwOnActivate = false, string id = PluginId, bool sign = true, string? notes = null, bool withCheck = false)
    {
        var directory = Directory.CreateTempSubdirectory("bops-api-plugin-source-").FullName;
        try
        {
            EmitAssembly(Path.Combine(directory, AssemblyFileName), markerPath, throwOnActivate, withCheck);
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

    private static void EmitAssembly(string path, string markerPath, bool throwOnActivate, bool withCheck)
    {
        var builder = new PersistedAssemblyBuilder(new AssemblyName("Acme.ApiPlugin") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = builder.DefineDynamicModule("Acme.ApiPlugin");
        var checkType = withCheck ? EmitCheck(module) : null;
        var type = module.DefineType(
            EntryType,
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
            typeof(object),
            withCheck ? [typeof(IToolProvider), typeof(IPrerequisiteProvider)] : [typeof(IToolProvider)]);

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

        if (checkType is not null)
        {
            var getChecks = type.DefineMethod(
                nameof(IPrerequisiteProvider.GetPrerequisiteChecks),
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                typeof(IReadOnlyList<IPrerequisiteCheck>),
                Type.EmptyTypes);
            var checks = getChecks.GetILGenerator();
            checks.Emit(OpCodes.Ldc_I4_1);
            checks.Emit(OpCodes.Newarr, typeof(IPrerequisiteCheck));
            checks.Emit(OpCodes.Dup);
            checks.Emit(OpCodes.Ldc_I4_0);
            checks.Emit(OpCodes.Newobj, checkType.DefineDefaultConstructor(MethodAttributes.Public));
            checks.Emit(OpCodes.Stelem_Ref);
            checks.Emit(OpCodes.Ret);
            type.DefineMethodOverride(getChecks, typeof(IPrerequisiteProvider).GetMethod(nameof(IPrerequisiteProvider.GetPrerequisiteChecks))!);
        }

        checkType?.CreateType();
        type.CreateType();
        builder.Save(path);
    }

    /// <summary>A read-only check that is always Available, contributed under <see cref="ContributedPrerequisite"/>.</summary>
    private static TypeBuilder EmitCheck(ModuleBuilder module)
    {
        var type = module.DefineType(CheckType, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, typeof(object), [typeof(IPrerequisiteCheck)]);

        var descriptor = type.DefineMethod(
            "get_" + nameof(IPrerequisiteCheck.Descriptor),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.SpecialName,
            typeof(PrerequisiteDescriptor),
            Type.EmptyTypes);
        var getter = descriptor.GetILGenerator();
        getter.Emit(OpCodes.Ldstr, ContributedPrerequisite);
        getter.Emit(OpCodes.Ldstr, "Plugin service");
        getter.Emit(OpCodes.Ldstr, "A service the API test plugin provides.");
        getter.Emit(OpCodes.Ldc_I4, (int)PrerequisiteKind.Service);
        getter.Emit(OpCodes.Newobj, typeof(PrerequisiteDescriptor).GetConstructor([typeof(string), typeof(string), typeof(string), typeof(PrerequisiteKind)])!);
        getter.Emit(OpCodes.Ret);
        type.DefineMethodOverride(descriptor, typeof(IPrerequisiteCheck).GetProperty(nameof(IPrerequisiteCheck.Descriptor))!.GetMethod!);

        var check = type.DefineMethod(
            nameof(IPrerequisiteCheck.CheckAsync),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            typeof(Task<PrerequisiteCheckOutcome>),
            [typeof(CancellationToken)]);
        var body = check.GetILGenerator();
        body.Emit(OpCodes.Ldc_I4, (int)PrerequisiteState.Available);
        body.Emit(OpCodes.Ldstr, "available");
        body.Emit(OpCodes.Ldstr, "The plugin service is reachable.");
        body.Emit(OpCodes.Newobj, typeof(PrerequisiteCheckOutcome).GetConstructor([typeof(PrerequisiteState), typeof(string), typeof(string)])!);
        body.Emit(OpCodes.Call, typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(typeof(PrerequisiteCheckOutcome)));
        body.Emit(OpCodes.Ret);
        type.DefineMethodOverride(check, typeof(IPrerequisiteCheck).GetMethod(nameof(IPrerequisiteCheck.CheckAsync))!);
        return type;
    }

    public void Dispose() => _key.Dispose();
}
