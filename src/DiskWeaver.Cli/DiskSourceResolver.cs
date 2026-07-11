using DiskWeaver.Inventory;
using DiskWeaver.Planner;

namespace DiskWeaver.Cli;

/// <summary>
/// Resolves the raw disk list for the `--disks`/`--lsblk-json`/`--lsblk`
/// options shared by `plan` and `inventory`, and applies the mandatory
/// disk-id selection (`plan`'s `--disks`, in inventory mode) on top.
/// </summary>
public static class DiskSourceResolver
{
    public static IReadOnlyList<Disk> Resolve(string? disksArg, string? lsblkJsonPath, bool useLiveLsblk)
    {
        return disksArg is not null
            ? disksArg
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select((size, i) => new Disk($"disk{i}", DiskSizeParser.ParseBytes(size)))
                .ToArray()
            : lsblkJsonPath is not null
                ? LsblkOutputParser.ParseDisks(File.ReadAllText(lsblkJsonPath))
                : new LsblkDiskInventorySource().GetDisks();
    }

    /// <summary>
    /// Filters <paramref name="disks"/> down to those named in <paramref name="disksArg"/>, a
    /// comma-separated list of full disk ids or just their trailing component (e.g. "loop0"
    /// instead of the full "/dev/disk/by-id/..." path). See <see cref="DiskSelector.Select"/>.
    /// </summary>
    public static IReadOnlyList<Disk> Select(IReadOnlyList<Disk> disks, string disksArg)
    {
        var names = disksArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return DiskSelector.Select(disks, names);
    }
}
