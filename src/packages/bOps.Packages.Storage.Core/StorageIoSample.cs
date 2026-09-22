// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
namespace bOps.Packages.Storage.Core;
public sealed record StorageIoSample(string Device, long Reads, long Writes, long ReadBytes, long WriteBytes, long ReadTimeMilliseconds, long WriteTimeMilliseconds, long WeightedIoTimeMilliseconds, long BusyTimeMilliseconds, long InFlight);
