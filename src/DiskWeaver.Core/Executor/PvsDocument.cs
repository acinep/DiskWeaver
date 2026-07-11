using System.Text.Json.Serialization;

namespace DiskWeaver.Executor;

/// <summary>Root object of `pvs --reportformat json` output.</summary>
internal sealed class PvsDocument
{
    [JsonPropertyName("report")]
    public List<PvsReportEntry> Report { get; set; } = [];
}
