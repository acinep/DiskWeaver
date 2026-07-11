using DiskWeaver.Cli;

namespace DiskWeaver.Cli.Tests;

public class PlanCommandTests
{
    [Fact]
    public void LsblkJson_DiskThatAlreadyHasAPartitionTable_ReturnsNonZeroAndRefuses()
    {
        // Regression test for the CLI's plan --lsblk-json path not applying the same blank-disk
        // refusal the daemon's POST /plan does -- this can otherwise write a real parted/mdadm
        // build script (--script) against a disk that already has data on it.
        var lsblkJsonPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(lsblkJsonPath, """
                {"blockdevices": [
                    {"name": "loop0", "size": "2000000000000", "type": "loop", "id-link": null,
                     "fstype": null, "mountpoints": [null],
                     "children": [{"name": "loop0p1", "size": "1000000000000", "type": "part", "id-link": null}]},
                    {"name": "loop1", "size": "2000000000000", "type": "loop", "id-link": null,
                     "fstype": null, "mountpoints": [null]}
                ]}
                """);

            var exitCode = RunPlanCommand(
                ["--redundancy", "dwr1", "--lsblk-json", lsblkJsonPath, "--disks", "loop0,loop1"], out var stderr);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("loop0", stderr);
        }
        finally
        {
            File.Delete(lsblkJsonPath);
        }
    }

    [Fact]
    public void LsblkJson_CaptureMissingFstypeMountpointsColumns_ReturnsNonZeroAndRefuses()
    {
        // Regression test: an old capture from `lsblk --json -b -d -o NAME,SIZE,TYPE,ID-LINK`
        // (predating disk-eligibility checking) has no FSTYPE/MOUNTPOINTS columns at all --
        // LsblkOutputParser must refuse it outright rather than defaulting every disk to "blank."
        var lsblkJsonPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(lsblkJsonPath, """
                {"blockdevices": [
                    {"name": "loop0", "size": "2000000000000", "type": "loop", "id-link": null},
                    {"name": "loop1", "size": "2000000000000", "type": "loop", "id-link": null}
                ]}
                """);

            var exitCode = RunPlanCommand(
                ["--redundancy", "dwr1", "--lsblk-json", lsblkJsonPath, "--disks", "loop0,loop1"], out var stderr);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("loop0", stderr);
        }
        finally
        {
            File.Delete(lsblkJsonPath);
        }
    }

    private static int RunPlanCommand(string[] args, out string stderr)
    {
        var originalError = Console.Error;
        var errorWriter = new StringWriter();
        Console.SetError(errorWriter);
        try
        {
            var exitCode = PlanCommand.Run(args);
            stderr = errorWriter.ToString();
            return exitCode;
        }
        finally
        {
            Console.SetError(originalError);
        }
    }
}
