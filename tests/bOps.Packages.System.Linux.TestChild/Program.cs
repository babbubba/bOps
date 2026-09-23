// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

if (args.Length == 2 && args[0] == "sleep" && int.TryParse(args[1], out var milliseconds))
{
    await Task.Delay(milliseconds);
    return;
}

Environment.ExitCode = 2;
