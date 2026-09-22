// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
namespace bOps.Packages.Storage.Core;
public sealed record StorageIoResult(string Device, double ReadsPerSec, double WritesPerSec, double ReadBytesPerSec, double WriteBytesPerSec, double? ReadLatencyMs, double? WriteLatencyMs, double? QueueDepth, double? UtilizationPercent);
