// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Storage.Linux;

internal sealed record LinuxMountInfo(
    string Source,
    string MountPoint,
    string FileSystemType,
    string Options,
    bool ReadOnly);
