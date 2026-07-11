using DiskWeaver.Executor;

namespace DiskWeaver.Core.Executor.Abstractions;

/// <summary>Persists/retrieves an <see cref="ExecutionJournal"/> by execution id. See docs/state-model.md.</summary>
public interface IJournalStore
{
    ExecutionJournal? Load(string id);

    void Save(ExecutionJournal journal);
}
