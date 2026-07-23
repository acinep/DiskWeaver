using DiskWeaver.Executor;
using DiskWeaver.Inventory;
using DiskWeaver.Planner;

namespace DiskWeaver.Cli;

/// <summary>
/// Implements `diskweaver plan --redundancy ... --disks ... [--lsblk-json <file> | --lsblk] [--script <file>] [--assume-clean] [--chunk-size <64|128|256|512>]`.
/// <c>--assume-clean</c> (only affects <c>--script</c>) runs each tier's <c>mdadm --create</c> with
/// <c>--assume-clean</c>, skipping the initial full-array resync/parity-build -- see
/// <see cref="Executor.CommandPlanner.Build"/>'s <c>assumeClean</c> parameter for why that's safe
/// on the always-blank disks this command works with.
/// <c>--chunk-size</c> (only affects <c>--script</c>, KiB, default
/// <see cref="Executor.CommandPlanner.DefaultChunkSizeKb"/>) sets the striped (RAID5/RAID6) tier
/// chunk size -- see <see cref="Executor.CommandPlanner.Build"/>'s <c>chunkSizeKb</c> parameter.
/// <c>--disks</c> is always required and always means "the exact disks to plan for" -- there is
/// no mode where omitting it falls back to "use everything found". Without an inventory source
/// it's a list of raw sizes (--disks 2TB,2TB,4TB); with --lsblk-json/--lsblk it's the disk
/// ids/names to select from that inventory (--disks loop0,loop1,loop2,loop3).
/// </summary>
public static class PlanCommand
{
    public static int Run(string[] args)
    {
        string? disksArg = null;
        string? lsblkJsonPath = null;
        var useLiveLsblk = false;
        string? redundancyArg = null;
        string? scriptOutputPath = null;
        string? teardownScriptOutputPath = null;
        var assumeClean = false;
        var chunkSizeKb = CommandPlanner.DefaultChunkSizeKb;
        string? chunkSizeArg = null;

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
                case "--redundancy" when i + 1 < args.Length:
                    redundancyArg = args[++i];
                    break;
                case "--script" when i + 1 < args.Length:
                    scriptOutputPath = args[++i];
                    break;
                case "--teardown-script" when i + 1 < args.Length:
                    teardownScriptOutputPath = args[++i];
                    break;
                case "--assume-clean":
                    assumeClean = true;
                    break;
                case "--chunk-size" when i + 1 < args.Length:
                    chunkSizeArg = args[++i];
                    break;
            }
        }

        if (chunkSizeArg is not null)
        {
            if (!int.TryParse(chunkSizeArg, out chunkSizeKb) || !CommandPlanner.ValidChunkSizesKb.Contains(chunkSizeKb))
            {
                Console.Error.WriteLine(
                    $"Unsupported --chunk-size '{chunkSizeArg}' -- use one of: "
                    + $"{string.Join(", ", CommandPlanner.ValidChunkSizesKb)}.");
                return 1;
            }
        }

        var inventorySourceCount = new[] { lsblkJsonPath is not null, useLiveLsblk }.Count(b => b);

        if (redundancyArg is null || disksArg is null || inventorySourceCount > 1)
        {
            Console.Error.WriteLine(
                "Usage: diskweaver plan --redundancy <none|dwr1|dwr2> --disks <size,size,... | name,name,...> "
                + "[--lsblk-json <file> | --lsblk] [--script <file>] [--assume-clean] [--chunk-size <64|128|256|512>]");
            Console.Error.WriteLine("Example: diskweaver plan --redundancy dwr1 --disks 2TB,2TB,4TB,4TB,4TB");
            Console.Error.WriteLine(
                "Example: diskweaver plan --redundancy dwr1 --lsblk-json inventory.json --disks loop0,loop1,loop2,loop3");
            Console.Error.WriteLine(
                "--disks is always required: with --lsblk-json/--lsblk it selects which captured disks to use "
                + "(run `diskweaver inventory` first to see what's available) -- there is no 'use everything found' "
                + "default, since that's how a real disk ends up in a plan by accident.");
            return 1;
        }

        RedundancyLevel redundancy;
        switch (redundancyArg.Trim().ToLowerInvariant())
        {
            case "none":
            case "0":
                redundancy = RedundancyLevel.None;
                break;
            case "dwr1":
            case "1":
                redundancy = RedundancyLevel.Dwr1;
                break;
            case "dwr2":
            case "2":
                redundancy = RedundancyLevel.Dwr2;
                break;
            default:
                Console.Error.WriteLine($"Unknown redundancy level '{redundancyArg}'. Use none, dwr1, or dwr2.");
                return 1;
        }

        IReadOnlyList<Disk> disks;
        try
        {
            if (inventorySourceCount == 1)
            {
                disks = DiskSourceResolver.Select(DiskSourceResolver.Resolve(null, lsblkJsonPath, useLiveLsblk), disksArg);
                // Real disks pulled from --lsblk/--lsblk-json can already have data on them --
                // refuse the same way the daemon's POST /plan does, since this command can go on to
                // write a real parted/mdadm/wipefs script (--script below). Synthetic --disks sizes
                // (the other branch) have no real hardware behind them, so this doesn't apply there.
                DiskSelector.EnsureBlank(disks);
            }
            else
            {
                disks = DiskSourceResolver.Resolve(disksArg, null, false);
            }
        }
        catch (Exception ex) when (ex is FormatException or IOException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"Could not read disk inventory: {ex.Message}");
            return 1;
        }

        PoolPlan plan;
        try
        {
            // PartitionLayout.PlanForRealDisks reserves GPT/alignment overhead per disk before
            // tiering, so the resulting plan is guaranteed fully partitionable -- otherwise the
            // smallest disk in the pool can come up short by exactly this much (`parted` then
            // fails: "location is outside of the device").
            plan = PartitionLayout.PlanForRealDisks(disks, redundancy);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        PlanFormatter.Print(plan);

        if (scriptOutputPath is not null)
        {
            var executionPlan = CommandPlanner.Build(plan, assumeClean: assumeClean, chunkSizeKb: chunkSizeKb);
            var script = ShellScriptEmitter.Render(executionPlan);
            File.WriteAllText(scriptOutputPath, script);
            Console.WriteLine();
            Console.WriteLine($"Execution script written to {scriptOutputPath}");
        }

        if (teardownScriptOutputPath is not null)
        {
            var teardownPlan = CommandPlanner.BuildTeardown(plan);
            var teardownScript = ShellScriptEmitter.Render(teardownPlan);
            File.WriteAllText(teardownScriptOutputPath, teardownScript);
            Console.WriteLine($"Teardown script written to {teardownScriptOutputPath}");
        }

        return 0;
    }
}
