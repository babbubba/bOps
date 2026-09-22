// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace bOps.Packages.Storage.Linux;

internal static partial class LinuxStatVfs
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Buffer
    {
        internal ulong BlockSize;
        internal ulong FragmentSize;
        internal ulong Blocks;
        internal ulong BlocksFree;
        internal ulong BlocksAvailable;
        internal ulong Files;
        internal ulong FilesFree;
        internal ulong FilesAvailable;
        internal ulong FileSystemId;
        internal ulong Flags;
        internal ulong NameMaximum;
        private readonly int spare0;
        private readonly int spare1;
        private readonly int spare2;
        private readonly int spare3;
        private readonly int spare4;
        private readonly int spare5;
    }

    [LibraryImport("libc", EntryPoint = "statvfs", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int Read(string path, out Buffer buffer);
}
