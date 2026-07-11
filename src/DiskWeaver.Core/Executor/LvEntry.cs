using System.Text.Json.Serialization;

namespace DiskWeaver.Executor;

/// <summary>One logical volume as reported by `lvs --reportformat json -o vg_name,lv_name`.</summary>
internal sealed class LvEntry
{
    [JsonPropertyName("vg_name")]
    public string VgName { get; set; } = "";

    [JsonPropertyName("lv_name")]
    public string LvName { get; set; } = "";
}
