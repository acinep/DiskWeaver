using DiskWeaver.Core.Inventory.Abstractions;

namespace DiskWeaver.Inventory;

/// <summary>
/// Parses `/proc/mdstat` to find which partitions are currently members of a live (assembled)
/// mdadm array -- pure and platform-independent, like <see cref="LsblkOutputParser"/>. Deliberately
/// doesn't care about RAID level or active-vs-spare role: for the "don't let mdadm --zero-superblock
/// fail on a live member" check this exists for, any member at all (including a spare added
/// mid-reshape) is reason enough to refuse. See <see cref="IArrayMembershipSource"/>.
/// </summary>
public static class ProcMdstatParser
{
    /// <summary>Maps each member partition's full device path (e.g. "/dev/loop3p1") to its array's device path (e.g. "/dev/md127").</summary>
    public static IReadOnlyDictionary<string, string> ParseArrayMembership(string mdstatContent)
    {
        var result = new Dictionary<string, string>();

        foreach (var rawLine in mdstatContent.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            // Array summary lines start at column 0 ("md127 : active raid1 ..."); everything else
            // (blank lines, indented stat lines, "Personalities :", "unused devices:") is skipped.
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || !line.StartsWith("md", StringComparison.Ordinal))
            {
                continue;
            }

            var colonIndex = line.IndexOf(':');
            if (colonIndex < 0)
            {
                continue;
            }

            var arrayDevice = $"/dev/{line[..colonIndex].Trim()}";
            var tokens = line[(colonIndex + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // tokens[0] is "active"/"inactive", tokens[1] is the level (e.g. "raid1") -- neither is
            // a member device, so both are skipped; everything after is a member, each optionally
            // suffixed with its role, e.g. "loop3p1[2](S)" for a spare or "sdb1[0]" for an active one.
            foreach (var token in tokens.Skip(2))
            {
                var bracketIndex = token.IndexOf('[');
                var memberName = bracketIndex < 0 ? token : token[..bracketIndex];
                if (memberName.Length > 0)
                {
                    result[$"/dev/{memberName}"] = arrayDevice;
                }
            }
        }

        return result;
    }
}
