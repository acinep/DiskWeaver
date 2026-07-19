using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DiskWeaver.Core.Executor.Abstractions;
using DiskWeaver.Core.Inventory.Abstractions;
using DiskWeaver.Planner;

namespace DiskWeaver.Executor;

/// <summary>
/// Resolves <see cref="Disk.RaidLvmSignatureOwner"/> from "unknown" (all lsblk-only inventory
/// sources can report -- see <see cref="DiskWeaver.Inventory.LsblkOutputParser"/>) to "diskweaver"
/// or "foreign", for whichever disks it can: a disk's RAID/LVM signature only names a specific
/// owner once its array is assembled enough for `pvs`/`vgs` to read the underlying VG's
/// <see cref="DiskWeaverPoolTag"/>. Disks with no signature at all (<c>RaidLvmSignatureOwner ==
/// null</c>) are left untouched -- lsblk's FSTYPE already answers those definitively, no live LVM
/// lookup needed.
/// </summary>
public static class DiskSignatureOwnership
{
    public static IReadOnlyList<Disk> Annotate(
        IReadOnlyList<Disk> disks,
        IReadOnlyDictionary<string, IReadOnlyList<string>> partitionPathsByDiskId,
        IArrayMembershipSource arrayMembershipSource,
        ICommandRunner runner)
    {
        if (!disks.Any(d => d.RaidLvmSignatureOwner == "unknown"))
        {
            return disks;
        }

        // Only reports members of a currently-assembled array -- a disk whose array isn't running
        // right now (the common case for a leftover signature) simply won't appear here, and stays
        // "unknown" below rather than being guessed at.
        var arrayDeviceByMember = arrayMembershipSource.GetArrayMembership();

        var vgIsDiskWeaverManaged = RunJson(runner, "vgs", ["--reportformat", "json", "-o", "vg_name,vg_tags"], ExecutorJsonContext.Default.VgsDocument)
            .Report.SelectMany(r => r.Vg).ToDictionary(vg => vg.VgName, vg => vg.HasTag(DiskWeaverPoolTag.Value));

        var vgNameByPv = RunJson(runner, "pvs", ["--reportformat", "json", "-o", "pv_name,vg_name"], ExecutorJsonContext.Default.PvsDocument)
            .Report.SelectMany(r => r.Pv).Where(pv => pv.VgName.Length > 0)
            .ToDictionary(pv => pv.PvName, pv => pv.VgName);

        return disks.Select(disk =>
        {
            if (disk.RaidLvmSignatureOwner != "unknown")
            {
                return disk;
            }

            var memberDevices = (partitionPathsByDiskId.GetValueOrDefault(disk.Id) ?? [])
                .Append(disk.DevicePath ?? disk.Id);

            var arrayDevice = memberDevices.Select(path => arrayDeviceByMember.GetValueOrDefault(path))
                .FirstOrDefault(a => a is not null);

            if (arrayDevice is null)
            {
                return disk; // array not currently assembled -- stays "unknown"
            }

            var isDiskWeaver = vgNameByPv.TryGetValue(arrayDevice, out var vgName) && vgIsDiskWeaverManaged.GetValueOrDefault(vgName);
            return disk with { RaidLvmSignatureOwner = isDiskWeaver ? "diskweaver" : "foreign" };
        }).ToList();
    }

    private static T RunJson<T>(ICommandRunner runner, string command, string[] arguments, JsonTypeInfo<T> typeInfo)
    {
        var output = runner.Run(command, arguments);
        return JsonSerializer.Deserialize(output, typeInfo)
            ?? throw new InvalidOperationException($"{command} produced no output.");
    }
}
