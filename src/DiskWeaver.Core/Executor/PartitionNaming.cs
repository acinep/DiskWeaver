namespace DiskWeaver.Executor;

/// <summary>
/// Converts between a disk id (e.g. /dev/loop0, /dev/disk/by-id/ata-WDC1) and the partition
/// device path for a given partition number, and back. By-id paths always take a `-partN`
/// suffix (a stable udev convention). Raw kernel device names differ: names ending in a digit
/// (loop0, nvme0n1) get a `pN` suffix to disambiguate; names that don't (sda) just get the
/// number appended directly.
/// </summary>
public static class PartitionNaming
{
    public static string ToPartitionPath(string diskId, int partitionNumber)
    {
        if (diskId.Contains("/by-id/", StringComparison.Ordinal))
        {
            return $"{diskId}-part{partitionNumber}";
        }

        return char.IsDigit(diskId[^1])
            ? $"{diskId}p{partitionNumber}"
            : $"{diskId}{partitionNumber}";
    }

    /// <summary>Reverses <see cref="ToPartitionPath"/>: recovers the disk id a partition path belongs to.</summary>
    public static string ToDiskId(string partitionPath)
    {
        if (partitionPath.Contains("/by-id/", StringComparison.Ordinal))
        {
            var dashPartIndex = partitionPath.LastIndexOf("-part", StringComparison.Ordinal);
            if (dashPartIndex < 0)
            {
                throw new FormatException(
                    $"'{partitionPath}' doesn't look like a by-id partition path (expected a '-partN' suffix).");
            }

            return partitionPath[..dashPartIndex];
        }

        var end = partitionPath.Length;
        while (end > 0 && char.IsDigit(partitionPath[end - 1]))
        {
            end--;
        }

        if (end == partitionPath.Length)
        {
            throw new FormatException(
                $"'{partitionPath}' doesn't look like a partition path (expected a trailing partition number).");
        }

        if (end > 1 && partitionPath[end - 1] == 'p' && char.IsDigit(partitionPath[end - 2]))
        {
            end--;
        }

        return partitionPath[..end];
    }
}
