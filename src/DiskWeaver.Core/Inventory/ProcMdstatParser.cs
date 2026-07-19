using System.Globalization;
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
    /// <summary>
    /// An in-progress recovery/resync/reshape/check on one array, as reported by its progress line
    /// in `/proc/mdstat` (e.g. "recovery = 82.1% (.../...) finish=31170.0min speed=373K/sec"). Not
    /// present at all means the array is fully in sync -- there's no "0%, done" entry for a healthy
    /// array, only an absent one.
    /// </summary>
    public sealed record MdstatSyncStatus(string Operation, double PercentComplete, double? SpeedKBps, double? EtaMinutes);

    /// <summary>Maps each array's device path (e.g. "/dev/md127") to its in-progress sync status, for arrays currently mid-recovery/resync/reshape/check.</summary>
    public static IReadOnlyDictionary<string, MdstatSyncStatus> ParseSyncStatus(string mdstatContent)
    {
        var result = new Dictionary<string, MdstatSyncStatus>();
        string? currentArrayDevice = null;

        foreach (var rawLine in mdstatContent.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                currentArrayDevice = null;
                continue;
            }

            if (!char.IsWhiteSpace(line[0]) && line.StartsWith("md", StringComparison.Ordinal))
            {
                var colonIndex = line.IndexOf(':');
                currentArrayDevice = colonIndex < 0 ? null : $"/dev/{line[..colonIndex].Trim()}";
                continue;
            }

            if (currentArrayDevice is null)
            {
                continue;
            }

            var status = TryParseSyncLine(line.Trim());
            if (status is not null)
            {
                result[currentArrayDevice] = status;
            }
        }

        return result;
    }

    // Handles two shapes: an in-progress line with a percentage, e.g.
    // "[================>....]  recovery = 82.1% (3209047328/3906884096) finish=31170.0min speed=373K/sec",
    // and a not-yet-started one, e.g. "resync=PENDING" (no progress bar, no percent yet -- reported
    // as 0% so callers don't have to special-case a third state).
    private static MdstatSyncStatus? TryParseSyncLine(string trimmedLine)
    {
        var rest = trimmedLine;
        if (rest.StartsWith('['))
        {
            var closeBracket = rest.IndexOf(']');
            if (closeBracket < 0)
            {
                return null;
            }
            rest = rest[(closeBracket + 1)..].TrimStart();
        }

        var eqIndex = rest.IndexOf('=');
        if (eqIndex < 0)
        {
            return null;
        }

        var operation = rest[..eqIndex].Trim();
        var afterEq = rest[(eqIndex + 1)..].Trim();

        if (operation.Length == 0)
        {
            return null;
        }

        if (afterEq.StartsWith("PENDING", StringComparison.Ordinal))
        {
            return new MdstatSyncStatus(operation, 0, null, null);
        }

        var tokens = afterEq.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var percentToken = tokens.FirstOrDefault();
        if (percentToken is null || !percentToken.EndsWith('%')
            || !double.TryParse(percentToken[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return null;
        }

        double? speed = null;
        double? eta = null;
        foreach (var token in tokens)
        {
            if (token.StartsWith("speed=", StringComparison.Ordinal) && token.EndsWith("K/sec", StringComparison.Ordinal)
                && double.TryParse(token["speed=".Length..^"K/sec".Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSpeed))
            {
                speed = parsedSpeed;
            }
            else if (token.StartsWith("finish=", StringComparison.Ordinal) && token.EndsWith("min", StringComparison.Ordinal)
                && double.TryParse(token["finish=".Length..^"min".Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedEta))
            {
                eta = parsedEta;
            }
        }

        return new MdstatSyncStatus(operation, percent, speed, eta);
    }

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

            // tokens[0] is "active"/"inactive", optionally followed by one or more parenthesized
            // state annotations -- e.g. "active (auto-read-only) raid5 sdi2[1] ..." (confirmed
            // live: a freshly-assembled array the kernel hasn't seen a write to yet). Skipping a
            // fixed 2 tokens here used to treat the RAID level itself ("raid5") as a phantom member
            // whenever one of these annotations was present, since it shifted every token after
            // "active" by one. Skip "active"/"inactive", then any "(...)" tokens, then the level --
            // everything after that is a real member, each optionally suffixed with its role, e.g.
            // "loop3p1[2](S)" for a spare or "sdb1[0]" for an active one.
            var memberStart = 1;
            while (memberStart < tokens.Length && tokens[memberStart].StartsWith('('))
            {
                memberStart++;
            }
            memberStart++; // the level token, e.g. "raid1"/"raid5"

            foreach (var token in tokens.Skip(memberStart))
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
