using System.Text.Json.Serialization;

namespace DiskWeaver.Inventory;

/// <summary>Source-generated JSON type info for lsblk's output -- required for AOT-safe deserialization.</summary>
[JsonSerializable(typeof(LsblkDocument))]
internal partial class LsblkJsonContext : JsonSerializerContext;
