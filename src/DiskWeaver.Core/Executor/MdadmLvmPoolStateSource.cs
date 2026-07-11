using DiskWeaver.Core.Executor.Abstractions;
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

        var pools = new List<ExistingPoolState>();

        foreach (var vgGroup in pvs.GroupBy(pv => pv.VgName))
        {
            var tiers = vgGroup.Select(pv => DescribeTier(pv.PvName, pv.HasTag("diskweaver-unprotected"))).ToList();
            var volumeName = volumeNameByVg.GetValueOrDefault(vgGroup.Key, "data");
            pools.Add(new ExistingPoolState(vgGroup.Key, volumeName, tiers));
        }

        return pools;
    }

    private ExistingTier DescribeTier(string arrayDevice, bool isUnprotectedByDesign)
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

        return new ExistingTier(arrayDevice, segmentSizeBytes, diskIds, raidLevel, partitionPaths, configuredMemberCount, isUnprotectedByDesign);
    }

    private T RunJson<T>(string command, string[] arguments, JsonTypeInfo<T> typeInfo)
    {
        var output = _runner.Run(command, arguments);
        return JsonSerializer.Deserialize(output, typeInfo)
            ?? throw new InvalidOperationException($"{command} produced no output.");
    }
}
