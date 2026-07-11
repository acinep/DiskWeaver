using DiskWeaver.Inventory;

namespace DiskWeaver.Inventory.Tests;

public class ProcMdstatParserTests
{
    [Fact]
    public void ParsesActiveMembers_IncludingSpares()
    {
        // Realistic snippet: a 2-disk mirror (loop0p1/loop1p1, active) with two spares
        // (loop3p1/loop4p1) added by a grow that then failed partway through.
        const string mdstat = """
            Personalities : [raid1]
            md127 : active raid1 loop4p1[3](S) loop3p1[2](S) loop1p1[1] loop0p1[0]
                  2095104 blocks super 1.2 [2/2] [UU]

            unused devices: <none>
            """;

        var membership = ProcMdstatParser.ParseArrayMembership(mdstat);

        Assert.Equal("/dev/md127", membership["/dev/loop0p1"]);
        Assert.Equal("/dev/md127", membership["/dev/loop1p1"]);
        Assert.Equal("/dev/md127", membership["/dev/loop3p1"]);
        Assert.Equal("/dev/md127", membership["/dev/loop4p1"]);
    }

    [Fact]
    public void MultipleArrays_AreKeptSeparate()
    {
        const string mdstat = """
            Personalities : [raid1] [raid5]
            md0 : active raid5 sdb1[0] sdc1[1] sdd1[2]
                  4190208 blocks super 1.2 level 5, 512k chunk, algorithm 2 [3/3] [UUU]

            md127 : active raid1 sde1[0] sdf1[1]
                  2095104 blocks super 1.2 [2/2] [UU]

            unused devices: <none>
            """;

        var membership = ProcMdstatParser.ParseArrayMembership(mdstat);

        Assert.Equal("/dev/md0", membership["/dev/sdb1"]);
        Assert.Equal("/dev/md127", membership["/dev/sde1"]);
    }

    [Fact]
    public void NoArraysAssembled_ReturnsEmpty()
    {
        const string mdstat = """
            Personalities :
            unused devices: <none>
            """;

        var membership = ProcMdstatParser.ParseArrayMembership(mdstat);

        Assert.Empty(membership);
    }
}
