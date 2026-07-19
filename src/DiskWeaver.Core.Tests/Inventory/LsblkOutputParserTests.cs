using DiskWeaver.Inventory;

namespace DiskWeaver.Inventory.Tests;

public class LsblkOutputParserTests
{
    // "fstype": null, "mountpoints": [null] on every disk/loop entry -- a real, current lsblk
    // capture that requested these columns and found nothing (a blank disk), as opposed to a
    // capture that never requested them at all (see MissingSafetyColumns_ThrowsRatherThanDefaultingToBlank).
    private const string SampleJson = """
        {
           "blockdevices": [
              {"name": "sda", "size": "4000787030016", "type": "disk", "id-link": "ata-WDC_WD40_ABC123", "fstype": null, "mountpoints": [null]},
              {"name": "sda1", "size": "1073741824", "type": "part", "id-link": "ata-WDC_WD40_ABC123-part1"},
              {"name": "sdb", "size": "2000398934016", "type": "disk", "id-link": "ata-ST2000_XYZ789", "fstype": null, "mountpoints": [null]},
              {"name": "sr0", "size": "1073741312", "type": "rom", "id-link": null},
              {"name": "sdc", "size": "2000398934016", "type": "disk", "id-link": null, "fstype": null, "mountpoints": [null]}
           ]
        }
        """;

    [Fact]
    public void ParsesWholeDisksOnly_ExcludingPartitionsAndOtherTypes()
    {
        var disks = LsblkOutputParser.ParseDisks(SampleJson);

        Assert.Equal(3, disks.Count);
        Assert.DoesNotContain(disks, d => d.Id.Contains("part1"));
        Assert.DoesNotContain(disks, d => d.Id.Contains("sr0"));
    }

    [Fact]
    public void PrefersIdLinkOverDeviceName()
    {
        var disks = LsblkOutputParser.ParseDisks(SampleJson);

        var sda = disks.Single(d => d.SizeBytes == 4000787030016);
        Assert.Equal("/dev/disk/by-id/ata-WDC_WD40_ABC123", sda.Id);
    }

    [Fact]
    public void FallsBackToDevNameWhenIdLinkMissing()
    {
        var disks = LsblkOutputParser.ParseDisks(SampleJson);

        var sdc = disks.Single(d => d.Id == "/dev/sdc");
        Assert.Equal(2000398934016, sdc.SizeBytes);
    }

    [Fact]
    public void AcceptsNumericSize_NotJustQuotedString()
    {
        // Some util-linux versions emit "size" as a bare JSON number rather than
        // a quoted string, even with -b. Both forms must parse identically.
        const string json = """
            {"blockdevices": [
                {"name": "sda", "size": 4000787030016, "type": "disk", "id-link": "ata-WDC_WD40_ABC123",
                 "fstype": null, "mountpoints": [null]}
            ]}
            """;

        var disks = LsblkOutputParser.ParseDisks(json);

        var sda = Assert.Single(disks);
        Assert.Equal(4000787030016, sda.SizeBytes);
    }

    [Fact]
    public void ThrowsOnMissingSize()
    {
        const string json = """
            {"blockdevices":[{"name":"sda","type":"disk","id-link":null,"fstype":null,"mountpoints":[null]}]}
            """;

        Assert.Throws<FormatException>(() => LsblkOutputParser.ParseDisks(json));
    }

    [Fact]
    public void IncludesLoopDevices_ForLoopbackTestRigs()
    {
        // Loop devices (sparse-file-backed test disks) report type "loop", not "disk", and
        // have no /dev/disk/by-id entry -- this is how docs/testing.md's validation rig works.
        const string json = """
            {"blockdevices": [
                {"name": "loop0", "size": "2000000000000", "type": "loop", "id-link": null, "fstype": null, "mountpoints": [null]},
                {"name": "loop1", "size": "2000000000000", "type": "loop", "id-link": null, "fstype": null, "mountpoints": [null]}
            ]}
            """;

        var disks = LsblkOutputParser.ParseDisks(json);

        Assert.Equal(2, disks.Count);
        Assert.Contains(disks, d => d.Id == "/dev/loop0");
        Assert.Contains(disks, d => d.Id == "/dev/loop1");
    }

    [Fact]
    public void DiskWithNoChildrenFstypeOrMountpoint_IsBlank()
    {
        const string json = """
            {"blockdevices": [
                {"name": "sda", "size": "4000787030016", "type": "disk", "id-link": "ata-WDC_WD40_ABC123",
                 "fstype": null, "mountpoints": [null]}
            ]}
            """;

        var sda = Assert.Single(LsblkOutputParser.ParseDisks(json));

        Assert.True(sda.IsBlank);
    }

