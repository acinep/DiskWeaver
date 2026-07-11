using DiskWeaver.Core.Executor.Abstractions;
using DiskWeaver.Executor;

namespace DiskWeaver.Daemon.Tests;

/// <summary>In-memory <see cref="IJournalStore"/> for daemon tests -- no real filesystem involved.</summary>
public sealed class InMemoryJournalStore : IJournalStore
{
    private readonly Dictionary<string, ExecutionJournal> _journals = [];

    public ExecutionJournal? Load(string id) => _journals.GetValueOrDefault(id);

    public void Save(ExecutionJournal journal) => _journals[journal.Id] = journal;
}
