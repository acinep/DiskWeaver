using DiskWeaver.Core.Inventory.Abstractions;
using DiskWeaver.Planner;

namespace DiskWeaver.Executor.Tests;

public class DiskSignatureOwnershipTests
{
    private sealed class FakeArrayMembershipSource(IReadOnlyDictionary<string, string> membership) : IArrayMembershipSource
    {
        public IReadOnlyDictionary<string, string> GetArrayMembership() => membership;
    }

    [Fact]
    public void AssembledArrayInATaggedVg_ResolvesToDiskweaver()
    {
        var disk = new Disk("/dev/disk/by-id/ata-1", 2_000_000_000_000, IsBlank: false, DevicePath: "/dev/sdb", RaidLvmSignatureOwner: "unknown");
        var partitionPaths = new Dictionary<string, IReadOnlyList<string>> { ["/dev/disk/by-id/ata-1"] = ["/dev/sdb1"] };
        var arrayMembership = new FakeArrayMembershipSource(new Dictionary<string, string> { ["/dev/sdb1"] = "/dev/md127" });

        var runner = new FakeCommandRunner();
        runner.Respond("vgs", ["--reportformat", "json", "-o", "vg_name,vg_tags"],
            """{"report":[{"vg":[{"vg_name":"diskweaver-pool","vg_tags":["diskweaver-managed"]}]}]}""");
        runner.Respond("pvs", ["--reportformat", "json", "-o", "pv_name,vg_name"],
            """{"report":[{"pv":[{"pv_name":"/dev/md127","vg_name":"diskweaver-pool"}]}]}""");

        var result = DiskSignatureOwnership.Annotate([disk], partitionPaths, arrayMembership, runner);

        Assert.Equal("diskweaver", Assert.Single(result).RaidLvmSignatureOwner);
    }

    [Fact]
    public void AssembledArrayInAnUntaggedVg_ResolvesToForeign()
    {
        var disk = new Disk("/dev/disk/by-id/ata-1", 2_000_000_000_000, IsBlank: false, DevicePath: "/dev/sdb", RaidLvmSignatureOwner: "unknown");
        var partitionPaths = new Dictionary<string, IReadOnlyList<string>> { ["/dev/disk/by-id/ata-1"] = ["/dev/sdb1"] };
        var arrayMembership = new FakeArrayMembershipSource(new Dictionary<string, string> { ["/dev/sdb1"] = "/dev/md127" });

        var runner = new FakeCommandRunner();
        runner.Respond("vgs", ["--reportformat", "json", "-o", "vg_name,vg_tags"],
            """{"report":[{"vg":[{"vg_name":"someone-elses-vg","vg_tags":[]}]}]}""");
        runner.Respond("pvs", ["--reportformat", "json", "-o", "pv_name,vg_name"],
            """{"report":[{"pv":[{"pv_name":"/dev/md127","vg_name":"someone-elses-vg"}]}]}""");

        var result = DiskSignatureOwnership.Annotate([disk], partitionPaths, arrayMembership, runner);

        Assert.Equal("foreign", Assert.Single(result).RaidLvmSignatureOwner);
    }

    [Fact]
    public void ArrayNotCurrentlyAssembled_StaysUnknown_AndNeverShellsOutForTheVgTag()
    {
        // No entry in arrayMembership at all -- the array isn't running, so there's no live PV/VG
        // to check. The fake would throw on any unexpected vgs/pvs call, so an empty FakeCommandRunner
        // here also proves Annotate doesn't even try once it can't find the array.
        var disk = new Disk("/dev/disk/by-id/ata-1", 2_000_000_000_000, IsBlank: false, DevicePath: "/dev/sdb", RaidLvmSignatureOwner: "unknown");
        var partitionPaths = new Dictionary<string, IReadOnlyList<string>> { ["/dev/disk/by-id/ata-1"] = ["/dev/sdb1"] };
        var arrayMembership = new FakeArrayMembershipSource(new Dictionary<string, string>());
        var runner = new FakeCommandRunner();
        runner.Respond("vgs", ["--reportformat", "json", "-o", "vg_name,vg_tags"], """{"report":[{"vg":[]}]}""");
        runner.Respond("pvs", ["--reportformat", "json", "-o", "pv_name,vg_name"], """{"report":[{"pv":[]}]}""");

        var result = DiskSignatureOwnership.Annotate([disk], partitionPaths, arrayMembership, runner);

        Assert.Equal("unknown", Assert.Single(result).RaidLvmSignatureOwner);
    }

    [Fact]
    public void NoDiskHasAnUnknownSignature_NeverShellsOut()
    {
        // A blank disk (RaidLvmSignatureOwner null) or one already resolved shouldn't trigger any
        // vgs/pvs call at all -- an empty FakeCommandRunner (no canned responses) proves it.
        var disks = new[] { new Disk("/dev/loop0", 2_000_000_000_000) };
        var runner = new FakeCommandRunner();

        var result = DiskSignatureOwnership.Annotate(
            disks, new Dictionary<string, IReadOnlyList<string>>(), new FakeArrayMembershipSource(new Dictionary<string, string>()), runner);

        Assert.Null(Assert.Single(result).RaidLvmSignatureOwner);
    }
}