    [Fact]
    public void DiskWithAPartitionChild_IsNotBlank()
    {
        // A partition table (even one whose partitions are otherwise unused) is exactly the kind
        // of pre-existing state a fresh `parted mklabel gpt` must never silently overwrite.
        const string json = """
            {"blockdevices": [
                {"name": "sda", "size": "4000787030016", "type": "disk", "id-link": "ata-WDC_WD40_ABC123",
                 "fstype": null, "mountpoints": [null],
                 "children": [{"name": "sda1", "size": "1073741824", "type": "part", "id-link": null}]}
            ]}
            """;

        var sda = Assert.Single(LsblkOutputParser.ParseDisks(json));

        Assert.False(sda.IsBlank);
    }

    [Fact]
    public void DiskWithAWholeDiskFilesystemSignature_IsNotBlank()
    {
        // e.g. a disk formatted directly (no partition table) or left over from a torn-down
        // mdadm/LVM member -- fstype carries signatures like "ext4", "linux_raid_member",
        // "LVM2_member" even with no partition table and nothing currently mounted.
        const string json = """
            {"blockdevices": [
                {"name": "sdb", "size": "2000398934016", "type": "disk", "id-link": null,
                 "fstype": "linux_raid_member", "mountpoints": [null]}
            ]}
            """;

        var sdb = Assert.Single(LsblkOutputParser.ParseDisks(json));

        Assert.False(sdb.IsBlank);
        // lsblk alone can see a RAID/LVM signature exists but not whose it is -- "unknown" is the
        // only honest answer here; DiskSignatureOwnership.Annotate (daemon-side) is what can
        // upgrade this to "diskweaver"/"foreign" once it can check the live LVM tag.
        Assert.Equal("unknown", sdb.RaidLvmSignatureOwner);
    }

    [Fact]
    public void MountedDisk_IsNotBlank()
    {
        const string json = """
            {"blockdevices": [
                {"name": "sdc", "size": "2000398934016", "type": "disk", "id-link": null,
                 "fstype": "ext4", "mountpoints": ["/mnt/data"]}
            ]}
            """;

        var sdc = Assert.Single(LsblkOutputParser.ParseDisks(json));

        Assert.False(sdc.IsBlank);
        // A plain filesystem is never a DiskWeaver artifact -- no live LVM lookup needed to know that.
        Assert.Null(sdc.RaidLvmSignatureOwner);
    }

    [Fact]
    public void DiskWithLvm2MemberSignatureOnAPartition_ReportsUnknownSignatureOwner()
    {
        // The signature can sit one level down (a partition holding the LVM PV directly, no RAID
        // layer) rather than on the whole disk -- HasRaidOrLvmSignature must recurse into children.
        const string json = """
            {"blockdevices": [
                {"name": "sdd", "size": "2000398934016", "type": "disk", "id-link": null,
                 "fstype": null, "mountpoints": [null],
                 "children": [{"name": "sdd1", "size": "2000397000000", "type": "part", "id-link": null,
                               "fstype": "LVM2_member", "mountpoints": [null]}]}
            ]}
            """;

        var sdd = Assert.Single(LsblkOutputParser.ParseDisks(json));

        Assert.False(sdd.IsBlank);
        Assert.Equal("unknown", sdd.RaidLvmSignatureOwner);
    }

    [Fact]
    public void EveryDisk_HasKernelDevicePathRegardlessOfIdLink()
    {
        const string json = """
            {"blockdevices": [
                {"name": "sde", "size": "2000398934016", "type": "disk", "id-link": "ata-WDC_WD20_XYZ",
                 "fstype": null, "mountpoints": [null]}
            ]}
            """;

        var sde = Assert.Single(LsblkOutputParser.ParseDisks(json));

        Assert.Equal("/dev/disk/by-id/ata-WDC_WD20_XYZ", sde.Id);
        Assert.Equal("/dev/sde", sde.DevicePath);
    }

