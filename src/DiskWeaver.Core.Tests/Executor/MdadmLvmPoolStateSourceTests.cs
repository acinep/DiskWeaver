using DiskWeaver.Planner;

namespace DiskWeaver.Executor.Tests;

public class MdadmLvmPoolStateSourceTests
{
    [Fact]
    public void SegmentSizeBytes_ComesFromAMemberPartition_NotTheArrayDevice()
    {
        // Regression test for a real bug: the array's own usable RAID5 capacity ((n-1) x segment)
        // is deliberately NOT registered as a response here. If DescribeTier ever goes back to
        // querying blockdev on the array device instead of a member partition, the fake throws
        // "No canned response" instead of silently returning the wrong (array-capacity) number.
        var runner = new FakeCommandRunner();
        runner.Respond("vgs", ["--reportformat", "json", "-o", "vg_name,vg_tags"],
            """{"report":[{"vg":[{"vg_name":"diskweaver-pool","vg_tags":["diskweaver-managed"]}]}]}""");
        runner.Respond("pvs", ["--reportformat", "json", "-o", "pv_name,vg_name,pv_tags"],
            """{"report":[{"pv":[{"pv_name":"/dev/md127","vg_name":"diskweaver-pool"}]}]}""");
        runner.Respond("lvs", ["--reportformat", "json", "-o", "vg_name,lv_name"],
            """{"report":[{"lv":[{"vg_name":"diskweaver-pool","lv_name":"data"}]}]}""");
        runner.Respond("mdadm", ["--detail", "--export", "/dev/md127"], """
            MD_LEVEL=raid5
            MD_DEVICE_dev_loop0p1_DEV=/dev/loop0p1
            MD_DEVICE_dev_loop0p1_ROLE=0
            MD_DEVICE_dev_loop1p1_DEV=/dev/loop1p1
            MD_DEVICE_dev_loop1p1_ROLE=1
            MD_DEVICE_dev_loop2p1_DEV=/dev/loop2p1
            MD_DEVICE_dev_loop2p1_ROLE=2
            """);
        runner.Respond("blockdev", ["--getsize64", "/dev/loop0p1"], "2145386496\n");

        var source = new MdadmLvmPoolStateSource(runner);
        var pools = source.GetPools();

        var pool = Assert.Single(pools);
        Assert.Equal("diskweaver-pool", pool.PoolName);
        Assert.Equal("data", pool.VolumeName);
        var tier = Assert.Single(pool.Tiers);
        Assert.Equal("/dev/md127", tier.ArrayDevice);
        Assert.Equal(2145386496, tier.SegmentSizeBytes);
        Assert.Equal(RaidLevel.Raid5, tier.RaidLevel);
        Assert.Equal(["/dev/loop0", "/dev/loop1", "/dev/loop2"], tier.DiskIds);
    }

    [Fact]
    public void PvTaggedAsUnprotected_SetsIsUnprotectedByDesign()
    {
        var runner = new FakeCommandRunner();
        runner.Respond("vgs", ["--reportformat", "json", "-o", "vg_name,vg_tags"],
            """{"report":[{"vg":[{"vg_name":"diskweaver-pool","vg_tags":["diskweaver-managed"]}]}]}""");
        runner.Respond("pvs", ["--reportformat", "json", "-o", "pv_name,vg_name,pv_tags"],
            """{"report":[{"pv":[{"pv_name":"/dev/md127","vg_name":"diskweaver-pool","pv_tags":["diskweaver-unprotected"]}]}]}""");
        runner.Respond("lvs", ["--reportformat", "json", "-o", "vg_name,lv_name"],
            """{"report":[{"lv":[{"vg_name":"diskweaver-pool","lv_name":"data"}]}]}""");
        runner.Respond("mdadm", ["--detail", "--export", "/dev/md127"], """
            MD_LEVEL=raid1
            MD_DEVICES=2
            MD_DEVICE_dev_loop0p1_DEV=/dev/loop0p1
            MD_DEVICE_dev_loop0p1_ROLE=0
            """);
        runner.Respond("blockdev", ["--getsize64", "/dev/loop0p1"], "2145386496\n");

        var source = new MdadmLvmPoolStateSource(runner);
        var pool = Assert.Single(source.GetPools());
        var tier = Assert.Single(pool.Tiers);

        Assert.True(tier.IsUnprotectedByDesign);
        Assert.Equal(2, tier.ConfiguredMemberCountOrDefault);
    }

