using System.Text.Json.Serialization;

namespace DiskWeaver.Executor;

/// <summary>One entry in a `pvs --reportformat json` document's "report" array.</summary>
internal sealed class PvsReportEntry
{
    [JsonPropertyName("pv")]
    public List<PvEntry> Pv { get; set; } = [];
}