    [Fact]
    public void MissingFstypeColumn_ThrowsRatherThanDefaultingToBlank()
    {
        // Regression test: a capture from an lsblk invocation that never requested FSTYPE at all
        // (e.g. an old `-d -o NAME,SIZE,TYPE,ID-LINK` capture, predating disk-eligibility checking)
        // must be refused, not silently treated as "confirmed blank" -- otherwise a stale
        // --lsblk-json file could still drive `diskweaver plan --script` into producing a real
        // parted/mdadm/wipefs script for a disk that actually has data on it.
        const string json = """
            {"blockdevices": [
                {"name": "sda", "size": "4000787030016", "type": "disk", "id-link": null, "mountpoints": [null]}
            ]}
            """;

        var ex = Assert.Throws<FormatException>(() => LsblkOutputParser.ParseDisks(json));
        Assert.Contains("sda", ex.Message);
    }

    [Fact]
    public void MissingMountpointsColumn_ThrowsRatherThanDefaultingToBlank()
    {
        const string json = """
            {"blockdevices": [
                {"name": "sda", "size": "4000787030016", "type": "disk", "id-link": null, "fstype": null}
            ]}
            """;

        var ex = Assert.Throws<FormatException>(() => LsblkOutputParser.ParseDisks(json));
        Assert.Contains("sda", ex.Message);
    }

    [Fact]
    public void DiskWithRootPartition_IsLikelySystemDisk()
    {
        const string json = """
            {"blockdevices": [
                {"name": "sda", "size": "4000787030016", "type": "disk", "id-link": "ata-WDC_WD40_ABC123",
                 "fstype": null, "mountpoints": [null],
                 "children": [
                    {"name": "sda1", "size": "1073741824", "type": "part", "id-link": null, "fstype": "vfat", "mountpoints": ["/boot/efi"]},
                    {"name": "sda2", "size": "4000787030016", "type": "part", "id-link": null, "fstype": "ext4", "mountpoints": ["/"]}
                 ]}
            ]}
            """;

        var sda = Assert.Single(LsblkOutputParser.ParseDisks(json));

        Assert.True(sda.IsLikelySystemDisk);
    }

    [Fact]
    public void DiskWithSwapPartition_IsLikelySystemDisk()
    {
        const string json = """
            {"blockdevices": [
                {"name": "sda", "size": "4000787030016", "type": "disk", "id-link": null,
                 "fstype": null, "mountpoints": [null],
                 "children": [
                    {"name": "sda1", "size": "1073741824", "type": "part", "id-link": null, "fstype": "swap", "mountpoints": ["[SWAP]"]}
                 ]}
            ]}
            """;

        var sda = Assert.Single(LsblkOutputParser.ParseDisks(json));

        Assert.True(sda.IsLikelySystemDisk);
    }

    [Fact]
    public void DataDiskWithNoSystemMountpoints_IsNotLikelySystemDisk()
    {
        const string json = """
            {"blockdevices": [
                {"name": "sdb", "size": "2000398934016", "type": "disk", "id-link": null,
                 "fstype": null, "mountpoints": [null]}
            ]}
            """;

        var sdb = Assert.Single(LsblkOutputParser.ParseDisks(json));

        Assert.False(sdb.IsLikelySystemDisk);
    }

    [Fact]
    public void ParsePartitionPaths_MapsEachDiskToItsPartitionDevicePaths()
    {
        const string json = """
            {"blockdevices": [
                {"name": "loop3", "size": "4000787030016", "type": "loop", "id-link": null,
                 "fstype": null, "mountpoints": [null],
                 "children": [{"name": "loop3p1", "size": "1073741824", "type": "part", "id-link": null}]},
                {"name": "loop4", "size": "4000787030016", "type": "loop", "id-link": null,
                 "fstype": null, "mountpoints": [null]}
            ]}
            """;

        var partitionPaths = LsblkOutputParser.ParsePartitionPaths(json);

        Assert.Equal(["/dev/loop3p1"], partitionPaths["/dev/loop3"]);
        Assert.Empty(partitionPaths["/dev/loop4"]);
    }

    [Fact]
    public void MissingSafetyColumns_OnAPartitionOrOtherNonDiskType_IsIgnored()
    {
        // Only "disk"/"loop" top-level entries are ever turned into a Disk (and need IsBlank at
        // all) -- a "part"/"rom" entry lacking fstype/mountpoints must not make the whole capture
        // unparseable, since those entries were never going to be selected as a whole disk anyway.
        const string json = """
            {"blockdevices": [
                {"name": "sda", "size": "4000787030016", "type": "disk", "id-link": null, "fstype": null, "mountpoints": [null]},
                {"name": "sda1", "size": "1073741824", "type": "part", "id-link": null}
            ]}
            """;

        var sda = Assert.Single(LsblkOutputParser.ParseDisks(json));

        Assert.True(sda.IsBlank);
    }
}
