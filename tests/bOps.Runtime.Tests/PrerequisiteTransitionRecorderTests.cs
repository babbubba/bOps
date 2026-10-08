// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>ADR-0049 section 7: messages on fingerprint transitions only, one per prerequisite, never a flood.</summary>
public sealed class PrerequisiteTransitionRecorderTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly PrerequisiteUsage RequiredByOneTool = new([ComponentReference.Tool("sample.analyze")], []);
    private static readonly PrerequisiteUsage OptionalOnly = new([], [ComponentReference.Tool("sample.analyze")]);

    private readonly InMemoryPrerequisiteStateStore _store = new();
    private readonly PrerequisiteTransitionRecorder _recorder;
    private int _minute;

    public PrerequisiteTransitionRecorderTests() => _recorder = new PrerequisiteTransitionRecorder(_store, NodeId.Local);

    [Fact]
    public async Task FirstObservedUnavailable_RequiredBySomething_IsOneWarning()
    {
        var message = await Record(PrerequisiteState.Unavailable, "executable-not-found", RequiredByOneTool);

        Assert.NotNull(message);
        Assert.Equal(SystemMessageSeverity.Warning, message.Severity);
        Assert.Equal(PrerequisiteCodes.MessageMissing, message.Code);
        Assert.Equal("prerequisite/sample.debugger", message.Source);
        Assert.Equal(SystemComponentType.Prerequisite, message.ComponentType);
        Assert.Equal("sample.debugger", message.ComponentId);
        Assert.Equal(NodeId.Local, message.Node);
        Assert.Equal("The prerequisite is Unavailable.", message.Message);
        Assert.True(message.Metadata.TryGetString("requirement", out var requirement));
        Assert.Equal("required", requirement);
        message.Validate();
        Assert.Single(_store.Messages);
    }

    [Fact]
    public async Task FirstObservedAvailable_IsQuiet_ButPersisted()
    {
        Assert.Null(await Record(PrerequisiteState.Available, "available", RequiredByOneTool));

        Assert.Empty(_store.Messages);
        Assert.Equal(PrerequisiteState.Available, (await _store.LoadStateAsync(NodeId.Local, "sample.debugger"))!.State);
    }

    [Fact]
    public async Task AvailableToUnavailable_IsAWarning()
    {
        await Record(PrerequisiteState.Available, "available", RequiredByOneTool);

        var message = await Record(PrerequisiteState.Unavailable, "unavailable", RequiredByOneTool);

        Assert.Equal(SystemMessageSeverity.Warning, message!.Severity);
        Assert.True(message.Metadata.TryGetString("previousState", out var previous));
        Assert.Equal("Available", previous);
    }

    [Fact]
    public async Task Recovery_IsInformation()
    {
        await Record(PrerequisiteState.Unavailable, "unavailable", RequiredByOneTool);

        var message = await Record(PrerequisiteState.Available, "available", RequiredByOneTool);

        Assert.Equal(SystemMessageSeverity.Information, message!.Severity);
        Assert.Equal(PrerequisiteCodes.MessageRecovered, message.Code);
        Assert.Equal(2, _store.Messages.Count);
    }

    [Fact]
    public async Task CheckFailure_IsAnError_AndRecoveryFromItIsInformation()
    {
        await Record(PrerequisiteState.Available, "available", RequiredByOneTool);

        var failed = await Record(PrerequisiteState.Error, PrerequisiteCodes.CheckFailed, OptionalOnly);
        var recovered = await Record(PrerequisiteState.Available, "available", OptionalOnly);

        Assert.Equal((SystemMessageSeverity.Error, PrerequisiteCodes.MessageCheckFailed), (failed!.Severity, failed.Code));
        Assert.Equal(SystemMessageSeverity.Information, recovered!.Severity);
    }

    [Fact]
    public async Task SameStateAndReason_Repeatedly_CreatesNoNewMessage_ButUpdatesTheCheckTime()
    {
        await Record(PrerequisiteState.Unavailable, "unavailable", RequiredByOneTool);
        var firstChange = (await _store.LoadStateAsync(NodeId.Local, "sample.debugger"))!.ChangedAtUtc;

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(await Record(PrerequisiteState.Unavailable, "unavailable", RequiredByOneTool, message: $"Different text {i}."));
        }

        var state = (await _store.LoadStateAsync(NodeId.Local, "sample.debugger"))!;
        Assert.Single(_store.Messages);
        Assert.Equal(firstChange, state.ChangedAtUtc);
        Assert.Equal(Start.AddMinutes(_minute), state.CheckedAtUtc);
        Assert.Equal("Different text 4.", state.Message);
    }

    [Fact]
    public async Task SameStateWithANewReason_IsANewMessage()
    {
        await Record(PrerequisiteState.Unavailable, "executable-not-found", RequiredByOneTool);

        var message = await Record(PrerequisiteState.Unavailable, "version-unsupported", RequiredByOneTool);

        Assert.Equal(PrerequisiteCodes.MessageMissing, message!.Code);
        Assert.Equal(2, _store.Messages.Count);
    }

    [Fact]
    public async Task OptionalOnlyPrerequisiteUnavailable_IsInformation()
    {
        var message = await Record(PrerequisiteState.Unavailable, "executable-not-found", OptionalOnly);

        Assert.Equal(SystemMessageSeverity.Information, message!.Severity);
        Assert.True(message.Metadata.TryGetString("requirement", out var requirement));
        Assert.Equal("optional", requirement);
    }

    [Fact]
    public async Task Degraded_IsAWarningWhenRequired_AndInformationWhenOptional()
    {
        var required = await Record(PrerequisiteState.Degraded, "version-old", RequiredByOneTool, id: "sample.a");
        var optional = await Record(PrerequisiteState.Degraded, "version-old", OptionalOnly, id: "sample.b");

        Assert.Equal((SystemMessageSeverity.Warning, PrerequisiteCodes.MessageDegraded), (required!.Severity, required.Code));
        Assert.Equal(SystemMessageSeverity.Information, optional!.Severity);
    }

    [Fact]
    public async Task ASharedPrerequisite_ProducesOneMessage_WithBoundedAffectedComponents()
    {
        var tools = Enumerable.Range(0, 20).Select(i => ComponentReference.Tool($"sample.tool{i:00}")).ToArray();

        var message = await Record(PrerequisiteState.Unavailable, "daemon-unreachable", new PrerequisiteUsage(tools, []));

        Assert.Single(_store.Messages);
        var metadata = message!.Metadata.ToJson();
        Assert.Equal(20, metadata["affectedComponentCount"]!.GetValue<int>());
        Assert.Equal(PrerequisiteTransitionRecorder.MaxAffectedComponents, metadata["affectedComponents"]!.AsArray().Count);
        Assert.Equal("tool:sample.tool00", metadata["affectedComponents"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task CheckDetail_IsCarriedUnderADetailPrefix()
    {
        var detail = OperationalMetadata.From(new JsonObject { ["requiredExecutable"] = "kd.exe" });

        var message = await Record(PrerequisiteState.Unavailable, "executable-not-found", RequiredByOneTool, metadata: detail);

        Assert.True(message!.Metadata.TryGetString("detail.requiredExecutable", out var executable));
        Assert.Equal("kd.exe", executable);
    }

    [Fact]
    public async Task UnknownResult_IsNeverRecorded()
    {
        Assert.Null(await Record(PrerequisiteState.Unknown, PrerequisiteCodes.NotRegistered, RequiredByOneTool));

        Assert.Null(await _store.LoadStateAsync(NodeId.Local, "sample.debugger"));
    }

    [Fact]
    public async Task AConcurrentWriterWithTheSameObservation_WinsAndNoDuplicateMessageIsWritten()
    {
        _store.BeforeNextSave = () => _store.Put(new PrerequisiteStateRecord
        {
            Node = NodeId.Local,
            PrerequisiteId = "sample.debugger",
            State = PrerequisiteState.Unavailable,
            Code = "unavailable",
            Message = "Written by another refresh.",
            CheckedAtUtc = Start,
            ChangedAtUtc = Start,
        });

        var message = await Record(PrerequisiteState.Unavailable, "unavailable", RequiredByOneTool);

        Assert.Null(message);
        Assert.Empty(_store.Messages);
        Assert.Equal(2, _store.SaveAttempts);
    }

    private static PrerequisiteDescriptor Debugger(string? remediation = "Install the \"Debugging Tools for Windows\" component so kd.exe is available.") => new(
        "sample.debugger", "Microsoft Debugging Tools for Windows", "kd.exe, used to read kernel dumps.", PrerequisiteKind.Executable)
    {
        Remediation = remediation,
        Metadata = OperationalMetadata.From(new JsonObject { ["requiredExecutable"] = "kd.exe" }),
    };

    [Fact]
    public async Task AMissingRequiredPrerequisite_ExplainsWhatToDo_FromItsDescriptor()
    {
        var message = await Record(PrerequisiteState.Unavailable, "executable-not-found", RequiredByOneTool, descriptor: Debugger());

        Assert.Equal(
            "Microsoft Debugging Tools for Windows is unavailable. Install the \"Debugging Tools for Windows\" component so kd.exe is available.",
            message!.Message);
        Assert.Equal(SystemMessageSeverity.Warning, message.Severity);
        Assert.True(message.Metadata.TryGetString("displayName", out var displayName));
        Assert.Equal("Microsoft Debugging Tools for Windows", displayName);
        Assert.True(message.Metadata.TryGetString("kind", out var kind));
        Assert.Equal("Executable", kind);
        Assert.True(message.Metadata.TryGetString("remediation", out var remediation));
        Assert.Contains("Debugging Tools for Windows", remediation, StringComparison.Ordinal);
        Assert.True(message.Metadata.TryGetString("descriptor.requiredExecutable", out var executable));
        Assert.Equal("kd.exe", executable);
        message.Validate();
    }

    [Fact]
    public async Task ALongRemediation_StaysWithinTheMessageAndMetadataBounds()
    {
        var message = await Record(
            PrerequisiteState.Unavailable, "executable-not-found", RequiredByOneTool,
            descriptor: Debugger(new string('r', PrerequisiteDescriptor.MaxTextLength)));

        Assert.True(message!.Message.Length <= SystemMessage.MaxMessageLength);
        Assert.StartsWith("Microsoft Debugging Tools for Windows is unavailable. rrr", message.Message, StringComparison.Ordinal);
        Assert.EndsWith("…", message.Message, StringComparison.Ordinal);
        Assert.True(message.Metadata.TryGetString("remediation", out var remediation));
        Assert.True(remediation.Length <= OperationalMetadata.MaxStringLength);
        message.Validate();
    }

    [Fact]
    public async Task ADegradedPrerequisite_AndARecovery_UseTheDisplayName()
    {
        var degraded = await Record(PrerequisiteState.Degraded, "old-version", OptionalOnly, descriptor: Debugger());
        var recovered = await Record(PrerequisiteState.Available, "available", OptionalOnly, descriptor: Debugger());

        Assert.StartsWith("Microsoft Debugging Tools for Windows is degraded.", degraded!.Message, StringComparison.Ordinal);
        Assert.Equal(SystemMessageSeverity.Information, degraded.Severity);
        Assert.Equal("Microsoft Debugging Tools for Windows is available again.", recovered!.Message);
    }

    [Fact]
    public async Task ChangingTheRemediationText_NeverCreatesANewTransition()
    {
        Assert.NotNull(await Record(PrerequisiteState.Unavailable, "executable-not-found", RequiredByOneTool, descriptor: Debugger()));

        var again = await Record(PrerequisiteState.Unavailable, "executable-not-found", RequiredByOneTool, descriptor: Debugger("Something else entirely."));

        Assert.Null(again);
        Assert.Single(_store.Messages);
    }

    [Fact]
    public async Task WithoutADescriptor_TheCheckMessageIsUsedAsBefore_AndNoRemediationIsInvented()
    {
        var message = await Record(PrerequisiteState.Unavailable, "executable-not-found", RequiredByOneTool);

        Assert.Equal("The prerequisite is Unavailable.", message!.Message);
        Assert.False(message.Metadata.TryGetString("remediation", out _));
    }

    [Fact]
    public async Task ACheckFailure_KeepsTheHostAuthoredText_WithoutRemediation()
    {
        var message = await Record(PrerequisiteState.Error, "check-failed", RequiredByOneTool, message: "The prerequisite check failed unexpectedly.", descriptor: Debugger());

        Assert.Equal("The prerequisite check failed unexpectedly.", message!.Message);
        Assert.Equal(SystemMessageSeverity.Error, message.Severity);
    }

    private Task<SystemMessage?> Record(
        PrerequisiteState state, string code, PrerequisiteUsage usage, string? message = null, string id = "sample.debugger", OperationalMetadata? metadata = null,
        PrerequisiteDescriptor? descriptor = null) =>
        _recorder.RecordAsync(
            new PrerequisiteCheckResult
            {
                Id = id,
                State = state,
                Code = code,
                Message = message ?? $"The prerequisite is {state}.",
                CheckedAtUtc = Start.AddMinutes(++_minute),
                Metadata = metadata ?? OperationalMetadata.Empty,
            },
            usage,
            descriptor);

    private sealed class InMemoryPrerequisiteStateStore : IPrerequisiteStateStore
    {
        private readonly Dictionary<string, PrerequisiteStateRecord> _states = new(StringComparer.Ordinal);

        public List<SystemMessage> Messages { get; } = [];

        public int SaveAttempts { get; private set; }

        public Action? BeforeNextSave { get; set; }

        public void Put(PrerequisiteStateRecord state) => _states[Key(state.Node, state.PrerequisiteId)] = state;

        public Task<PrerequisiteStateRecord?> LoadStateAsync(NodeId node, string prerequisiteId, CancellationToken ct = default) =>
            Task.FromResult(_states.GetValueOrDefault(Key(node, prerequisiteId)));

        public Task<IReadOnlyList<PrerequisiteStateRecord>> ListStatesAsync(NodeId node, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PrerequisiteStateRecord>>(_states.Values.Where(state => state.Node == node).ToArray());

        public Task<bool> SaveStateAsync(PrerequisiteStateRecord state, string? expectedFingerprint, SystemMessage? transitionMessage, CancellationToken ct = default)
        {
            SaveAttempts++;
            var hook = BeforeNextSave;
            BeforeNextSave = null;
            hook?.Invoke();

            var current = _states.GetValueOrDefault(Key(state.Node, state.PrerequisiteId));
            if (!string.Equals(current?.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            Put(state);
            if (transitionMessage is not null)
            {
                Messages.Add(transitionMessage);
            }

            return Task.FromResult(true);
        }

        private static string Key(NodeId node, string id) => $"{node.Value}|{id}";
    }
}
