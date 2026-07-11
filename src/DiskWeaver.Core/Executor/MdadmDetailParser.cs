using DiskWeaver.Planner;

namespace DiskWeaver.Executor;

/// <summary>
/// Parses the KEY=VALUE lines produced by `mdadm --detail --export &lt;array&gt;` into a RAID
/// level and its member partition device paths, ordered by RAID role. Pure and
/// platform-independent -- works from a captured file just as well as a live mdadm process.
/// </summary>
public static class MdadmDetailParser
{
    private const string DevicePrefix = "MD_DEVICE_dev_";

    public static (RaidLevel RaidLevel, IReadOnlyList<string> PartitionPaths, int ConfiguredMemberCount) Parse(string export)
    {
        string? level = null;
        int? configuredMemberCount = null;

        // Each member device is keyed by its own name (not a plain index), e.g.
        // MD_DEVICE_dev_loop0p1_DEV=/dev/loop0p1 paired with
        // MD_DEVICE_dev_loop0p1_ROLE=0 -- the role gives the position in the array,
        // the device name in the key is just an arbitrary identifier.
        var devByKey = new Dictionary<string, string>();
        var roleByKey = new Dictionary<string, int>();

        foreach (var rawLine in export.Split('\n'))
        {
            var line = rawLine.Trim();
            var eq = line.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            var key = line[..eq];
            var value = line[(eq + 1)..];

            if (key == "MD_LEVEL")
            {
                level = value;
            }
            else if (key == "MD_DEVICES" && int.TryParse(value, out var devices))
            {
                configuredMemberCount = devices;
            }
            else if (key.StartsWith(DevicePrefix, StringComparison.Ordinal) && key.EndsWith("_DEV", StringComparison.Ordinal))
            {
                devByKey[key[DevicePrefix.Length..^"_DEV".Length]] = value;
            }
            else if (key.StartsWith(DevicePrefix, StringComparison.Ordinal) && key.EndsWith("_ROLE", StringComparison.Ordinal)
                && int.TryParse(value, out var role))
            {
                roleByKey[key[DevicePrefix.Length..^"_ROLE".Length]] = role;
            }
        }

        if (level is null)
        {
            throw new FormatException("mdadm --detail --export output is missing MD_LEVEL.");
        }

        if (devByKey.Count == 0)
        {
            throw new FormatException("mdadm --detail --export output has no MD_DEVICE_dev_*_DEV entries.");
        }

        var pathsByRole = new SortedDictionary<int, string>();
        foreach (var (deviceKey, path) in devByKey)
        {
            if (roleByKey.TryGetValue(deviceKey, out var role))
            {
                pathsByRole[role] = path;
            }
        }

        if (pathsByRole.Count == 0)
        {
            throw new FormatException("mdadm --detail --export output has device entries but no matching ROLE entries.");
        }

        // Falls back to the present-device count when MD_DEVICES itself is missing from the
        // export (shouldn't happen with real mdadm output, but keeps this parser lenient rather
        // than throwing on an otherwise-valid capture) -- that fallback is exactly "no missing
        // slots," i.e. today's default healthy-array assumption.
        return (ToRaidLevel(level), pathsByRole.Values.ToList(), configuredMemberCount ?? pathsByRole.Count);
    }

    private static RaidLevel ToRaidLevel(string mdadmLevel) => mdadmLevel switch
    {
        "raid1" => RaidLevel.Mirror,
        "raid5" => RaidLevel.Raid5,
        "raid6" => RaidLevel.Raid6,
        _ => throw new FormatException($"Unsupported mdadm level '{mdadmLevel}' -- DiskWeaver only builds raid1/5/6 arrays."),
    };
}
