using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskWeaver.Executor;

/// <summary>One volume group as reported by `vgs --reportformat json -o vg_name,vg_tags`.</summary>
internal sealed class VgEntry
{
    [JsonPropertyName("vg_name")]
    public string VgName { get; set; } = "";

    // lvm2's json reportformat represents a multi-value field like tags as either a JSON array or
    // a single comma-separated string depending on version -- accept either rather than assuming one.
    [JsonPropertyName("vg_tags")]
    public JsonElement VgTags { get; set; }

    public bool HasTag(string tag) => VgTags.ValueKind switch
    {
        JsonValueKind.Array => VgTags.EnumerateArray().Any(e => e.GetString() == tag),
        JsonValueKind.String => (VgTags.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(tag),
        _ => false,
    };
}
