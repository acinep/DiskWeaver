using DiskWeaver.Core.Executor.Abstractions;
using DiskWeaver.Inventory;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DiskWeaver.Executor;

/// <summary>
/// Reads real pool state by shelling out to pvs, lvs, mdadm, and blockdev. Requires those on
/// PATH, so this only runs on Linux. DiskWeaver never persists its own copy of this state (see
/// docs/state-model.md) -- it's rediscovered from mdadm/LVM every time it's needed.
/// </summary>
public sealed class MdadmLvmPoolStateSource(ICommandRunner? commandRunner = null) : IPoolStateSource
{
    private readonly ICommandRunner _runner = commandRunner ?? new ProcessCommandRunner();

    public IReadOnlyList<ExistingPoolState> GetPools()
    {
        // Ownership is the tag, not the name: vgs/pvs happily report every VG/PV on the host, including
        // ones DiskWeaver never touched (manual mdadm+LVM layouts, other tools). Only a VG carrying
        // DiskWeaverPoolTag.Value -- applied by CommandPlanner.Build's vgcreate --addtag -- is treated
        // as DiskWeaver's; everything else is filtered out before a single ExistingPoolState is built,
        // so callers (including the teardown endpoint) can never see or act on a pool that isn't ours.
        var ownedVgNames = RunJson("vgs", ["--reportformat", "json", "-o", "vg_name,vg_tags"], ExecutorJsonContext.Default.VgsDocument)
            .Report.SelectMany(r => r.Vg).Where(vg => vg.HasTag(DiskWeaverPoolTag.Value)).Select(vg => vg.VgName).ToHashSet();

        var pvs = RunJson("pvs", ["--reportformat", "json", "-o", "pv_name,vg_name,pv_tags"], ExecutorJsonContext.Default.PvsDocument)
            .Report.SelectMany(r => r.Pv).Where(pv => pv.VgName.Length > 0 && ownedVgNames.Contains(pv.VgName)).ToList();

        var lvs = RunJson("lvs", ["--reportformat", "json", "-o", "vg_name,lv_name"], ExecutorJsonContext.Default.LvsDocument)
            .Report.SelectMany(r => r.Lv).ToList();

        var volumeNameByVg = lvs
            .GroupBy(l => l.VgName)
            .ToDictionary(g => g.Key, g => g.First().LvName);

        // Read once and shared across every tier below, rather than one call per array -- /proc/mdstat
        // already covers the whole host in one shot. Only arrays currently mid recovery/resync/reshape/
        // check show up here at all; an array with no entry is simply fully in sync.
        var syncStatusByArrayDevice = ProcMdstatParser.ParseSyncStatus(_runner.Run("cat", ["/proc/mdstat"]));

        var pools = new List<ExistingPoolState>();

        foreach (var vgGroup in pvs.GroupBy(pv => pv.VgName))
        {
            var volumeName = volumeNameByVg.GetValueOrDefault(vgGroup.Key, "data");
            var tiers = new List<ExistingTier>();
            string? error = null;

            foreach (var pv in vgGroup)
            {
                // A PV backed by a currently-inactive/missing mdadm array (e.g. after a reboot that
                // raced incremental assembly, or a member disk that's failed) reports as the literal
                // string "[unknown]" here rather than a real device path -- confirmed live. Passing
                // that straight to `mdadm --detail --export` doesn't fail gracefully, it errors with
                // "cannot open [unknown]: No such file or directory", so it's checked for up front
                // rather than caught after the fact.
                if (pv.PvName == "[unknown]")
                {
                    error = $"Volume group '{vgGroup.Key}' has a physical volume backed by an array that "
                        + "isn't currently running (device unknown to LVM) -- reassemble the underlying "
                        + "mdadm array (see /proc/mdstat) before this pool's full state can be read.";
                    continue;
                }

                try
                {
                    var syncStatus = syncStatusByArrayDevice.GetValueOrDefault(pv.PvName);
                    tiers.Add(DescribeTier(pv.PvName, pv.HasTag("diskweaver-unprotected"), syncStatus));
                }
                catch (InvalidOperationException ex)
                {
                    error = $"Volume group '{vgGroup.Key}', physical volume '{pv.PvName}': {ex.Message}";
                }
            }

            pools.Add(new ExistingPoolState(vgGroup.Key, volumeName, tiers, error));
        }

        return pools;
    }

    private ExistingTier DescribeTier(string arrayDevice, bool isUnprotectedByDesign, ProcMdstatParser.MdstatSyncStatus? syncStatus)
    {
        var export = _runner.Run("mdadm", ["--detail", "--export", arrayDevice]);
        var (raidLevel, partitionPaths, configuredMemberCount) = MdadmDetailParser.Parse(export);
        var diskIds = partitionPaths.Select(PartitionNaming.ToDiskId).ToList();

        // ExistingTier.SegmentSizeBytes is the per-disk slice size (matching Tier.SegmentSizeBytes),
        // not the array's usable RAID capacity -- blockdev on the array device itself would return
        // (n-1)x/(n-2)x that for RAID5/6, not the segment size BuildIncremental compares against.
        // Every member partition is the same size by construction, so any one of them gives it.
        var sizeText = _runner.Run("blockdev", ["--getsize64", partitionPaths[0]]).Trim();
        var segmentSizeBytes = long.Parse(sizeText);

        return new ExistingTier(
            arrayDevice, segmentSizeBytes, diskIds, raidLevel, partitionPaths, configuredMemberCount, isUnprotectedByDesign,
            syncStatus?.Operation, syncStatus?.PercentComplete, syncStatus?.SpeedKBps, syncStatus?.EtaMinutes);
    }

    private T RunJson<T>(string command, string[] arguments, JsonTypeInfo<T> typeInfo)
    {
        var output = _runner.Run(command, arguments);
        return JsonSerializer.Deserialize(output, typeInfo)
            ?? throw new InvalidOperationException($"{command} produced no output.");
    }
}
