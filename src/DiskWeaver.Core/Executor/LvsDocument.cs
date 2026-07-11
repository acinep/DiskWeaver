using System.Text.Json.Serialization;

namespace DiskWeaver.Executor;

/// <summary>Root object of `lvs --reportformat json` output.</summary>
internal sealed class LvsDocument
{
    [JsonPropertyName("report")]
    public List<LvsReportEntry> Report { get; set; } = [];
}
