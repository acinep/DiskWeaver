using System.Text.Json.Serialization;

namespace DiskWeaver.Executor;

/// <summary>Root object of `vgs --reportformat json` output.</summary>
internal sealed class VgsDocument
{
    [JsonPropertyName("report")]
    public List<VgsReportEntry> Report { get; set; } = [];
}
