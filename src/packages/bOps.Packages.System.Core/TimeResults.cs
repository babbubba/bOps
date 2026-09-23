namespace bOps.Packages.Sys.Core;

public sealed record SystemTimeResult(DateTimeOffset UtcNow, DateTimeOffset LocalNow, string TimeZoneId, int UtcOffsetMinutes, bool Dst, string ClockSource, bool? NtpConfigured, bool? NtpSynchronized, string? TimeServiceStatus, string Source, bool Complete);
public sealed record RebootPendingResult(bool Pending, IReadOnlyList<string> Reasons, string Source, bool Complete);
