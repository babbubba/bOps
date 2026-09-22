// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace bOps.Packages.Filesystem;

[StructLayout(LayoutKind.Sequential)]
internal struct RmUniqueProcess
{
    public int ProcessId;
    public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
}
