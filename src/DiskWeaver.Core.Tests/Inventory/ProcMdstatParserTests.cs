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
    public void AutoReadOnlyArray_DoesNotTreatTheRaidLevelAsAPhantomMember()
    {
        // Regression test for a real bug found live: "active (auto-read-only) raid5 ..." (a
        // freshly-assembled array the kernel hasn't seen a write to yet) has an extra state token
        // between "active" and the RAID level that ParseArrayMembership's old fixed Skip(2) didn't
        // account for -- it shifted every token by one, so "raid5" itself got treated as a member
        // device (mapped to "/dev/raid5"), while still (by luck) keeping every real member too.
        const string mdstat = """
            Personalities : [raid1] [raid4] [raid5] [raid6]
            md126 : active (auto-read-only) raid5 sdi2[1] sdh2[0] sdf2[6] sde2[4] sdk2[3] sdj2[2]
                  19534379520 blocks super 1.2 level 5, 512k chunk, algorithm 2 [6/6] [UUUUUU]
                  bitmap: 0/30 pages [0KB], 65536KB chunk

            unused devices: <none>
            """;

        var membership = ProcMdstatParser.ParseArrayMembership(mdstat);

        Assert.Equal("/dev/md126", membership["/dev/sdi2"]);
        Assert.Equal("/dev/md126", membership["/dev/sdh2"]);
        Assert.Equal("/dev/md126", membership["/dev/sdf2"]);
        Assert.Equal("/dev/md126", membership["/dev/sde2"]);
        Assert.Equal("/dev/md126", membership["/dev/sdk2"]);
        Assert.Equal("/dev/md126", membership["/dev/sdj2"]);
        Assert.False(membership.ContainsKey("/dev/raid5"));
        Assert.Equal(6, membership.Count);
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

    [Fact]
    public void RecoveryInProgress_ParsesPercentSpeedAndEta()
    {
        const string mdstat = """
            Personalities : [raid5]
            md127 : active raid5 sda1[0] sdb1[1]
                  35161956864 blocks super 1.2 level 5, 512k chunk, algorithm 2 [10/9] [UUUUUUUUU_]
                  [================>....]  recovery = 82.1% (3209047328/3906884096) finish=31170.0min speed=373K/sec
                  bitmap: 1/30 pages [4KB], 65536KB chunk

            unused devices: <none>
            """;

        var status = ProcMdstatParser.ParseSyncStatus(mdstat)["/dev/md127"];

        Assert.Equal("recovery", status.Operation);
        Assert.Equal(82.1, status.PercentComplete);
        Assert.Equal(373, status.SpeedKBps);
        Assert.Equal(31170.0, status.EtaMinutes);
    }

    [Fact]
    public void ResyncPending_ReportsZeroPercent_NoSpeedOrEta()
    {
        const string mdstat = """
            Personalities : [raid1]
            md125 : active (auto-read-only) raid1 sdf3[1] sde3[0]
                  5858223744 blocks super 1.2 [2/2] [UU]
                  resync=PENDING
                  bitmap: 44/44 pages [176KB], 65536KB chunk

            unused devices: <none>
            """;

        var status = ProcMdstatParser.ParseSyncStatus(mdstat)["/dev/md125"];

        Assert.Equal("resync", status.Operation);
        Assert.Equal(0, status.PercentComplete);
        Assert.Null(status.SpeedKBps);
        Assert.Null(status.EtaMinutes);
    }

    [Fact]
    public void FullyInSyncArray_HasNoEntry()
    {
        const string mdstat = """
            Personalities : [raid5]
            md126 : active raid5 sdf2[6] sde2[4] sdg2[0]
                  19534379520 blocks super 1.2 level 5, 512k chunk, algorithm 2 [3/3] [UUU]
                  bitmap: 0/30 pages [0KB], 65536KB chunk

            unused devices: <none>
            """;

        var statuses = ProcMdstatParser.ParseSyncStatus(mdstat);

        Assert.Empty(statuses);
    }

    [Fact]
    public void MultipleArrays_OnlyRecoveringOneGetsAnEntry()
    {
        const string mdstat = """
            Personalities : [raid1] [raid5]
            md126 : active raid5 sdf2[6] sde2[4] sdg2[0] sdh2[1] sdj2[3] sdi2[2]
                  19534379520 blocks super 1.2 level 5, 512k chunk, algorithm 2 [6/5] [UUUUU_]
                  bitmap: 1/30 pages [4KB], 65536KB chunk

            md127 : active raid5 sda1[0] sdb1[1]
                  35161956864 blocks super 1.2 level 5, 512k chunk, algorithm 2 [10/9] [UUUUUUUUU_]
                  [================>....]  recovery = 82.1% (3209047328/3906884096) finish=1027.9min speed=11314K/sec
                  bitmap: 1/30 pages [4KB], 65536KB chunk

            unused devices: <none>
            """;

        var statuses = ProcMdstatParser.ParseSyncStatus(mdstat);

        Assert.False(statuses.ContainsKey("/dev/md126"));
        Assert.True(statuses.ContainsKey("/dev/md127"));
        Assert.Equal(82.1, statuses["/dev/md127"].PercentComplete);
    }
}
