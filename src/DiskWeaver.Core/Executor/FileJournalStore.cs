using DiskWeaver.Core.Executor.Abstractions;
using System.Text.Json;

namespace DiskWeaver.Executor;

/// <summary>
/// Persists an <see cref="ExecutionJournal"/> as one JSON file per execution id under a
/// directory (e.g. /var/lib/diskweaver/journal). The directory is created lazily on first
/// <see cref="Save"/>, not in the constructor, so instantiating this on a host/path that isn't
/// writable yet (e.g. during daemon startup on a dev machine) doesn't fail until actually used.
/// Writes go to a temp file and are renamed into place so a crash mid-write never leaves a
/// half-written journal behind.
/// </summary>
public sealed class FileJournalStore(string directory) : IJournalStore
{
    public ExecutionJournal? Load(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, ExecutorJsonContext.Default.ExecutionJournal);
    }

    public void Save(ExecutionJournal journal)
    {
        Directory.CreateDirectory(directory);

        var path = PathFor(journal.Id);
        var tempPath = $"{path}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(journal, ExecutorJsonContext.Default.ExecutionJournal));
        File.Move(tempPath, path, overwrite: true);
    }

    private string PathFor(string id) => Path.Combine(directory, $"{id}.json");
}
