// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Filesystem;

/// <summary>Contributes every <c>fs.*</c> tool, all sharing one <see cref="FilesystemPathPolicy"/> instance.</summary>
public sealed class FilesystemToolProvider(FilesystemPathPolicy pathPolicy) : IToolProvider
{
    public IEnumerable<ITool> GetTools() =>
    [
        new FsListTool(pathPolicy),
        new FsReadTool(pathPolicy),
        new FsStatTool(pathPolicy),
        new FsWriteTool(pathPolicy),
        new FsDeleteTool(pathPolicy),
        new FsSearchTool(pathPolicy),
        new FsHashTool(pathPolicy),
    ];
}
