// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Filesystem;

/// <summary>Contributes every <c>fs.*</c> tool, sharing one path policy and inventory service.</summary>
public sealed class FilesystemToolProvider : IToolProvider
{
    private readonly FilesystemPathPolicy _pathPolicy;
    private readonly FilesystemInventoryOptions _inventoryOptions;
    private readonly FilesystemInventoryService _inventory;

    public FilesystemToolProvider(FilesystemPathPolicy pathPolicy)
        : this(pathPolicy, new FilesystemInventoryOptions())
    {
    }

    public FilesystemToolProvider(FilesystemPathPolicy pathPolicy, FilesystemInventoryOptions inventoryOptions)
    {
        _pathPolicy = pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));
        _inventoryOptions = inventoryOptions ?? throw new ArgumentNullException(nameof(inventoryOptions));
        _inventoryOptions.Validate();
        var store = new SqliteFilesystemManifestStore(_inventoryOptions.ManifestStorePath);
        _inventory = new FilesystemInventoryService(_pathPolicy, _inventoryOptions, store);
    }

    public IEnumerable<ITool> GetTools() =>
    [
        new FsListTool(_pathPolicy),
        new FsReadTool(_pathPolicy),
        new FsStatTool(_pathPolicy),
        new FsWriteTool(_pathPolicy),
        new FsDeleteTool(_pathPolicy),
        new FsSearchTool(_pathPolicy),
        new FsHashTool(_pathPolicy),
        new FsMoveTool(_pathPolicy),
        new FsSizeTool(_inventory, _inventoryOptions),
    ];
}
