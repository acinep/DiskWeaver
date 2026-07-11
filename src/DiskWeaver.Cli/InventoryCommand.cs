namespace DiskWeaver.Cli;

/// <summary>
/// Implements `diskweaver inventory (--disks ... | --lsblk-json <file> | --lsblk)` -- the review
/// step between capturing inventory and running `plan`, so disks can be deliberately selected
/// with `plan`'s `--only` rather than every captured device being fed into a pool by default.
/// </summary>
public static class InventoryCommand
{
    public static int Run(string[] args)
    {
        string? disksArg = null;
        string? lsblkJsonPath = null;
        var useLiveLsblk = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--disks" when i + 1 < args.Length:
                    disksArg = args[++i];
                    break;
                case "--lsblk-json" when i + 1 < args.Length:
                    lsblkJsonPath = args[++i];
                    break;
                case "--lsblk":
                    useLiveLsblk = true;
                    break;
            }
        }

        var sourceCount = new[] { disksArg is not null, lsblkJsonPath is not null, useLiveLsblk }.Count(b => b);
        if (sourceCount != 1)
        {
            Console.Error.WriteLine("Usage: diskweaver inventory (--disks <size,size,...> | --lsblk-json <file> | --lsblk)");
            return 1;
        }

        IReadOnlyList<Planner.Disk> disks;
        try
        {
            disks = DiskSourceResolver.Resolve(disksArg, lsblkJsonPath, useLiveLsblk);
        }
        catch (Exception ex) when (ex is FormatException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Could not read disk inventory: {ex.Message}");
            return 1;
        }

        if (disks.Count == 0)
        {
            Console.WriteLine("No disks found.");
            return 0;
        }

        Console.WriteLine($"{disks.Count} disk(s) found:");
        foreach (var disk in disks)
        {
            var flag = disk.IsBlank ? "" : "  [NOT BLANK]";
            Console.WriteLine($"  {disk.Id}  ({ByteSizeFormatter.Format(disk.SizeBytes)}){flag}");
        }

        Console.WriteLine();
        Console.WriteLine("Pass the ones you want into a pool with `plan`'s --disks, e.g.:");
        Console.WriteLine($"  --only {string.Join(",", disks.Take(2).Select(d => d.Id.Split('/')[^1]))}");

        var notBlank = disks.Where(d => !d.IsBlank).ToList();
        if (notBlank.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                "[NOT BLANK] disks have a partition table, a mounted filesystem, or an existing "
                + "filesystem/RAID/LVM signature -- `plan` will refuse them as-is. Clear them first with:");
            Console.WriteLine($"  diskweaver wipe --disks {string.Join(",", notBlank.Select(d => d.Id.Split('/')[^1]))} ...");
        }

        return 0;
    }
}
