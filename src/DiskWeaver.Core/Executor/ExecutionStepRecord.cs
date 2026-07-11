namespace DiskWeaver.Executor;

/// <summary>
/// The recorded outcome of one <see cref="ExecutionStep"/> within an <see cref="ExecutionJournal"/>.
/// Command/Arguments are duplicated from the plan (not just an index) so the journal remains a
/// ground-truth record of exactly what ran, independent of whether re-deriving the plan later
/// would still produce the same steps.
/// </summary>
public sealed record ExecutionStepRecord(
    int Index,
    string Description,
    string? Command,
    IReadOnlyList<string> Arguments,
    ExecutionStepStatus Status,
    int? ExitCode,
    string? Output,
    string? Error);