    [Fact]
    public void PvWithNoTags_IsNotUnprotectedByDesign()
    {
        var runner = new FakeCommandRunner();
        runner.Respond("vgs", ["--reportformat", "json", "-o", "vg_name,vg_tags"],
            """{"report":[{"vg":[{"vg_name":"diskweaver-pool","vg_tags":["diskweaver-managed"]}]}]}""");
        runner.Respond("pvs", ["--reportformat", "json", "-o", "pv_name,vg_name,pv_tags"],
            """{"report":[{"pv":[{"pv_name":"/dev/md127","vg_name":"diskweaver-pool"}]}]}""");
        runner.Respond("lvs", ["--reportformat", "json", "-o", "vg_name,lv_name"],
            """{"report":[{"lv":[{"vg_name":"diskweaver-pool","lv_name":"data"}]}]}""");
        runner.Respond("mdadm", ["--detail", "--export", "/dev/md127"], """
            MD_LEVEL=raid1
            MD_DEVICES=2
            MD_DEVICE_dev_loop0p1_DEV=/dev/loop0p1
            MD_DEVICE_dev_loop0p1_ROLE=0
            """);
        runner.Respond("blockdev", ["--getsize64", "/dev/loop0p1"], "2145386496\n");

        var source = new MdadmLvmPoolStateSource(runner);
        var pool = Assert.Single(source.GetPools());
        var tier = Assert.Single(pool.Tiers);

        // A real disk-failure degradation looks identical at the mdadm level (also
        // MD_DEVICES=2, 1 real member) -- only the absence of the PV tag distinguishes it.
        Assert.False(tier.IsUnprotectedByDesign);
        Assert.Equal(2, tier.ConfiguredMemberCountOrDefault);
    }

    [Fact]
    public void NoVolumeGroups_ReturnsNoPools()
    {
        var runner = new FakeCommandRunner();
        runner.Respond("vgs", ["--reportformat", "json", "-o", "vg_name,vg_tags"], """{"report":[{"vg":[]}]}""");
        runner.Respond("pvs", ["--reportformat", "json", "-o", "pv_name,vg_name,pv_tags"], """{"report":[{"pv":[]}]}""");
        runner.Respond("lvs", ["--reportformat", "json", "-o", "vg_name,lv_name"], """{"report":[{"lv":[]}]}""");

        var source = new MdadmLvmPoolStateSource(runner);

        Assert.Empty(source.GetPools());
    }

    [Fact]
    public void UntaggedVolumeGroup_IsNotTreatedAsADiskWeaverPool()
    {
        // A manually built mdadm+LVM layout (or any VG DiskWeaver didn't create) has no
        // diskweaver-managed tag, even if it happens to be named "diskweaver-pool" -- ownership is
        // the tag, never the name alone, so GetPools() must not surface it as ours to expose/teardown.
        var runner = new FakeCommandRunner();
        runner.Respond("vgs", ["--reportformat", "json", "-o", "vg_name,vg_tags"],
            """{"report":[{"vg":[{"vg_name":"diskweaver-pool","vg_tags":[]}]}]}""");
        runner.Respond("pvs", ["--reportformat", "json", "-o", "pv_name,vg_name,pv_tags"],
            """{"report":[{"pv":[{"pv_name":"/dev/md127","vg_name":"diskweaver-pool"}]}]}""");
        runner.Respond("lvs", ["--reportformat", "json", "-o", "vg_name,lv_name"],
            """{"report":[{"lv":[{"vg_name":"diskweaver-pool","lv_name":"data"}]}]}""");

        var source = new MdadmLvmPoolStateSource(runner);

        Assert.Empty(source.GetPools());
    }
}
