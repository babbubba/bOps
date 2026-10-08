// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// The node-local, host-owned prerequisite registry (ADR-0049 section 4). Packages contribute read-only checks; this registry
/// owns their registration, execution under a bounded timeout, the last result of each, and the boolean
/// <see cref="ICapabilityProbe"/> compatibility view (<c>Available</c>/<c>Degraded</c> → <c>true</c>; everything else,
/// unregistered ids included, → <c>false</c>). It names no package: ids are data supplied by packages and the host.
/// </summary>
public sealed class PrerequisiteRegistry : ICapabilityProbe
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RegisteredCheck> _checks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PrerequisiteCheckResult> _results = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cacheDuration;

    /// <summary>Creates an empty registry.</summary>
    /// <param name="timeProvider">Stamps results and drives check timeouts.</param>
    /// <param name="cacheDuration">How long <see cref="IsAvailableAsync"/> reuses a result before checking again.</param>
    public PrerequisiteRegistry(TimeProvider timeProvider, TimeSpan cacheDuration)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(cacheDuration, TimeSpan.Zero);

        _timeProvider = timeProvider;
        _cacheDuration = cacheDuration;
    }

    /// <summary>Registers one check under the host-assigned <paramref name="package"/>. An id can be registered by one check only.</summary>
    /// <exception cref="PrerequisiteRegistrationException">The descriptor is invalid or its id is already registered.</exception>
    public void Register(PackageId package, IPrerequisiteCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        Register(package, [check]);
    }

    /// <summary>
    /// Registers every check a package's <see cref="IPrerequisiteProvider"/> contributes, atomically: either all of them or,
    /// on any invalid, duplicate or throwing contribution, none.
    /// </summary>
    /// <exception cref="PrerequisiteRegistrationException">A contribution is invalid or duplicated, or the provider threw.</exception>
    public void Register(PackageId package, IPrerequisiteProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        IReadOnlyList<IPrerequisiteCheck> checks;
        try
        {
            checks = provider.GetPrerequisiteChecks()
                ?? throw new PrerequisiteRegistrationException($"Package '{package.Value}' returned a null prerequisite check collection.");
        }
        catch (PrerequisiteRegistrationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PrerequisiteRegistrationException($"Package '{package.Value}' threw while declaring its prerequisite checks.", ex);
        }

        Register(package, checks.ToArray());
    }

    /// <summary>Removes every check contributed by a package, and its last results, before the package is unloaded.</summary>
    public void Unregister(PackageId package)
    {
        lock (_gate)
        {
            foreach (var id in _checks.Where(pair => pair.Value.Package == package).Select(pair => pair.Key).ToArray())
            {
                _checks.Remove(id);
                _results.TryRemove(id, out _);
            }
        }
    }

    /// <summary>Every registered prerequisite, ordered by id.</summary>
    public IReadOnlyList<PrerequisiteRegistration> GetRegistrations()
    {
        lock (_gate)
        {
            return _checks.Values
                .Select(registered => new PrerequisiteRegistration(registered.Check.Descriptor, registered.Package))
                .OrderBy(registration => registration.Descriptor.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <summary>The last recorded result of a prerequisite, or <c>null</c> if it is unregistered or was never checked.</summary>
    public PrerequisiteCheckResult? GetLastResult(string prerequisiteId) =>
        _results.TryGetValue(prerequisiteId, out var result) ? result : null;

    /// <summary>The last recorded state of a prerequisite; <see cref="PrerequisiteState.Unknown"/> if unregistered or never checked.</summary>
    public PrerequisiteState GetState(string prerequisiteId) =>
        GetLastResult(prerequisiteId)?.State ?? PrerequisiteState.Unknown;

    /// <summary>
    /// Runs one prerequisite's check now, records and returns its result. An unregistered id yields an unrecorded
    /// <see cref="PrerequisiteState.Unknown"/> result. Caller cancellation propagates; every other failure is a result.
    /// </summary>
    public async Task<PrerequisiteCheckResult> CheckAsync(string prerequisiteId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prerequisiteId);

        RegisteredCheck? registered;
        lock (_gate)
        {
            _checks.TryGetValue(prerequisiteId, out registered);
        }

        if (registered is null)
        {
            return new PrerequisiteCheckResult
            {
                Id = prerequisiteId,
                State = PrerequisiteState.Unknown,
                Code = PrerequisiteCodes.NotRegistered,
                Message = "No check is registered for this prerequisite.",
                CheckedAtUtc = _timeProvider.GetUtcNow(),
            };
        }

        var result = await RunAsync(registered.Check, ct);

        lock (_gate)
        {
            // A check unregistered while it ran must not leave a result behind.
            if (_checks.TryGetValue(prerequisiteId, out var current) && ReferenceEquals(current, registered))
            {
                _results[registered.Check.Descriptor.Id] = result;
            }
        }

        return result;
    }

    /// <summary>Runs every registered check now, concurrently, and returns their results ordered by id.</summary>
    public async Task<IReadOnlyList<PrerequisiteCheckResult>> RefreshAsync(CancellationToken ct = default)
    {
        string[] ids;
        lock (_gate)
        {
            ids = _checks.Keys.Order(StringComparer.Ordinal).ToArray();
        }

        return await Task.WhenAll(ids.Select(id => CheckAsync(id, ct)));
    }

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);

        if (GetLastResult(capability) is { } cached && _timeProvider.GetUtcNow() - cached.CheckedAtUtc < _cacheDuration)
        {
            return cached.IsSatisfied;
        }

        return (await CheckAsync(capability, ct)).IsSatisfied;
    }

    private void Register(PackageId package, IReadOnlyList<IPrerequisiteCheck> checks)
    {
        if (string.IsNullOrWhiteSpace(package.Value))
        {
            throw new PrerequisiteRegistrationException("The host-assigned package id cannot be blank.");
        }

        var localIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var check in checks)
        {
            if (check is null)
            {
                throw new PrerequisiteRegistrationException($"Package '{package.Value}' contributed a null prerequisite check.");
            }

            var id = Validate(package, check);
            if (!localIds.Add(id))
            {
                throw new PrerequisiteRegistrationException($"Package '{package.Value}' contributes prerequisite '{id}' more than once.");
            }
        }

        lock (_gate)
        {
            foreach (var check in checks)
            {
                if (_checks.TryGetValue(check.Descriptor.Id, out var existing))
                {
                    throw new PrerequisiteRegistrationException(
                        $"Prerequisite '{check.Descriptor.Id}' is already registered by package '{existing.Package.Value}'.");
                }
            }

            foreach (var check in checks)
            {
                _checks.Add(check.Descriptor.Id, new RegisteredCheck(check, package));
            }
        }
    }

    private static string Validate(PackageId package, IPrerequisiteCheck check)
    {
        // A descriptor can be altered with a `with` expression after its constructor validated it, so every bound is re-checked here.
        var descriptor = check.Descriptor
            ?? throw new PrerequisiteRegistrationException($"Package '{package.Value}' contributed a check without a descriptor.");

        if (!OperationalIdentifier.IsValidPrerequisiteId(descriptor.Id))
        {
            throw new PrerequisiteRegistrationException($"Package '{package.Value}' contributed an invalid prerequisite id '{descriptor.Id}'.");
        }

        if (string.IsNullOrWhiteSpace(descriptor.DisplayName)
            || descriptor.DisplayName.Length > PrerequisiteDescriptor.MaxDisplayNameLength
            || string.IsNullOrWhiteSpace(descriptor.Description)
            || descriptor.Description.Length > PrerequisiteDescriptor.MaxTextLength
            || descriptor.Remediation is { Length: > PrerequisiteDescriptor.MaxTextLength }
            || descriptor.Metadata is null
            || !Enum.IsDefined(descriptor.Kind))
        {
            throw new PrerequisiteRegistrationException($"Prerequisite '{descriptor.Id}' has a descriptor field that is blank or out of bounds.");
        }

        return descriptor.Id;
    }

    private async Task<PrerequisiteCheckResult> RunAsync(IPrerequisiteCheck check, CancellationToken ct)
    {
        var descriptor = check.Descriptor;
        var timeout = Clamp(descriptor.CheckTimeout);

        PrerequisiteCheckOutcome outcome;
        using (var deadline = new CancellationTokenSource(timeout, _timeProvider))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token))
        {
            try
            {
                // WaitAsync bounds a check that ignores its token; the abandoned task can only observe, by contract.
                outcome = await check.CheckAsync(linked.Token).WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                outcome = new PrerequisiteCheckOutcome(
                    PrerequisiteState.Error,
                    PrerequisiteCodes.CheckTimeout,
                    $"The prerequisite check did not finish within {timeout.TotalSeconds:0} seconds.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Exception text routinely carries connection strings or paths with credentials; only its type is kept.
                outcome = new PrerequisiteCheckOutcome(PrerequisiteState.Error, PrerequisiteCodes.CheckFailed, "The prerequisite check failed unexpectedly.")
                {
                    Metadata = OperationalMetadata.From(new() { ["exceptionType"] = ex.GetType().FullName }),
                };
            }
        }

        if (!IsValid(outcome))
        {
            outcome = new PrerequisiteCheckOutcome(
                PrerequisiteState.Error, PrerequisiteCodes.CheckInvalidResult, "The prerequisite check returned an invalid result.");
        }

        return new PrerequisiteCheckResult
        {
            Id = descriptor.Id,
            State = outcome.State,
            Code = outcome.Code,
            Message = outcome.Message,
            CheckedAtUtc = _timeProvider.GetUtcNow(),
            Metadata = outcome.Metadata,
        };
    }

    private static bool IsValid(PrerequisiteCheckOutcome? outcome) =>
        outcome is not null
        && outcome.State is not PrerequisiteState.Unknown
        && Enum.IsDefined(outcome.State)
        && OperationalIdentifier.IsValidCode(outcome.Code)
        && !string.IsNullOrWhiteSpace(outcome.Message)
        && outcome.Message.Length <= PrerequisiteCheckOutcome.MaxMessageLength
        && outcome.Metadata is not null;

    private static TimeSpan Clamp(TimeSpan timeout) =>
        timeout < PrerequisiteDescriptor.MinCheckTimeout ? PrerequisiteDescriptor.MinCheckTimeout
        : timeout > PrerequisiteDescriptor.MaxCheckTimeout ? PrerequisiteDescriptor.MaxCheckTimeout
        : timeout;

    private sealed record RegisteredCheck(IPrerequisiteCheck Check, PackageId Package);
}
