// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using bOps.ReplayFixtureCapture;

// Same culture pinning as bOps.Cli (bOps.Hosting.HostCulture.PinInvariant), so the collectors run under the culture of the earlier captures.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

if (!OperatingSystem.IsWindows())
{
    await Console.Error.WriteLineAsync("This harness captures the Windows collectors; it runs on Windows only.");
    return 2;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

var app = new CliApp(
    Console.Out, Console.Error, new RealEnvironmentProbe(), new RealManifestSource(),
    () => new RealCollectorSource(), ReadOnlyPolicy.Create(), TimeProvider.System,
    () => Guid.NewGuid().ToString("N")[..4],
    typeof(CliApp).Assembly.Location);
return await app.RunAsync(args, cancel.Token);

namespace bOps.ReplayFixtureCapture
{
    /// <summary>Anchor type for the assembly, so the environment probe can hash this binary.</summary>
    internal sealed partial class Program;
}
