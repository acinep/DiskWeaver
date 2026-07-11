using DiskWeaver.Executor;

namespace DiskWeaver.Cli;

/// <summary>
/// Implements `diskweaver testkit --disks ... [--out <file>] [--dir <workdir>]` --
/// emits the loop-device setup script described in docs/testing.md, plus the
/// follow-on commands to capture inventory and generate a pool-build script.
/// </summary>
public static class TestKitCommand
{
    public static int Run(string[] args)
    {
        string? disksArg = null;
        string? outPath = null;
        var workDir = "~/diskweaver-test";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--disks" when i + 1 < args.Length:
                    disksArg = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;
                case "--dir" when i + 1 < args.Length:
                    workDir = args[++i];
                    break;
            }
        }

        if (disksArg is null)
        {
            Console.Error.WriteLine("Usage: diskweaver testkit --disks <size,size,...> [--out <file>] [--dir <workdir>]");
            Console.Error.WriteLine("Example: diskweaver testkit --disks 2GB,2GB,4GB,4GB,4GB --out setup-loop-devices.sh");
            return 1;
        }

        long[] sizes;
        try
        {
            sizes = disksArg
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(DiskSizeParser.ParseBytes)
                .ToArray();
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"Could not parse disk sizes: {ex.Message}");
            return 1;
        }

        var script = LoopDeviceScriptEmitter.Render(sizes, workDir);

        if (outPath is not null)
        {
            File.WriteAllText(outPath, script);
            Console.WriteLine($"Loop-device setup script written to {outPath}");
        }
        else
        {
            Console.WriteLine(script);
        }

        Console.WriteLine();
        Console.WriteLine("Next steps (run on the Linux box with the loop devices attached):");
        Console.WriteLine($"  1. bash {outPath ?? "<this script>"}");
        Console.WriteLine("     (this also writes inventory.json, scoped to ONLY the loop devices it created --");
        Console.WriteLine("     do not replace it with a system-wide `lsblk` capture, or your real disks will");
        Console.WriteLine("     end up in the same plan/script.)");
        Console.WriteLine("  2. diskweaver plan --lsblk-json inventory.json --redundancy dwr1 --script build-pool.sh");
        Console.WriteLine("  3. review build-pool.sh, then run it by hand. See docs/testing.md for gotchas + teardown.");

        return 0;
    }
}
