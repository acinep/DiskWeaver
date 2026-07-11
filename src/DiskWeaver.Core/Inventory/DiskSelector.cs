using DiskWeaver.Planner;

namespace DiskWeaver.Inventory;

/// <summary>Selects specific disks out of a captured/live inventory by id or trailing name component.</summary>
public static class DiskSelector
{
    /// <summary>
    /// Filters <paramref name="disks"/> down to those named in <paramref name="names"/> (each a full
    /// disk id or just its trailing component, e.g. "loop0" or "wwn-0x5000cca264efee2f" instead of the
    /// full "/dev/disk/by-id/..." path). Throws <see cref="ArgumentException"/> naming any requested
    /// disk that doesn't match exactly one, so a typo never silently selects the wrong disk set --
    /// and throws the same way if two names resolve to the same disk (e.g. a full id and its
    /// trailing component both naming "loop0", or the name simply repeated), so a duplicate can
    /// never inflate a tier's participant count with two partitions of what's really one physical
    /// disk pretending to be redundant.
    /// </summary>
    public static IReadOnlyList<Disk> Select(IReadOnlyList<Disk> disks, IReadOnlyList<string> names)
    {
        var selected = new List<Disk>();

        foreach (var name in names)
        {
            var matches = disks.Where(d => d.Id == name || d.Id.EndsWith("/" + name, StringComparison.Ordinal)).ToList();
            if (matches.Count == 0)
            {
                var available = string.Join(", ", disks.Select(d => d.Id));
                throw new ArgumentException($"No disk matching '{name}'. Available: {available}");
            }

            if (matches.Count > 1)
            {
                throw new ArgumentException(
                    $"'{name}' matches multiple disks ({string.Join(", ", matches.Select(d => d.Id))}); "
                    + "use the full disk id to disambiguate.");
            }

            selected.Add(matches[0]);
        }

        var duplicateIds = selected.GroupBy(d => d.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicateIds.Count > 0)
        {
            throw new ArgumentException(
                $"The same disk was requested more than once: {string.Join(", ", duplicateIds)}. "
                + "Each disk can only participate once -- it can't stand in for two separate redundant members.");
        }

        return selected;
    }

    /// <summary>
    /// Refuses any disk that isn't blank (per <see cref="Disk.IsBlank"/>) -- i.e. has a partition
    /// table, a mounted filesystem, or a foreign filesystem/RAID/LVM signature already on it. Call
    /// this on newly-requested disks before planning a fresh pool or an expansion, so a stale
    /// selection (or a disk the user didn't realize already has data) never reaches
    /// `parted mklabel`/`wipefs`. Deliberately does not apply to disks a pool already owns (those
    /// are legitimately non-blank -- they're mdadm/LVM members by design).
    /// </summary>
    public static void EnsureBlank(IReadOnlyList<Disk> disks)
    {
        var notBlank = disks.Where(d => !d.IsBlank).Select(d => d.Id).ToList();
        if (notBlank.Count > 0)
        {
            throw new InvalidOperationException(
                $"Refusing to use disk(s) that aren't blank: {string.Join(", ", notBlank)}. "
                + "They have a partition table, a mounted filesystem, or an existing filesystem/RAID/LVM "
                + "signature -- wipe them manually first if you intend to reuse them.");
        }
    }
}
