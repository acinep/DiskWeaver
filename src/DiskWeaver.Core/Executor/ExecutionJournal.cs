namespace DiskWeaver.Executor;

/// <summary>
/// Persisted record of one plan's execution -- which steps ran, which succeeded/failed, and
/// their output. This is the one piece of state DiskWeaver actually needs to persist (see
/// docs/state-model.md): a crash mid-run must be recoverable by resuming from here rather than
/// blindly re-running everything or guessing what already happened to real disks.
/// </summary>
public sealed record ExecutionJournal(
    string Id,
    string Kind,
    ExecutionJournalStatus Status,
    IReadOnlyList<ExecutionStepRecord> Steps);
