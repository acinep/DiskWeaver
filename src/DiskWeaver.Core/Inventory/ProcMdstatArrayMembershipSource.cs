using DiskWeaver.Core.Inventory.Abstractions;

namespace DiskWeaver.Inventory;

/// <summary>Reads real array membership from `/proc/mdstat` -- Linux-only, like <see cref="LsblkDiskInventorySource"/>.</summary>
public sealed class ProcMdstatArrayMembershipSource : IArrayMembershipSource
{
    public IReadOnlyDictionary<string, string> GetArrayMembership()
    {
        // No array assembled at all (e.g. a fresh box before any pool exists) means no /proc/mdstat
        // members, not an error -- callers should treat that the same as "nothing found".
        if (!File.Exists("/proc/mdstat"))
        {
            return new Dictionary<string, string>();
        }

        return ProcMdstatParser.ParseArrayMembership(File.ReadAllText("/proc/mdstat"));
    }
}
