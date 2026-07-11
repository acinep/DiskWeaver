using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskWeaver.Inventory;

/// <summary>One entry in `lsblk --json`'s "blockdevices" array.</summary>
internal sealed class LsblkDevice
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    // util-linux versions disagree on whether -b produces a quoted string or a bare
    // number for "size" -- accept either and normalize in LsblkOutputParser.
    [JsonPropertyName("size")]
    public JsonElement Size { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("id-link")]
    public string? IdLink { get; set; }

    // JsonElement (not string?), like Size above, but for a different reason: an *absent* "fstype"
    // key (ValueKind == Undefined -- a property the deserializer never touched keeps its C#
    // default) must be distinguishable from an explicit `"fstype": null` (ValueKind == Null). The
    // former means this capture came from an lsblk invocation that never requested the column at
    // all (e.g. an old `-d -o NAME,SIZE,TYPE,ID-LINK` capture) -- unknown, not "confirmed blank" --
    // while the latter is a real, current answer of "no signature." See LsblkOutputParser.IsBlank.
    /// <summary>Filesystem/RAID/LVM signature directly on this device (e.g. "ext4", "linux_raid_member", "LVM2_member").</summary>
    [JsonPropertyName("fstype")]
    public JsonElement FsType { get; set; }

    /// <summary>Same Undefined-vs-Null distinction as <see cref="FsType"/> applies here.</summary>
    [JsonPropertyName("mountpoints")]
    public JsonElement MountPoints { get; set; }

    /// <summary>
    /// Partitions, or a holder device (e.g. an mdadm array/dm-lvm PV directly on a whole disk with
    /// no partition table) -- lsblk represents both as nested children in the tree. Only populated
    /// when lsblk is invoked without -d; see <see cref="LsblkDiskInventorySource"/>.
    /// </summary>
    [JsonPropertyName("children")]
    public List<LsblkDevice>? Children { get; set; }
}
