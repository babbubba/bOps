// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using bOps.Abstractions;

namespace bOps.Packages.Storage.Core;

public static class StorageArguments
{
    public static bool TryReadList(ToolArguments arguments, string? filterName, out string? filter, out int limit, out int maxOutputBytes, out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        filter = null;
        limit = StorageLimits.DefaultRows;
        maxOutputBytes = StorageLimits.DefaultOutputBytes;
        if (filterName is not null && IsPresent(arguments, filterName))
        {
            if (!arguments.TryGet<string>(filterName, out filter) || string.IsNullOrWhiteSpace(filter) || filter.Length > StorageLimits.TextCharacters)
            {
                limit = StorageLimits.DefaultRows;
                maxOutputBytes = StorageLimits.DefaultOutputBytes;
                error = $"{filterName} must be a non-empty string of at most {StorageLimits.TextCharacters} characters.";
                return false;
            }
        }

        return TryInteger(arguments, "limit", StorageLimits.DefaultRows, 1, StorageLimits.MaximumRows, out limit, out error)
            && TryInteger(arguments, "maxOutputBytes", StorageLimits.DefaultOutputBytes, StorageLimits.MinimumOutputBytes, StorageLimits.MaximumOutputBytes, out maxOutputBytes, out error);
    }

    public static bool TryReadIo(ToolArguments arguments, out string? device, out int sampleMilliseconds, out int maxOutputBytes, out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        device = null;
        sampleMilliseconds = StorageLimits.DefaultSampleMilliseconds;
        maxOutputBytes = StorageLimits.DefaultOutputBytes;
        if (IsPresent(arguments, "device")
            && (!arguments.TryGet<string>("device", out device) || string.IsNullOrWhiteSpace(device) || device.Length > StorageLimits.TextCharacters))
        {
            sampleMilliseconds = StorageLimits.DefaultSampleMilliseconds;
            maxOutputBytes = StorageLimits.DefaultOutputBytes;
            error = $"device must be a non-empty string of at most {StorageLimits.TextCharacters} characters.";
            return false;
        }

        return TryInteger(arguments, "sampleMilliseconds", StorageLimits.DefaultSampleMilliseconds,
                StorageLimits.MinimumSampleMilliseconds, StorageLimits.MaximumSampleMilliseconds, out sampleMilliseconds, out error)
            && TryInteger(arguments, "maxOutputBytes", StorageLimits.DefaultOutputBytes,
                StorageLimits.MinimumOutputBytes, StorageLimits.MaximumOutputBytes, out maxOutputBytes, out error);
    }

    private static bool TryInteger(ToolArguments arguments, string name, int defaultValue, int minimum, int maximum, out int value, out string? error)
    {
        value = defaultValue;
        if (!IsPresent(arguments, name))
        {
            error = null;
            return true;
        }

        if (!arguments.TryGet<int>(name, out value) || value < minimum || value > maximum)
        {
            error = $"{name} must be an integer between {minimum} and {maximum}.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool IsPresent(ToolArguments arguments, string name)
    {
        var json = arguments.ToJson();
        return json.ContainsKey(name) && json[name] is not null && json[name]!.GetValueKind() is not JsonValueKind.Null;
    }
}
