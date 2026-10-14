// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace bOps.Hosting;

/// <summary>
/// Keeps a host's culture-dependent behaviour exactly what it was under invariant globalization. Hosts that carry Knowledge discovery
/// run with full globalization so Unicode normalization (NFC/NFKC) is real (ADR-0057 section U.5); that would also switch the
/// process to the machine culture, which the rest of bOps never evaluated. Pinning the invariant culture makes number, date and
/// comparison behaviour independent of the machine again, and leaves only normalization as the capability the host gained.
/// </summary>
public static class HostCulture
{
    /// <summary>Pins the invariant culture for the current thread and for every thread created afterwards. Call first in <c>Main</c>.</summary>
    public static void PinInvariant()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }
}
