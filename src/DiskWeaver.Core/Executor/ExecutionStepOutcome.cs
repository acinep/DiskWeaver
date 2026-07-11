namespace DiskWeaver.Executor;

/// <summary>The raw result of actually running one <see cref="ExecutionStep"/>'s command.</summary>
public sealed record ExecutionStepOutcome(int ExitCode, string Output, string Error);
