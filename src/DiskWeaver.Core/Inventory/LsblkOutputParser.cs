using System.Text.Json;
using DiskWeaver.Planner;

namespace DiskWeaver.Inventory;

/// <summary>
/// Parses the JSON produced by `lsblk --json -b -o NAME,SIZE,TYPE,ID-LINK,FSTYPE,MOUNTPOINTS`
/// into planner <see cref="Disk"/> objects, including whether each one is safe to select for a
/// new plan (<see cref="Disk.IsBlank"/>). Pure and platform-independent — works from a captured
/// file just as well as from a live lsblk process, which is what makes it usable while developing
/// off-target (e.g. Windows).
/// </summary>
public static class LsblkOutputParser
{
    public static IReadOnlyList<Disk> ParseDisks(string json)
    {
        var document = JsonSerializer.Deserialize(json, LsblkJsonContext.Default.LsblkDocument)
            ?? throw new FormatException("lsblk output did not contain a 'blockdevices' array.");

        var disksAndLoops = document.BlockDevices.Where(d => d.Type is "disk" or "loop").ToList();
        foreach (var device in disksAndLoops)
        {
            EnsureSafetyColumnsPresent(device);
        }

        return disksAndLoops.Select(ToDisk).ToList();
    }

    /// <summary>
    /// Maps each disk id to its current partition device paths (e.g. "/dev/loop3p1"), read from
    /// lsblk's nested "children" -- used by `diskweaver wipe` to clear a stale/foreign RAID
    /// superblock on each partition (<c>mdadm --zero-superblock</c>) before wiping the parent
    /// disk's own signature, the same order <see cref="DiskWeaver.Executor.CommandPlanner.BuildTeardown"/>
    /// already relies on for a pool's own disks. Disks with no partitions map to an empty list.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParsePartitionPaths(string json)
    {
        var document = JsonSerializer.Deserialize(json, LsblkJsonContext.Default.LsblkDocument)
            ?? throw new FormatException("lsblk output did not contain a 'blockdevices' array.");

        var disksAndLoops = document.BlockDevices.Where(d => d.Type is "disk" or "loop");

        var result = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var device in disksAndLoops)
        {
            var id = device.IdLink is { Length: > 0 }
                ? $"/dev/disk/by-id/{device.IdLink}"
                : $"/dev/{device.Name}";

            IReadOnlyList<string> partitionPaths = (device.Children ?? [])
                .Where(c => c.Type == "part")
                .Select(c => $"/dev/{c.Name}")
                .ToList();

            result[id] = partitionPaths;
        }

        return result;
    }

    /// <summary>
    /// Refuses a capture that never asked lsblk for FSTYPE/MOUNTPOINTS at all (e.g. an old `lsblk
    /// --json -b -d -o NAME,SIZE,TYPE,ID-LINK` capture, predating <see cref="Disk.IsBlank"/>)
    /// rather than silently treating the missing columns as "confirmed blank." Without this, a
    /// stale <c>--lsblk-json</c> capture could still feed <c>diskweaver plan --script</c> and
    /// produce a real parted/mdadm/wipefs script for a disk that actually has data on it -- the
    /// exact gap <see cref="Disk.IsBlank"/> exists to close. See LsblkDevice's FsType/MountPoints
    /// doc comments for the Undefined-vs-Null distinction this relies on.
    /// </summary>
    private static void EnsureSafetyColumnsPresent(LsblkDevice device)
    {
        if (device.FsType.ValueKind == JsonValueKind.Undefined || device.MountPoints.ValueKind == JsonValueKind.Undefined)
        {
            throw new FormatException(
                $"lsblk capture for '{device.Name}' is missing the FSTYPE/MOUNTPOINTS columns, so DiskWeaver can't "
                + "tell whether it's actually blank. This looks like it came from an lsblk invocation that predates "
                + "disk-eligibility checking -- re-capture with `lsblk --json -b -o NAME,SIZE,TYPE,ID-LINK,FSTYPE,MOUNTPOINTS` "
                + "(no -d, so partitions/holders are visible too).");
        }
    }

    private static Disk ToDisk(LsblkDevice device)
    {
        var sizeBytes = device.Size.ValueKind switch
        {
            JsonValueKind.Number when device.Size.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(device.Size.GetString(), out var n) => n,
            _ => throw new FormatException(
                $"Disk '{device.Name}' has no numeric size — run lsblk with -b for raw byte sizes."),
        };

        var id = device.IdLink is { Length: > 0 }
            ? $"/dev/disk/by-id/{device.IdLink}"
            : $"/dev/{device.Name}";

        return new Disk(id, sizeBytes, IsBlank(device), IsLikelySystemDisk(device));
    }

    /// <summary>
    /// A disk is safe to hand to <c>parted mklabel</c>/<c>wipefs</c> only if it has no partitions or
    /// holder devices (children), no filesystem/RAID/LVM signature of its own, and isn't mounted
    /// itself. Any one of those means the disk already has data, or is already in use by something
    /// else (foreign mdadm/LVM, a filesystem) -- selecting it must be refused, not silently allowed
    /// through to a destructive command. See <see cref="DiskWeaver.Inventory.DiskSelector.EnsureBlank"/>
    /// for where that refusal happens. Only called once <see cref="EnsureSafetyColumnsPresent"/> has
    /// already confirmed FsType/MountPoints were actually requested, so Null here is a real answer.
    /// </summary>
    private static bool IsBlank(LsblkDevice device) =>
        device.Children is not { Count: > 0 }
        && !HasNonEmptyString(device.FsType)
        && MountPointsAllEmpty(device.MountPoints);

    private static bool HasNonEmptyString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 };

    private static bool MountPointsAllEmpty(JsonElement mountPoints) => mountPoints.ValueKind switch
    {
        JsonValueKind.Array => mountPoints.EnumerateArray().All(e => !HasNonEmptyString(e)),
        _ => true, // Null (nothing mounted) -- Undefined is already rejected by EnsureSafetyColumnsPresent.
    };

    /// <summary>
    /// Best-effort "this is probably the OS/boot disk" signal: true if this disk or any of its
    /// partitions (recursively, in case of nested holders like an LVM PV) is currently mounted at
    /// "/", "/boot", "/boot/efi"/"/boot/EFI", or as swap. Not exhaustive -- a disk can be important
    /// without being mounted right now -- see <see cref="Disk.IsLikelySystemDisk"/>.
    /// </summary>
    private static bool IsLikelySystemDisk(LsblkDevice device) =>
        CollectMountPoints(device).Any(mp =>
            mp is "/" or "/boot" or "/boot/efi" or "/boot/EFI" or "[SWAP]"
            || mp.StartsWith("/boot/", StringComparison.Ordinal));

    private static IEnumerable<string> CollectMountPoints(LsblkDevice device)
    {
        if (device.MountPoints.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in device.MountPoints.EnumerateArray())
            {
                if (HasNonEmptyString(element))
                {
                    yield return element.GetString()!;
                }
            }
        }

        foreach (var child in device.Children ?? [])
        {
            foreach (var mountPoint in CollectMountPoints(child))
            {
                yield return mountPoint;
            }
        }
    }
}
