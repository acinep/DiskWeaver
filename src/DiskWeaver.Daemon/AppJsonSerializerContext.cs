using System.Text.Json.Serialization;
using DiskWeaver.Executor;
using DiskWeaver.Planner;

namespace DiskWeaver.Daemon;

/// <summary>
/// Source-generated JSON type info for everything the daemon serializes/deserializes --
/// required for reflection-free (Native AOT) serialization. See Program.cs wiring.
/// </summary>
[JsonSerializable(typeof(PlanRequest))]
[JsonSerializable(typeof(PlanResponse))]
[JsonSerializable(typeof(IReadOnlyList<Disk>))]
[JsonSerializable(typeof(IReadOnlyList<ExistingPoolState>))]
[JsonSerializable(typeof(ExecutionJournal))]
[JsonSerializable(typeof(ExpansionRequest))]
[JsonSerializable(typeof(ExpansionOption))]
[JsonSerializable(typeof(ExpansionOptionsResponse))]
[JsonSerializable(typeof(WipeRequest))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(SessionResponse))]
internal partial class AppJsonSerializerContext : JsonSerializerContext;
