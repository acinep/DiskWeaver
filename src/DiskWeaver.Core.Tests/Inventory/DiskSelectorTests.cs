using DiskWeaver.Planner;

namespace DiskWeaver.Inventory.Tests;

public class DiskSelectorTests
{
    private static readonly Disk[] Disks =
    [
        new Disk("/dev/disk/by-id/loop0", 2_000_000_000_000),
        new Disk("/dev/disk/by-id/loop1", 2_000_000_000_000),
    ];

    [Fact]
    public void SameNameRequestedTwice_Throws()
    {
        // Regression test: two requested names resolving to the same disk (here, the literal name
        // repeated) must never silently produce a two-entry selection -- that would let one
        // physical disk masquerade as two separate redundant members.
        var ex = Assert.Throws<ArgumentException>(() => DiskSelector.Select(Disks, ["loop0", "loop0"]));

        Assert.Contains("loop0", ex.Message);
    }

    [Fact]
    public void FullIdAndTrailingNameForSameDisk_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => DiskSelector.Select(Disks, ["/dev/disk/by-id/loop0", "loop0"]));

        Assert.Contains("loop0", ex.Message);
    }

    [Fact]
    public void DistinctDisks_Succeeds()
    {
        var selected = DiskSelector.Select(Disks, ["loop0", "loop1"]);

        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void EnsureBlank_AllBlank_DoesNotThrow()
    {
        DiskSelector.EnsureBlank(Disks);
    }

    [Fact]
    public void EnsureBlank_NonBlankDisk_Throws()
    {
        var disks = new[] { Disks[0], Disks[1] with { IsBlank = false } };

        var ex = Assert.Throws<InvalidOperationException>(() => DiskSelector.EnsureBlank(disks));

        Assert.Contains("loop1", ex.Message);
    }
}
