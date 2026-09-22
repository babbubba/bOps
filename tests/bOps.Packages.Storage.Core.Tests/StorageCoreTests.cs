// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Storage.Core.Tests;

public sealed class StorageCoreTests
{
    [Fact]
    public void ListArguments_ApplyDocumentedDefaults()
    {
        Assert.True(StorageArguments.TryReadList(ToolArguments.Empty, null, out _, out var limit, out var bytes, out _));
        Assert.Equal(StorageLimits.DefaultRows, limit);
        Assert.Equal(StorageLimits.DefaultOutputBytes, bytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(StorageLimits.MaximumSampleMilliseconds + 1)]
    public void IoArguments_RejectOutOfRangeSamples(int milliseconds)
    {
        var arguments = ToolArguments.FromJson(new JsonObject { ["sampleMilliseconds"] = milliseconds });
        Assert.False(StorageArguments.TryReadIo(arguments, out _, out _, out _, out _));
    }

    [Fact]
    public void ComputeIo_ProducesNonNegativeRatesAndLatency()
    {
        var before = new StorageIoSample("/dev/sda", 10, 20, 1_000, 2_000, 50, 80, 100, 200, 0);
        var after = new StorageIoSample("/dev/sda", 14, 22, 5_000, 6_000, 70, 100, 140, 400, 1);
        var result = Assert.Single(StorageCalculations.ComputeIo([before], [after], 2));
        Assert.Equal(2, result.ReadsPerSec);
        Assert.Equal(1, result.WritesPerSec);
        Assert.Equal(5, result.ReadLatencyMs);
        Assert.Equal(10, result.WriteLatencyMs);
        Assert.InRange(result.UtilizationPercent!.Value, 0, 100);
    }

    [Fact]
    public void ComputeIo_ClampsCounterResetInsteadOfProducingNegativeRates()
    {
        var before = new StorageIoSample("disk", 10, 10, 10, 10, 10, 10, 10, 10, 0);
        var after = new StorageIoSample("disk", 1, 1, 1, 1, 1, 1, 1, 1, 0);
        var result = Assert.Single(StorageCalculations.ComputeIo([before], [after], 1));
        Assert.Equal(0, result.ReadsPerSec);
        Assert.Equal(0, result.WritesPerSec);
    }

    [Fact]
    public void Formatting_HonorsUtf8OutputBound()
    {
        var rows = Enumerable.Range(0, 2_000).Select(i => new StorageDisk($"disk-{i}", new string('x', 256), null, null, null, null, 1, null, null, null, false, false)).ToArray();
        var output = StorageFormatting.Disks(rows, rows.Length, StorageLimits.MinimumOutputBytes);
        Assert.True(Encoding.UTF8.GetByteCount(output) <= StorageLimits.MinimumOutputBytes);
        using var document = JsonDocument.Parse(output);
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task IoTool_HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CancellingIoTool().ExecuteAsync(ToolArguments.Empty, cancellation.Token));
    }

    private sealed class CancellingIoTool : StorageIoToolBase
    {
        public CancellingIoTool() : base("test") { }
        protected override Task<IReadOnlyList<StorageIoSample>> SampleAsync(string? device, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<StorageIoSample>>([]);
        }
    }
}
