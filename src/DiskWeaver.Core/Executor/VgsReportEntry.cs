using System.Text.Json.Serialization;

namespace DiskWeaver.Executor;

/// <summary>One entry in a `vgs --reportformat json` document's "report" array.</summary>
internal sealed class VgsReportEntry
{
    [JsonPropertyName("vg")]
    public List<VgEntry> Vg { get; set; } = [];
}
