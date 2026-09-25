// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO.Compression;
using bOps.Abstractions;
using bOps.PluginHost;

namespace bOps.Cli;

/// <summary>
/// <c>bops plugin</c> (ADR-0020, ADR-0037). Every lifecycle mutation goes through the one
/// <see cref="PluginLifecycleService"/> backend — recovery, transition legality, activation
/// confirmation, revision (ETag) preconditions, per-plugin serialization and audit — exactly like the API.
/// <see cref="PluginManager"/> stays the low-level loader the service composes; this command never calls
/// its <c>Install</c>/<c>Enable</c>/<c>Disable</c>/<c>Remove</c> as an independent state machine.
/// </summary>
internal sealed class PluginCommand(
    PluginLifecycleService lifecycle,
    PluginManager manager,
    string trustStorePath,
    TextWriter output,
    TextWriter error,
    ActorIdentity actor)
{
    public const string Usage = """
        Usage: bops plugin install <directory|archive.zip> [--expected-version <n>] [--idempotency-key <key>]
               bops plugin list
               bops plugin enable <id> --confirm-version <version> [--expected-version <n>] [--idempotency-key <key>]
               bops plugin disable <id> [--expected-version <n>] [--idempotency-key <key>]
               bops plugin remove <id>                (not available: the lifecycle defines no removal transaction)
               bops plugin validate <directory>
               bops plugin sign <directory> <publisher> <key-id> <private-key-pem-file>
        """;

    /// <summary>Runs the command line that follows <c>bops plugin</c>. Returns the process exit code.</summary>
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0)
        {
            await error.WriteLineAsync(Usage);
            return 1;
        }

        try
        {
            var command = args[0].ToLowerInvariant();
            var rest = args[1..];
            switch (command)
            {
                case "validate" when rest.Length == 1:
                    return await ValidateAsync(rest[0]);

                case "sign" when rest.Length == 4:
                    var privateKeyPem = await File.ReadAllTextAsync(rest[3], cancellationToken);
                    PluginPackageSignature.Sign(rest[0], rest[1], rest[2], privateKeyPem);
                    await output.WriteLineAsync($"Signed plugin package '{rest[0]}' as publisher '{rest[1]}' with key '{rest[2]}'.");
                    return 0;

                case "install" or "enable" or "disable" or "remove" or "list":
                    if (!TryParse(command, rest, out var options, out var problem))
                    {
                        await error.WriteLineAsync(problem);
                        await error.WriteLineAsync(Usage);
                        return 1;
                    }

                    // Never act on an interrupted journal or a stale in-memory picture: reconcile first (ADR-0037).
                    await lifecycle.RecoverAllAsync(cancellationToken);
                    return command switch
                    {
                        "list" => await ListAsync(),
                        "install" => await InstallAsync(options, cancellationToken),
                        "enable" => await EnableAsync(options, cancellationToken),
                        "disable" => await DisableAsync(options, cancellationToken),
                        _ => await RemoveAsync(),
                    };

                default:
                    await error.WriteLineAsync(Usage);
                    return 1;
            }
        }
        catch (PluginValidationException ex)
        {
            await error.WriteLineAsync($"Validation failed: {ex.Message}");
            return 1;
        }
        catch (PluginOperationException ex)
        {
            await error.WriteLineAsync($"Operation failed: {ex.Message}");
            return 1;
        }
    }

    private sealed record Options(string? Target, long? ExpectedVersion, string? ConfirmVersion, string? IdempotencyKey);

    private static bool TryParse(string command, string[] rest, out Options options, out string problem)
    {
        options = new Options(null, null, null, null);
        problem = string.Empty;
        string? target = null;
        long? expected = null;
        string? confirm = null;
        string? key = null;
        for (var index = 0; index < rest.Length; index++)
        {
            var argument = rest[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (target is not null)
                {
                    problem = $"Unexpected argument '{argument}'.";
                    return false;
                }

                target = argument;
                continue;
            }

            if (index + 1 >= rest.Length)
            {
                problem = $"Option '{argument}' needs a value.";
                return false;
            }

            var value = rest[++index];
            switch (argument)
            {
                case "--expected-version" when command is "install" or "enable" or "disable":
                    if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                    {
                        problem = "--expected-version must be a non-negative lifecycle revision (see 'bops plugin list').";
                        return false;
                    }

                    expected = parsed;
                    break;
                case "--confirm-version" when command == "enable":
                    confirm = value;
                    break;
                case "--idempotency-key" when command is "install" or "enable" or "disable":
                    key = value;
                    break;
                default:
                    problem = $"Unknown option '{argument}'.";
                    return false;
            }
        }

        var needsTarget = command != "list";
        if (needsTarget != (target is not null))
        {
            problem = needsTarget ? $"'{command}' needs a target." : "'list' takes no arguments.";
            return false;
        }

        options = new Options(target, expected, confirm, key);
        return true;
    }

    private PluginLifecycleRequestContext Context(Options options) =>
        new(NodeId.Local, actor, options.IdempotencyKey, Guid.NewGuid().ToString("N"));

    private async Task<int> ListAsync()
    {
        foreach (var record in manager.List())
        {
            var status = lifecycle.GetStatus(record.Id);
            await output.WriteLineAsync(
                $"{record.Id}\t{(record.Enabled ? "enabled" : "disabled")}\tv{record.Manifest.Version}\t{record.Manifest.Publisher}\t" +
                $"{record.Provenance?.Trust ?? PackageTrustLevel.Unverified}\t{status?.State}\trevision {status?.LifecycleVersion}");
        }

        return 0;
    }

    private async Task<int> InstallAsync(Options options, CancellationToken cancellationToken)
    {
        var source = options.Target!;
        string? temporaryArchive = null;
        try
        {
            Stream archive;
            if (Directory.Exists(source))
            {
                // A directory is packaged into the same bounded ZIP the lifecycle backend admits for every host.
                temporaryArchive = Path.Combine(Path.GetTempPath(), $"bops-plugin-{Guid.NewGuid():N}.zip");
                await ZipFile.CreateFromDirectoryAsync(source, temporaryArchive, CompressionLevel.Optimal, includeBaseDirectory: false, cancellationToken);
                archive = new FileStream(temporaryArchive, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            else if (File.Exists(source))
            {
                archive = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            else
            {
                await error.WriteLineAsync("Install refused: the source directory or archive does not exist.");
                return 1;
            }

            await using (archive)
            {
                var result = await lifecycle.InstallArchiveAsync(Context(options), archive, options.ExpectedVersion, cancellationToken: cancellationToken);
                return await ReportAsync("Installed", result);
            }
        }
        finally
        {
            if (temporaryArchive is not null)
            {
                TryDeleteFile(temporaryArchive);
            }
        }
    }

    private async Task<int> EnableAsync(Options options, CancellationToken cancellationToken)
    {
        var id = options.Target!;
        var result = await lifecycle.EnableAsync(Context(options), id, ExpectedFor(id, options), options.ConfirmVersion, cancellationToken);
        return await ReportAsync("Enabled", result);
    }

    private async Task<int> DisableAsync(Options options, CancellationToken cancellationToken)
    {
        var id = options.Target!;
        var result = await lifecycle.DisableAsync(Context(options), id, ExpectedFor(id, options), cancellationToken);
        return await ReportAsync("Disabled", result);
    }

    /// <summary>The operator's revision when given; otherwise the revision read after this command's own recovery pass.</summary>
    private long? ExpectedFor(string id, Options options) => options.ExpectedVersion ?? lifecycle.GetStatus(id)?.LifecycleVersion;

    private async Task<int> RemoveAsync()
    {
        // ADR-0037 defines no remove/delete transaction. Deleting Current/LKG/rollback material without one could
        // leave the lifecycle metadata naming generations that no longer exist, so this fails closed.
        await error.WriteLineAsync(
            "Remove refused: plugin removal is not available because the lifecycle service defines no removal transaction. No plugin material was changed.");
        return 1;
    }

    private async Task<int> ReportAsync(string verb, PluginLifecycleResult result)
    {
        if (result.Succeeded)
        {
            await output.WriteLineAsync(
                $"{verb} '{result.PluginId}' v{result.PluginVersion} ({result.State}, revision {result.LifecycleVersion}{(result.Replayed ? ", replayed" : string.Empty)}).");
            return 0;
        }

        await error.WriteLineAsync(
            $"Refused: {result.Category}{(result.Stage is null ? string.Empty : $" at stage '{result.Stage}'")}" +
            $"{(result.PluginId is null ? string.Empty : $" for '{result.PluginId}'")}" +
            $"{(result.State is null ? string.Empty : $" (state {result.State}, revision {result.LifecycleVersion})")}.");
        switch (result.Category)
        {
            case PluginLifecycleResultCategory.ActivationConfirmationRequired:
                await error.WriteLineAsync(
                    $"Enabling executes the plugin in-process with host privileges. Confirm the exact version explicitly with --confirm-version {result.PluginVersion}.");
                break;
            case PluginLifecycleResultCategory.StaleVersion:
                await error.WriteLineAsync("The lifecycle revision does not match; check 'bops plugin list' and pass the current --expected-version.");
                break;
            case PluginLifecycleResultCategory.RecoveryRequired:
            case PluginLifecycleResultCategory.StateConflict when result.State == PluginLifecycleState.RecoveryRequired:
                await error.WriteLineAsync("The plugin needs administrator recovery (restore its activation last-known-good); it was not activated.");
                break;
            default:
                break;
        }

        return 1;
    }

    private async Task<int> ValidateAsync(string directory)
    {
        var manifest = PluginManifestValidator.ReadManifest(directory);
        PluginManifestValidator.Validate(manifest, directory);
        var verified = PluginPackageSignature.Verify(directory, manifest, new PluginPublisherTrustStore(trustStorePath));
        await output.WriteLineAsync($"'{directory}' is a valid manifest for '{manifest.Id}' v{manifest.Version}; provenance: " +
            $"{(verified.Verified ? $"verified ({verified.Trust})" : $"unverified ({verified.FailureReason})")}.");
        return 0;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temporary archive that cannot be removed is not an install failure.
        }
    }
}
