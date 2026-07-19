using System.Net;
using System.Net.Http.Json;
using DiskWeaver.Executor;
using DiskWeaver.Planner;

namespace DiskWeaver.Daemon.Tests;

public class DaemonEndpointsTests : IClassFixture<DaemonWebApplicationFactory>
{
    private readonly DaemonWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public DaemonEndpointsTests(DaemonWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetInventory_ReturnsTheFakeDisks()
    {
        var disks = await _client.GetFromJsonAsync<List<Disk>>("/inventory");

        Assert.NotNull(disks);
        Assert.Equal(4, disks.Count);
    }

    [Fact]
    public async Task GetInventory_ResolvesAnUnknownRaidLvmSignatureAgainstLiveLvmState()
    {
        // End-to-end version of DiskSignatureOwnershipTests: a disk lsblk flagged as "unknown"
        // (some RAID/LVM signature, owner not determinable from lsblk alone) should come back from
        // GET /inventory resolved to "diskweaver", using the array membership + vgs/pvs plumbing
        // wired up in Program.cs. Uses its own factory (like DaemonSafetyEndpointsTests) rather
        // than the shared class fixture, since it mutates inventory/array-membership/command-runner
        // state that other tests in this class don't expect.
        using var factory = new DaemonWebApplicationFactory();
        factory.Inventory.Disks =
        [
            new Disk("/dev/disk/by-id/fake-9", 2_000_000_000_000, IsBlank: false, DevicePath: "/dev/sdz", RaidLvmSignatureOwner: "unknown"),
        ];
        factory.Inventory.PartitionPaths = new Dictionary<string, IReadOnlyList<string>>
        {
            ["/dev/disk/by-id/fake-9"] = ["/dev/sdz1"],
        };
        factory.ArrayMembership.Membership = new Dictionary<string, string> { ["/dev/sdz1"] = "/dev/md127" };
        factory.CommandRunner.Respond("vgs", ["--reportformat", "json", "-o", "vg_name,vg_tags"],
            """{"report":[{"vg":[{"vg_name":"diskweaver-pool","vg_tags":["diskweaver-managed"]}]}]}""");
        factory.CommandRunner.Respond("pvs", ["--reportformat", "json", "-o", "pv_name,vg_name"],
            """{"report":[{"pv":[{"pv_name":"/dev/md127","vg_name":"diskweaver-pool"}]}]}""");
        var client = factory.CreateClient();

        var disks = await client.GetFromJsonAsync<List<Disk>>("/inventory");

        var disk = Assert.Single(disks!);
        Assert.Equal("diskweaver", disk.RaidLvmSignatureOwner);
    }

    [Fact]
    public async Task GetPools_ReturnsTheFakePoolState()
    {
        var pools = await _client.GetFromJsonAsync<List<ExistingPoolState>>("/pools");

        Assert.NotNull(pools);
        var pool = Assert.Single(pools);
        Assert.Equal("diskweaver-pool", pool.PoolName);
        Assert.Equal("data", pool.VolumeName);
        var tier = Assert.Single(pool.Tiers);
        Assert.Equal(RaidLevel.Mirror, tier.RaidLevel);
    }

    [Fact]
    public async Task PostPoolTeardown_TearsDownAPoolFoundViaGetPools()
    {
        var response = await _client.PostAsync("/pools/diskweaver-pool/teardown", content: null);
        response.EnsureSuccessStatusCode();
        var journal = await response.Content.ReadFromJsonAsync<ExecutionJournal>();

        Assert.NotNull(journal);
        Assert.Equal(ExecutionJournalStatus.Succeeded, journal.Status);
        Assert.Contains(_factory.StepRunner.Invocations, s => s.Command == "vgremove");
    }

    [Fact]
    public async Task PostPoolTeardown_UnknownPool_ReturnsNotFound()
    {
        var response = await _client.PostAsync("/pools/does-not-exist/teardown", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostPoolTeardown_PoolWithUnreadableTier_ReturnsConflict_NotAttempted()
    {
        // Regression test: a pool whose state couldn't be fully read (e.g. one tier's array isn't
        // currently running) must not be torn down against partial/unknown information -- refuse
        // outright rather than plan a teardown from an incomplete tier list.
        var original = _factory.PoolState.Pools;
        try
        {
            _factory.PoolState.Pools = [original[0] with { Error = "tier array not running" }];

            var response = await _client.PostAsync("/pools/diskweaver-pool/teardown", content: null);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.DoesNotContain(_factory.StepRunner.Invocations, s => s.Command == "vgremove");
        }
        finally
        {
            _factory.PoolState.Pools = original;
        }
    }

    [Fact]
    public async Task PostExpand_AddingASingleLargerDisk_GrowsExistingTierInPlace_AndFormsANewTierFromTheExcess()
    {
        // Fake pool state: diskweaver-pool is a 2-disk mirror over fake-0/fake-1 (2TB each) --
        // DWR-1, already fully protected. Adding just fake-2 (4TB): the existing tier grown to 3
        // disks (migrating Mirror -> RAID5) using 2TB of fake-2, with its remaining 2TB forming its
        // own new (unprotected, since it has no size-matched partner) independent tier rather than
        // sitting reserved -- that's the "space" option this test is about. A "protection" option
        // (Dwr1 -> Dwr2, a genuine redundancy increase reachable without a merge conflict since
        // there's only one existing tier) also comes back now, but is a separate concern.
        var response = await _client.PostAsJsonAsync(
            "/pools/diskweaver-pool/expand", new ExpansionRequest(["fake-2"]));

        if (!response.IsSuccessStatusCode)
        {
            Assert.Fail(await response.Content.ReadAsStringAsync());
        }
        var body = await response.Content.ReadFromJsonAsync<ExpansionOptionsResponse>();

        Assert.NotNull(body);
        Assert.Contains(body.Options, o => o.Intent == ExpansionOptionsPlanner.ProtectionIntent);
        var option = body.Options.Single(o => o.Intent == ExpansionOptionsPlanner.SpaceIntent);
        Assert.False(string.IsNullOrWhiteSpace(option.PlanId));
        Assert.Equal(2, option.DesiredPlan.Tiers.Count);

        var grownTier = Assert.Single(option.DesiredPlan.Tiers, t => t.DegradedSlots == 0);
        Assert.Equal(3, grownTier.DiskIds.Count);

        var excessTier = Assert.Single(option.DesiredPlan.Tiers, t => t.DegradedSlots > 0);
        Assert.Contains("/dev/disk/by-id/fake-2", excessTier.DiskIds);

        // Both tiers are automated (the grow, and the brand-new tier), so achieved capacity
        // matches the full desired plan's, not just the original 2-disk mirror's.
        Assert.Equal(option.DesiredPlan.PoolCapacityBytes, option.AchievedCapacityBytes);
    }

    [Fact]
    public async Task PostExpand_WithNoneRedundancy_AddsANewIndependentUnprotectedTier_LeavingExistingTierUntouched()
    {
        // diskweaver-pool is a 2-disk Dwr1 mirror over fake-0/fake-1. Requesting redundancy:
        // "none" (the advanced/manual mode) for the newly-added fake-2 must NOT merge it into that
        // mirror's fault tolerance -- it becomes its own brand-new, independent (degraded 2-slot
        // mirror) tier instead, while the existing tier is recomputed unchanged at its own
        // inferred (Dwr1) redundancy.
        var response = await _client.PostAsJsonAsync(
            "/pools/diskweaver-pool/expand", new ExpansionRequest(["fake-2"], Redundancy: "none"));

        if (!response.IsSuccessStatusCode)
        {
            Assert.Fail(await response.Content.ReadAsStringAsync());
        }
        var body = await response.Content.ReadFromJsonAsync<ExpansionOptionsResponse>();

        Assert.NotNull(body);
        var option = Assert.Single(body.Options);
        Assert.Equal("manual", option.Intent);
        Assert.Equal(2, option.DesiredPlan.Tiers.Count);

        var existingTier = Assert.Single(option.DesiredPlan.Tiers, t => t.DiskIds.Contains("/dev/disk/by-id/fake-0"));
        Assert.Equal(2, existingTier.DiskIds.Count);

        var newTier = Assert.Single(option.DesiredPlan.Tiers, t => t.DiskIds.Contains("/dev/disk/by-id/fake-2"));
        Assert.Single(newTier.DiskIds);
        Assert.Equal(RaidLevel.Mirror, newTier.RaidLevel);
    }

    [Fact]
    public async Task PostExpand_UnknownPool_ReturnsNotFound()
    {
        var response = await _client.PostAsJsonAsync(
            "/pools/does-not-exist/expand", new ExpansionRequest(["fake-2"]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostExpand_UnknownDisk_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync(
            "/pools/diskweaver-pool/expand", new ExpansionRequest(["nonexistent"]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostExpand_ThenExecute_AddingTwoMatchingDisks_CreatesNewTierAndSucceeds()
    {
        // fake-2 and fake-3 (both 4TB) arrive together -- large enough as a pair to form a
        // genuine new top tier (exercises vgextend/lvextend), while the bottom segment now has 4
        // qualifying disks, migrating the existing tier from Mirror to RAID5 (also automated).
        // The pool's already fully protected, so a "protection" option also comes back (a wholly
        // new protected tier from fake-2+fake-3 instead of growing the existing one) -- this test
        // is specifically about the growth path, so it picks the "space" option.
        var planResponse = await _client.PostAsJsonAsync(
            "/pools/diskweaver-pool/expand", new ExpansionRequest(["fake-2", "fake-3"]));
        var body = await planResponse.Content.ReadFromJsonAsync<ExpansionOptionsResponse>();
        var plan = body!.Options.Single(o => o.Intent == ExpansionOptionsPlanner.SpaceIntent);

        Assert.Equal(2, plan.DesiredPlan.Tiers.Count);
        // Both the migrated bottom tier and the genuinely new top tier are automated now.
        Assert.Equal(plan.DesiredPlan.PoolCapacityBytes, plan.AchievedCapacityBytes);

        var scriptResponse = await _client.GetAsync($"/pools/diskweaver-pool/expand/{plan.PlanId}/script");
        scriptResponse.EnsureSuccessStatusCode();
        var script = await scriptResponse.Content.ReadAsStringAsync();
        Assert.Contains("vgextend", script);

        var executeResponse = await _client.PostAsync($"/pools/diskweaver-pool/expand/{plan.PlanId}/execute", content: null);
        executeResponse.EnsureSuccessStatusCode();
        var journal = await executeResponse.Content.ReadFromJsonAsync<ExecutionJournal>();

        Assert.NotNull(journal);
        Assert.Equal(ExecutionJournalStatus.Succeeded, journal.Status);
    }

    [Fact]
    public async Task GetExpandScript_UnknownId_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/pools/diskweaver-pool/expand/does-not-exist/script");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostPlan_ReturnsPoolPlanForSelectedDisks()
    {
        var response = await _client.PostAsJsonAsync(
            "/plan", new PlanRequest(["fake-0", "fake-1"], "dwr1", "test-new-pool"));

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<PlanResponse>();

        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body.Id));
        var tier = Assert.Single(body.Plan.Tiers);
        Assert.Equal(RaidLevel.Mirror, tier.RaidLevel);
    }

    [Fact]
    public async Task PostPlan_UnknownDisk_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync(
            "/plan", new PlanRequest(["nonexistent"], "dwr1", "test-new-pool"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("nonexistent", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PostPlan_PoolNameAlreadyExists_ReturnsBadRequest()
    {
        // "diskweaver-pool" is FakePoolStateSource's seeded pool -- POST /plan must refuse to reuse
        // that name for a brand-new pool rather than only failing later, mid-Execute, on mdadm's own
        // "Array name ... is in use already."
        var response = await _client.PostAsJsonAsync(
            "/plan", new PlanRequest(["fake-2", "fake-3"], "dwr1", "diskweaver-pool"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("diskweaver-pool", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PostPlan_InvalidPoolName_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync(
            "/plan", new PlanRequest(["fake-0", "fake-1"], "dwr1", "-not valid!"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostPlan_NoneRedundancy_SingleDisk_Succeeds()
    {
        var response = await _client.PostAsJsonAsync(
            "/plan", new PlanRequest(["fake-2"], "none", "test-unprotected-pool"));

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<PlanResponse>();

        Assert.NotNull(body);
        var tier = Assert.Single(body.Plan.Tiers);
        Assert.Equal(RaidLevel.Mirror, tier.RaidLevel);
        Assert.Single(tier.DiskIds);
    }

    [Fact]
    public async Task PostPlan_UnknownRedundancy_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync(
            "/plan", new PlanRequest(["fake-0", "fake-1"], "shr99"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SamePlanInputs_ProduceTheSameId()
    {
        var first = await _client.PostAsJsonAsync("/plan", new PlanRequest(["fake-0", "fake-1"], "dwr1", "test-new-pool"));
        var second = await _client.PostAsJsonAsync("/plan", new PlanRequest(["fake-0", "fake-1"], "dwr1", "test-new-pool"));

        var firstBody = await first.Content.ReadFromJsonAsync<PlanResponse>();
        var secondBody = await second.Content.ReadFromJsonAsync<PlanResponse>();

        Assert.Equal(firstBody!.Id, secondBody!.Id);
    }

    [Fact]
    public async Task GetPlanScript_ReturnsBuildScriptForACachedPlan()
    {
        var planResponse = await _client.PostAsJsonAsync(
            "/plan", new PlanRequest(["fake-0", "fake-1"], "dwr1", "test-new-pool"));
        var plan = await planResponse.Content.ReadFromJsonAsync<PlanResponse>();

        var script = await _client.GetStringAsync($"/plan/{plan!.Id}/script");

        Assert.Contains("mdadm", script);
        Assert.Contains("--create", script);
    }

    [Fact]
    public async Task GetPlanScript_Teardown_ReturnsTeardownScript()
    {
        var planResponse = await _client.PostAsJsonAsync(
            "/plan", new PlanRequest(["fake-0", "fake-1"], "dwr1", "test-new-pool"));
        var plan = await planResponse.Content.ReadFromJsonAsync<PlanResponse>();

        var script = await _client.GetStringAsync($"/plan/{plan!.Id}/script?kind=teardown");

        Assert.Contains("lvremove", script);
        Assert.Contains("vgremove", script);
    }

    [Fact]
    public async Task GetPlanScript_UnknownId_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/plan/does-not-exist/script");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostExecute_RunsEveryStepAndReturnsASucceededJournal()
    {
        var planResponse = await _client.PostAsJsonAsync(
            "/plan", new PlanRequest(["fake-0", "fake-1"], "dwr1", "test-new-pool"));
        var plan = await planResponse.Content.ReadFromJsonAsync<PlanResponse>();

        var response = await _client.PostAsync($"/plan/{plan!.Id}/execute", content: null);
        response.EnsureSuccessStatusCode();
        var journal = await response.Content.ReadFromJsonAsync<ExecutionJournal>();

        Assert.NotNull(journal);
        Assert.Equal(ExecutionJournalStatus.Succeeded, journal.Status);
        Assert.All(journal.Steps, s => Assert.Equal(ExecutionStepStatus.Succeeded, s.Status));
        Assert.Contains(_factory.StepRunner.Invocations, s => s.Command == "mdadm");
    }

    [Fact]
    public async Task PostExecute_UnknownPlanId_ReturnsNotFound()
    {
        var response = await _client.PostAsync("/plan/does-not-exist/execute", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetExecuteStatus_ReturnsThePersistedJournal()
    {
        var planResponse = await _client.PostAsJsonAsync(
            "/plan", new PlanRequest(["fake-0", "fake-1"], "dwr1", "test-new-pool"));
        var plan = await planResponse.Content.ReadFromJsonAsync<PlanResponse>();
        await _client.PostAsync($"/plan/{plan!.Id}/execute", content: null);

        var journal = await _client.GetFromJsonAsync<ExecutionJournal>($"/execute/{plan.Id}-build/status");

        Assert.NotNull(journal);
        Assert.Equal(ExecutionJournalStatus.Succeeded, journal.Status);
    }

    [Fact]
    public async Task GetExecuteStatus_UnknownId_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/execute/does-not-exist/status");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

}
