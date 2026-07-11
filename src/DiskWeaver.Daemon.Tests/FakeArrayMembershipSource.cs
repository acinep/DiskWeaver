using DiskWeaver.Core.Inventory.Abstractions;

namespace DiskWeaver.Daemon.Tests;

/// <summary>Canned array membership for daemon tests -- no real /proc/mdstat involved.</summary>
public sealed class FakeArrayMembershipSource : IArrayMembershipSource
{
    public IReadOnlyDictionary<string, string> Membership { get; set; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> GetArrayMembership() => Membership;
}
