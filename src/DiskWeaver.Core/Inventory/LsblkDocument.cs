using System.Text.Json.Serialization;

namespace DiskWeaver.Inventory;

/// <summary>Root object of `lsblk --json` output.</summary>
internal sealed class LsblkDocument
{
    [JsonPropertyName("blockdevices")]
    public List<LsblkDevice> BlockDevices { get; set; } = [];
}
