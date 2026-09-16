// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Api;

internal sealed class AgentTaskLauncherOptions
{
    public int MaxConcurrentTasks { get; init; } = 2;
}
