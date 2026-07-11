using System.Text.Json.Serialization;

namespace DiskWeaver.Executor;

/// <summary>One entry in a `lvs --reportformat json` document's "report" array.</summary>
internal sealed class LvsReportEntry
{
    [JsonPropertyName("lv")]
    public List<LvEntry> Lv { get; set; } = [];
}
