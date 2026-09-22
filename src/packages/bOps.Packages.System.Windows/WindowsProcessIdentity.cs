// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Windows;

/// <summary>
/// What <c>Win32_Process</c> knows about one process that <see cref="System.Diagnostics.Process"/>
/// does not: its parent, the image it was loaded from and the command line it was started with.
/// A property the current identity may not read is <c>null</c>.
/// </summary>
internal sealed record WindowsProcessIdentity(
    int Pid,
    int? ParentPid,
    string? Name,
    string? ExecutablePath,
    string? CommandLine);
