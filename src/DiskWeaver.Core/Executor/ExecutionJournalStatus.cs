namespace DiskWeaver.Executor;

/// <summary>The overall state of an <see cref="ExecutionJournal"/>.</summary>
public enum ExecutionJournalStatus
{
    Running,
    Succeeded,
    Failed,
}
