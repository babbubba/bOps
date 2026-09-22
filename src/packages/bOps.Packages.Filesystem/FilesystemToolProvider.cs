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
    private readonly FilesystemDeletionService _deletion;
    private readonly FilesystemOperationsOptions _operationsOptions;

    public FilesystemToolProvider(FilesystemPathPolicy pathPolicy)
        : this(pathPolicy, new FilesystemInventoryOptions())
    {
    }

    public FilesystemToolProvider(FilesystemPathPolicy pathPolicy, FilesystemInventoryOptions inventoryOptions)
        : this(pathPolicy, inventoryOptions, new FilesystemOperationsOptions())
    {
    }

    public FilesystemToolProvider(
        FilesystemPathPolicy pathPolicy,
        FilesystemInventoryOptions inventoryOptions,
        FilesystemOperationsOptions operationsOptions)
    {
        _pathPolicy = pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));
        _inventoryOptions = inventoryOptions ?? throw new ArgumentNullException(nameof(inventoryOptions));
        _inventoryOptions.Validate();
        _operationsOptions = operationsOptions ?? throw new ArgumentNullException(nameof(operationsOptions));
        _operationsOptions.Validate();
        var store = new SqliteFilesystemManifestStore(_inventoryOptions.ManifestStorePath);
        _inventory = new FilesystemInventoryService(_pathPolicy, _inventoryOptions, store);
        var deletionStore = new SqliteDeletionManifestStore(_inventoryOptions.ManifestStorePath);
        _deletion = new FilesystemDeletionService(
            _pathPolicy, _inventoryOptions, _inventory, store, deletionStore);
    }

    public FilesystemDeletionService DeletionService => _deletion;

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
        new FsDeleteTreePrepareTool(_deletion, _inventoryOptions),
        new FsDeleteTreeVerifyTool(_deletion),
        new FsDeleteTreeTool(_deletion),
        new FsGrepTool(_pathPolicy),
        new FsTailTool(_pathPolicy),
        new FsPermissionsTool(_pathPolicy),
        new FsLocksTool(_pathPolicy),
        new FsCopyVerifyTool(_pathPolicy),
        new FsCopyTool(_pathPolicy, _operationsOptions),
        new FsMkdirTool(_pathPolicy),
    ];
}
