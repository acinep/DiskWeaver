using System.Diagnostics;
using DiskWeaver.Core.Inventory.Abstractions;
using DiskWeaver.Planner;

namespace DiskWeaver.Inventory;

/// <summary>
/// Reads real disk inventory by shelling out to lsblk. Requires lsblk on
/// PATH, so this only runs on Linux (or a POSIX environment providing it) —
/// use <see cref="LsblkOutputParser"/> directly against a captured file
/// when developing off-target.
/// </summary>
public sealed class LsblkDiskInventorySource : IDiskInventorySource
{
    public IReadOnlyList<Disk> GetDisks() => LsblkOutputParser.ParseDisks(GetRawJson());

    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetPartitionPaths() =>
        LsblkOutputParser.ParsePartitionPaths(GetRawJson());

    /// <summary>
    /// Runs lsblk and returns its raw JSON, for callers (e.g. `diskweaver wipe`) that need more
    /// than <see cref="Disk"/> exposes -- e.g. <see cref="LsblkOutputParser.ParsePartitionPaths"/>.
    /// </summary>
    public string GetRawJson()
    {
        // -d (list only the whole disk, not its partitions) is deliberately NOT passed: eligibility
        // checking needs to see each disk's children (partitions, or a holder device like an mdadm
        // array/LVM PV sitting directly on the disk with no partition table) plus its own FSTYPE and
        // MOUNTPOINTS, so LsblkOutputParser can tell a truly blank disk from one that already has
        // data or is already in use by something else. See LsblkOutputParser.IsBlank.
        var startInfo = new ProcessStartInfo("lsblk")
        {
            ArgumentList = { "--json", "-b", "-o", "NAME,SIZE,TYPE,ID-LINK,FSTYPE,MOUNTPOINTS" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start lsblk.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Could not run lsblk. This source only works on a Linux host with util-linux installed. "
                + "Capture `lsblk --json -b -o NAME,SIZE,TYPE,ID-LINK,FSTYPE,MOUNTPOINTS` output to a file "
                + "and parse it with LsblkOutputParser instead.", ex);
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"lsblk exited with code {process.ExitCode}: {error}");
        }

        return output;
    }
}
