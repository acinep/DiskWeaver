using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskWeaver.Executor;

/// <summary>One physical volume as reported by `pvs --reportformat json -o pv_name,vg_name,pv_tags`.</summary>
internal sealed class PvEntry
{
    [JsonPropertyName("pv_name")]
    public string PvName { get; set; } = "";

    [JsonPropertyName("vg_name")]
    public string VgName { get; set; } = "";

    // Same array-or-comma-separated-string handling as VgEntry.VgTags -- lvm2's json
    // reportformat represents a multi-value field like tags differently across versions.
    [JsonPropertyName("pv_tags")]
    public JsonElement PvTags { get; set; }

    public bool HasTag(string tag) => PvTags.ValueKind switch
    {
        JsonValueKind.Array => PvTags.EnumerateArray().Any(e => e.GetString() == tag),
        JsonValueKind.String => (PvTags.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(tag),
        _ => false,
    };
}
