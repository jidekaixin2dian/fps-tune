namespace FpsTune.Wpf.Services;

internal enum MemoryCleanupMode { LowStandby, Standby, WorkingSets, Combined, Full }
internal sealed record MemoryCleanupProgress(string Stage, int Completed, int Total, string? Name = null);
internal sealed record MemoryCleanupRun(MemorySnapshot Before, MemorySnapshot? After,
    IReadOnlyList<MemoryProcessResult> Apps, bool StandbyCompleted, string? Error, string? ReadError, bool Canceled);
