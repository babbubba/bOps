// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace bOps.Packages.Filesystem;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct RmProcessInfo
{
    public RmUniqueProcess Process;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string ApplicationName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string ServiceShortName;

    public uint ApplicationType;
    public uint AppStatus;
    public uint TerminalSessionId;

    [MarshalAs(UnmanagedType.Bool)]
    public bool Restartable;
}
