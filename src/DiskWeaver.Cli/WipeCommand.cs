using DiskWeaver.Executor;
using DiskWeaver.Inventory;

namespace DiskWeaver.Cli;

/// <summary>
/// Implements `diskweaver wipe --disks <name,name,...> (--lsblk-json <file> | --lsblk) [--script <file>]`
/// -- clears the partition-table/filesystem/RAID/LVM signatures flagged as "[NOT BLANK]" by
/// `diskweaver inventory`, so the disk(s) can be selected by `plan` afterward. Only ever emits a
/// script for the user to review and run, the same as `plan --script`/`--teardown-script` -- this
/// CLI never executes destructive commands itself.
/// </summary>
public static class WipeCommand
{
    public static int Run(string[] args)
    {
        string? disksArg = null;
        string? lsblkJsonPath = null;
        var useLiveLsblk = false;
        string? scriptOutputPath = null;

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
                case "--script" when i + 1 < args.Length:
                    scriptOutputPath = args[++i];
                    break;
            }
        }

        var inventorySourceCount = new[] { lsblkJsonPath is not null, useLiveLsblk }.Count(b => b);
        if (disksArg is null || inventorySourceCount != 1)
        {
            Console.Error.WriteLine(
                "Usage: diskweaver wipe --disks <name,name,...> (--lsblk-json <file> | --lsblk) [--script <file>]");
            Console.Error.WriteLine("Example: diskweaver wipe --disks loop3,loop4 --lsblk-json inventory.json --script wipe.sh");
            Console.Error.WriteLine(
                "Run `diskweaver inventory` first to see which disks are flagged [NOT BLANK] and need this.");
            return 1;
        }

        string rawJson;
        try
        {
            rawJson = lsblkJsonPath is not null ? File.ReadAllText(lsblkJsonPath) : new LsblkDiskInventorySource().GetRawJson();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Could not read disk inventory: {ex.Message}");
            return 1;
        }

        IReadOnlyList<Planner.Disk> selected;
        IReadOnlyDictionary<string, IReadOnlyList<string>> partitionPathsByDisk;
        try
        {
            var disks = LsblkOutputParser.ParseDisks(rawJson);
            var names = disksArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            selected = DiskSelector.Select(disks, names);
            partitionPathsByDisk = LsblkOutputParser.ParsePartitionPaths(rawJson);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"Could not resolve disk selection: {ex.Message}");
            return 1;
        }

        var diskIds = selected.Select(d => d.Id).ToList();
        var wipePlan = CommandPlanner.BuildWipe(diskIds, partitionPathsByDisk);
        var script = ShellScriptEmitter.Render(wipePlan);

        if (scriptOutputPath is not null)
        {
            File.WriteAllText(scriptOutputPath, script);
            Console.WriteLine($"Wipe script written to {scriptOutputPath}");
        }
        else
        {
            Console.WriteLine(script);
        }

        Console.WriteLine();
        Console.WriteLine(
            "If a disk is still an active member/spare of a running mdadm array (e.g. left over from "
            + "a grow that failed partway through), stop or remove it from that array first -- "
            + "mdadm --zero-superblock refuses to touch a live member.");

        return 0;
    }
}
