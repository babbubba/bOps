// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.AgentFramework;
using bOps.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace bOps.Cli;

/// <summary>
/// The CLI side of the opt-in Local Agent knowledge (ADR-0058): reads the <c>Knowledge</c> section strictly and asks the framework adapter
/// for the host. Knowledge is off unless <c>Knowledge:Enabled</c> is <c>true</c>; then an unknown key, an unusable value or a missing file
/// stops the command before any model is contacted, because running on without the knowledge the operator asked for would be a silent
/// degradation. Used for a goal and for a resume only; no other command opens knowledge, and a delegated role never receives it.
/// </summary>
internal static class CliKnowledge
{
    /// <summary>Reads the section. An unknown key or a value of the wrong type is an error even while the section is disabled, so a misspelt <c>Enabled</c> is not a silent <c>false</c>.</summary>
    /// <exception cref="LocalKnowledgeConfigurationException">The section cannot be read.</exception>
    internal static LocalKnowledgeOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        try
        {
            return configuration.GetSection(LocalKnowledgeOptions.SectionName)
                .Get<LocalKnowledgeOptions>(binder => binder.ErrorOnUnknownConfiguration = true)
                ?? new LocalKnowledgeOptions();
        }
        catch (InvalidOperationException ex)
        {
            throw new LocalKnowledgeConfigurationException($"The 'Knowledge' section is not valid: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The knowledge host of a goal or a resume, or <c>null</c> when knowledge is not enabled and the CLI behaves as it did before it existed.
    /// </summary>
    /// <exception cref="LocalKnowledgeConfigurationException">Knowledge is enabled and the configuration is not usable.</exception>
    internal static IKnowledgeTaskHost? CreateHost(IConfiguration configuration, IAuditSink audit, TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var options = Read(configuration);
        var host = LocalKnowledgeComposition.Create(options, audit, time);
        if (host is not null && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Knowledge is enabled for the Local Agent: tasks are bound to '{PackId}' {Version}; {GrantCount} pack version(s) granted.",
                options.Binding?.PackId, options.Binding?.Version, options.Grants.Count);
        }

        return host;
    }
}
