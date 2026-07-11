using System.Text.Json.Serialization;

namespace DiskWeaver.Executor;

/// <summary>Source-generated JSON type info for pvs/lvs report parsing -- required for AOT-safe deserialization.</summary>
[JsonSerializable(typeof(PvsDocument))]
[JsonSerializable(typeof(LvsDocument))]
[JsonSerializable(typeof(VgsDocument))]
[JsonSerializable(typeof(ExecutionJournal))]
internal partial class ExecutorJsonContext : JsonSerializerContext;
