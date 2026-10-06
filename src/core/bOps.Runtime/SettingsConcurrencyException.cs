// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>A Settings write was based on a stale persisted revision.</summary>
public sealed class SettingsConcurrencyException : Exception
{
    public SettingsConcurrencyException() { }

    public SettingsConcurrencyException(string message) : base(message) { }

    public SettingsConcurrencyException(string message, Exception innerException) : base(message, innerException) { }

    public SettingsConcurrencyException(int expectedRevision, int actualRevision)
        : base($"Settings revision {expectedRevision} is stale; current revision is {actualRevision}.")
    {
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public int ExpectedRevision { get; }
    public int ActualRevision { get; }
}
