namespace DiskWeaver.Executor;

/// <summary>The state of one <see cref="ExecutionStep"/> within an <see cref="ExecutionJournal"/>.</summary>
public enum ExecutionStepStatus
{
    Pending,
    Succeeded,
    Failed,
}
