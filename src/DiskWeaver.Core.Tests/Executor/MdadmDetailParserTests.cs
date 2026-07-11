using DiskWeaver.Planner;

namespace DiskWeaver.Executor.Tests;

public class MdadmDetailParserTests
{
    [Fact]
    public void ParsesLevelAndMemberDevicesInRoleOrder()
    {
        // Captured verbatim from a real `mdadm --detail --export` on a 3-disk RAID5 array --
        // the device is keyed by its own name (dev_loop0p1), not a plain numeric index, and
        // ROLE (not the key's device name or line order) is what determines array position.
        const string export = """
            MD_LEVEL=raid5
            MD_DEVICES=3
            MD_METADATA=1.2
            MD_UUID=f81d1f15:a115d57d:4f9212d1:8157d05b
            MD_DEVNAME=diskweaver-tier0
            MD_RESHAPE_ACTIVE=False
            MD_NAME=ws-noel:diskweaver-tier0
            MD_DEVICE_dev_loop0p1_ROLE=0
            MD_DEVICE_dev_loop0p1_DEV=/dev/loop0p1
            MD_DEVICE_dev_loop2p1_ROLE=2
            MD_DEVICE_dev_loop2p1_DEV=/dev/loop2p1
            MD_DEVICE_dev_loop1p1_ROLE=1
            MD_DEVICE_dev_loop1p1_DEV=/dev/loop1p1
            """;

        var (raidLevel, partitionPaths, configuredMemberCount) = MdadmDetailParser.Parse(export);

        Assert.Equal(RaidLevel.Raid5, raidLevel);
        Assert.Equal(["/dev/loop0p1", "/dev/loop1p1", "/dev/loop2p1"], partitionPaths);
        Assert.Equal(3, configuredMemberCount);
    }

    [Fact]
    public void DegradedMirror_MissingOneSlot_ConfiguredMemberCountReflectsMdDevicesNotPresentDevices()
    {
        // Captured shape for a mirror deliberately (or accidentally, from a real disk failure)
        // missing one of its two configured slots: mdadm's export has no MD_DEVICE_dev_*
        // entry at all for a "missing" role -- only MD_DEVICES (the configured slot count)
        // reveals that a slot is absent. This is exactly the shape RedundancyLevel.None
        // produces (see Tier.DegradedSlots): one real member, MD_DEVICES=2.
        const string export = """
            MD_LEVEL=raid1
            MD_DEVICES=2
            MD_METADATA=1.2
            MD_UUID=f81d1f15:a115d57d:4f9212d1:8157d05b
            MD_DEVNAME=diskweaver-pool-tier0
            MD_DEVICE_dev_loop0p1_ROLE=0
            MD_DEVICE_dev_loop0p1_DEV=/dev/loop0p1
            """;

        var (raidLevel, partitionPaths, configuredMemberCount) = MdadmDetailParser.Parse(export);

        Assert.Equal(RaidLevel.Mirror, raidLevel);
        Assert.Equal(["/dev/loop0p1"], partitionPaths);
        Assert.Equal(2, configuredMemberCount);
    }

    [Theory]
    [InlineData("raid1", RaidLevel.Mirror)]
    [InlineData("raid5", RaidLevel.Raid5)]
    [InlineData("raid6", RaidLevel.Raid6)]
    public void MapsMdadmLevelNames(string mdadmLevel, RaidLevel expected)
    {
        var export = $"MD_LEVEL={mdadmLevel}\nMD_DEVICE_dev_loop0p1_DEV=/dev/loop0p1\nMD_DEVICE_dev_loop0p1_ROLE=0\n";

        var (raidLevel, _, _) = MdadmDetailParser.Parse(export);

        Assert.Equal(expected, raidLevel);
    }

    [Fact]
    public void UnknownLevel_Throws()
    {
        const string export = "MD_LEVEL=raid10\nMD_DEVICE_dev_loop0p1_DEV=/dev/loop0p1\nMD_DEVICE_dev_loop0p1_ROLE=0\n";

        Assert.Throws<FormatException>(() => MdadmDetailParser.Parse(export));
    }

    [Fact]
    public void MissingLevel_Throws()
    {
        const string export = "MD_DEVICE_dev_loop0p1_DEV=/dev/loop0p1\nMD_DEVICE_dev_loop0p1_ROLE=0\n";

        Assert.Throws<FormatException>(() => MdadmDetailParser.Parse(export));
    }

    [Fact]
    public void NoDeviceEntries_Throws()
    {
        const string export = "MD_LEVEL=raid5\n";

        Assert.Throws<FormatException>(() => MdadmDetailParser.Parse(export));
    }
}
