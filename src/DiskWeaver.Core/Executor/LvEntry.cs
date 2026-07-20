using System.Text.Json.Serialization;

namespace DiskWeaver.Executor;

/// <summary>One logical volume as reported by `lvs --reportformat json -o vg_name,lv_name,pool_lv`.</summary>
internal sealed class LvEntry
{
    [JsonPropertyName("vg_name")]
    public string VgName { get; set; } = "";

    [JsonPropertyName("lv_name")]
    public string LvName { get; set; } = "";

    /// <summary>
    /// Non-empty when this LV is a thin volume carved from a thin pool -- names the pool LV it
    /// depends on. Used only to order teardown's lvremove calls (thin volumes before their pool);
    /// DiskWeaver doesn't otherwise treat thin volumes any differently from a plain LV.
    /// </summary>
    [JsonPropertyName("pool_lv")]
    public string PoolLv { get; set; } = "";
}
