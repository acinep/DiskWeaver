using System.Globalization;

namespace DiskWeaver.Cli;

/// <summary>Parses human-friendly disk size strings like "4TB" or "500GB" into bytes.</summary>
public static class DiskSizeParser
{
    private static readonly (string Suffix, long Multiplier)[] Units =
    [
        ("TB", 1_000_000_000_000L),
        ("GB", 1_000_000_000L),
        ("MB", 1_000_000L),
        ("T", 1_000_000_000_000L),
        ("G", 1_000_000_000L),
        ("M", 1_000_000L),
        ("B", 1L),
    ];

    public static long ParseBytes(string text)
    {
        var trimmed = text.Trim();

        foreach (var (suffix, multiplier) in Units)
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var numberPart = trimmed[..^suffix.Length];
                var value = double.Parse(numberPart, CultureInfo.InvariantCulture);
                return (long)(value * multiplier);
            }
        }

        return long.Parse(trimmed, CultureInfo.InvariantCulture);
    }
}
