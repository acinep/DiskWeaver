using System.Net;
using System.Net.Http.Json;
using DiskWeaver.Executor;
using DiskWeaver.Planner;

namespace DiskWeaver.Daemon.Tests;

public class DaemonWipeEndpointTests
{
    [Fact]
    public async Task PostWipe_RunsWipefsOnEachRequestedDisk()
    {
        using var factory = new DaemonWebApplicationFactory();
        factory.Inventory.Disks =
        [
            new Disk("/dev/loop3", 4_000_000_000_000, IsBlank: false),
            new Disk("/dev/loop4", 4_000_000_000_000, IsBlank: false),
        ];
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/disks/wipe", new WipeRequest(["loop3", "loop4"]));

        response.EnsureSuccessStatusCode();
        Assert.Contains(factory.StepRunner.Invocations, s => s.Command == "wipefs" && s.Arguments.Contains("/dev/loop3"));
        Assert.Contains(factory.StepRunner.Invocations, s => s.Command == "wipefs" && s.Arguments.Contains("/dev/loop4"));
    }

    [Fact]
    public async Task PostWipe_ZeroesPartitionSuperblockBeforeWipingParentDisk()
    {
        using var factory = new DaemonWebApplicationFactory();
        factory.Inventory.Disks = [new Disk("/dev/loop3", 4_000_000_000_000, IsBlank: false)];
        factory.Inventory.PartitionPaths = new Dictionary<string, IReadOnlyList<string>>
        {
            ["/dev/loop3"] = ["/dev/loop3p1"],
        };
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/disks/wipe", new WipeRequest(["loop3"]));
        response.EnsureSuccessStatusCode();

        var invocations = factory.StepRunner.Invocations;
        var zeroSuperblock = invocations.FindIndex(s =>
            s.Command == "mdadm" && s.Arguments.SequenceEqual(new[] { "--zero-superblock", "/dev/loop3p1" }));
        var wipefs = invocations.FindIndex(s => s.Command == "wipefs" && s.Arguments.Contains("/dev/loop3"));

        Assert.True(zeroSuperblock >= 0 && wipefs >= 0 && zeroSuperblock < wipefs);
    }

    [Fact]
    public async Task PostWipe_PartitionStillInALiveArray_ReturnsConflictAndRunsNothing()
    {
        // Reproduces the real failure: mdadm --zero-superblock refuses a partition that's still an
        // active/spare member of a currently-assembled array (e.g. left behind by a grow/reshape
        // that failed partway through) -- this must be caught up front with a clear message naming
        // the array, not discovered only after a doomed mdadm command runs and fails.
        using var factory = new DaemonWebApplicationFactory();
        factory.Inventory.Disks = [new Disk("/dev/loop3", 4_000_000_000_000, IsBlank: false)];
        factory.Inventory.PartitionPaths = new Dictionary<string, IReadOnlyList<string>>
        {
            ["/dev/loop3"] = ["/dev/loop3p1"],
        };
        factory.ArrayMembership.Membership = new Dictionary<string, string>
        {
            ["/dev/loop3p1"] = "/dev/md127",
        };
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/disks/wipe", new WipeRequest(["loop3"]));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("/dev/loop3p1", body);
        Assert.Contains("/dev/md127", body);
        Assert.Empty(factory.StepRunner.Invocations);
    }

    [Fact]
    public async Task PostWipe_UnknownDiskName_ReturnsBadRequest()
    {
        using var factory = new DaemonWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/disks/wipe", new WipeRequest(["does-not-exist"]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.StepRunner.Invocations);
    }
}
