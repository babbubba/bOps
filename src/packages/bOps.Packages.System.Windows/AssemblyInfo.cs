// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

// Declares platform intent explicitly (this assembly only runs on Windows) so the
// platform-compatibility analyzer treats every Win32 call in it as valid, without needing a
// Windows-specific TargetFramework — see the note in the .csproj.
[assembly: SupportedOSPlatform("windows")]
[assembly: InternalsVisibleTo("bOps.Packages.System.Windows.Tests")]
