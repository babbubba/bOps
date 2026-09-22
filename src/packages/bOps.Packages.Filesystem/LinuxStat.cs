// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace bOps.Packages.Filesystem;

[StructLayout(LayoutKind.Sequential)]
internal struct LinuxStat
{
    public ulong Device;
    public ulong Inode;
    public ulong LinkCount;
    public uint Mode;
    public uint UserId;
    public uint GroupId;
    public uint Padding;
    public ulong DeviceId;
    public long Size;
    public long BlockSize;
    public long Blocks;
    public long AccessSeconds;
    public long AccessNanoseconds;
    public long ModificationSeconds;
    public long ModificationNanoseconds;
    public long ChangeSeconds;
    public long ChangeNanoseconds;
    public long Reserved0;
    public long Reserved1;
    public long Reserved2;
}
