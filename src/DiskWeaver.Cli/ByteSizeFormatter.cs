using System.Globalization;

namespace DiskWeaver.Cli;

/// <summary>Formats a byte count as a human-friendly decimal size, e.g. "4.00 TB".</summary>
public static class ByteSizeFormatter
{
    public static string Format(long bytes)
    {
        const double Tb = 1_000_000_000_000d;
        const double Gb = 1_000_000_000d;

        return bytes >= (long)Tb
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / Tb:0.00} TB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / Gb:0.00} GB");
    }
}
