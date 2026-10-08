// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>The stable, runtime-authored codes of ADR-0049: check-result codes and system-message codes.</summary>
public static class PrerequisiteCodes
{
    /// <summary>Result code: a boolean check reported the prerequisite present.</summary>
    public const string Available = "available";

    /// <summary>Result code: a boolean check reported the prerequisite absent.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>Result code: the check did not finish within its timeout.</summary>
    public const string CheckTimeout = "check-timeout";

    /// <summary>Result code: the check threw.</summary>
    public const string CheckFailed = "check-failed";

    /// <summary>Result code: the check returned a malformed outcome or the <c>Unknown</c> state.</summary>
    public const string CheckInvalidResult = "check-invalid-result";

    /// <summary>Result code: no check is registered under the requested id.</summary>
    public const string NotRegistered = "not-registered";

    /// <summary>
    /// Result code: the host could not durably record an observation of this prerequisite, so it is treated as <c>Error</c> until a
    /// later observation is recorded (fail-closed; ADR-0049 section 10). Host-authored; a package never produces it.
    /// </summary>
    public const string StateRecordFailed = "state-record-failed";

    /// <summary>Message code: a prerequisite became unavailable, or was first observed unavailable.</summary>
    public const string MessageMissing = "prerequisite.missing";

    /// <summary>Message code: a prerequisite became degraded.</summary>
    public const string MessageDegraded = "prerequisite.degraded";

    /// <summary>Message code: a prerequisite became available again.</summary>
    public const string MessageRecovered = "prerequisite.recovered";

    /// <summary>Message code: a prerequisite check failed.</summary>
    public const string MessageCheckFailed = "prerequisite.check-failed";

    /// <summary>Message code: a component declares a prerequisite that no package registered a check for.</summary>
    public const string MessageNotRegistered = "prerequisite.not-registered";
}
