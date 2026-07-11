using DiskWeaver.Cli;
using DiskWeaver.Planner;

namespace DiskWeaver.Cli.Tests;

public class DiskSourceResolverTests
{
    private static readonly Disk[] Disks =
    [
        new Disk("/dev/loop0", 2_000_000_000),
        new Disk("/dev/loop1", 2_000_000_000),
        new Disk("/dev/disk/by-id/wwn-0x50014ee20ff130cf", 4_000_787_030_016),
    ];

    [Fact]
    public void SelectsByExactId()
    {
        var selected = DiskSourceResolver.Select(Disks, "/dev/loop0");

        var disk = Assert.Single(selected);
        Assert.Equal("/dev/loop0", disk.Id);
    }

    [Fact]
    public void SelectsByTrailingComponent()
    {
        var selected = DiskSourceResolver.Select(Disks, "wwn-0x50014ee20ff130cf");

        var disk = Assert.Single(selected);
        Assert.Equal("/dev/disk/by-id/wwn-0x50014ee20ff130cf", disk.Id);
    }

    [Fact]
    public void SelectsMultiple_PreservingRequestOrder()
    {
        var selected = DiskSourceResolver.Select(Disks, "loop1,loop0");

        Assert.Equal(["/dev/loop1", "/dev/loop0"], selected.Select(d => d.Id));
    }

    [Fact]
    public void UnknownName_ThrowsWithAvailableListedInMessage()
    {
        var ex = Assert.Throws<ArgumentException>(() => DiskSourceResolver.Select(Disks, "loopX"));

        Assert.Contains("loopX", ex.Message);
        Assert.Contains("/dev/loop0", ex.Message);
    }
}
