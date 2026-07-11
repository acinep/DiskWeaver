using System.Net;
using System.Net.Http.Json;
using DiskWeaver.Executor;
using DiskWeaver.Planner;

namespace DiskWeaver.Daemon.Tests;

/// <summary>
/// Covers the disk-eligibility and plan/execute-staleness guards: each test needs to mutate the
/// fake inventory/pool state mid-test (e.g. "a disk stopped being blank between plan and
/// execute"), so unlike <see cref="DaemonEndpointsTests"/> these don't share a fixture across
/// tests -- each gets its own freshly-constructed <see cref="DaemonWebApplicationFactory"/>.
/// </summary>
public class DaemonSafetyEndpointsTests
{
    [Fact]
    public async Task PostPlan_DiskThatAlreadyHasAPartitionTable_ReturnsBadRequest()
    {
        using var factory = new DaemonWebApplicationFactory();
        // This test plans a brand-new pool independent of the fixture's default seeded
        // "diskweaver-pool" -- clear it so the poolName-collision check doesn't mask the
        // blank-disk rejection this test is actually about.
        factory.PoolState.Pools = [];
        factory.Inventory.Disks =
        [
            new Disk("/dev/disk/by-id/fake-0", 2_000_000_000_000, IsBlank: false),
            new Disk("/dev/disk/by-id/fake-1", 2_000_000_000_000),
        ];
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/plan", new PlanRequest(["fake-0", "fake-1"], "dwr1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("fake-0", body);
    }

    [Fact]
    public async Task PostExecute_DiskStoppedBeingBlankAfterPlanning_ReturnsConflictAndRunsNothing()
    {
        // Simulates the TOCTOU window the review flagged: a plan is reviewed (POST /plan
        // succeeds), then before Execute is clicked, one of its disks gets used by something else
        // (another operation, a manual mdadm/parted command, etc.) -- Execute must refuse to run
        // parted/mdadm against it rather than trusting the plan it already computed.
        using var factory = new DaemonWebApplicationFactory();
        // Same reasoning as above -- this plans a brand-new pool, unrelated to the fixture's
        // default seeded "diskweaver-pool".
        factory.PoolState.Pools = [];
        var client = factory.CreateClient();

        var planResponse = await client.PostAsJsonAsync(
            "/plan", new PlanRequest(["fake-0", "fake-1"], "dwr1"));
        var plan = await planResponse.Content.ReadFromJsonAsync<PlanResponse>();

        factory.Inventory.Disks =
        [
            new Disk("/dev/disk/by-id/fake-0", 2_000_000_000_000, IsBlank: false),
            new Disk("/dev/disk/by-id/fake-1", 2_000_000_000_000),
        ];

        var response = await client.PostAsync($"/plan/{plan!.Id}/execute", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(factory.StepRunner.Invocations);
    }

    [Fact]
    public async Task PostExpand_NewDiskThatIsNotBlank_ReturnsBadRequest()
    {
        using var factory = new DaemonWebApplicationFactory();
        factory.Inventory.Disks =
        [
            .. factory.Inventory.Disks,
            new Disk("/dev/disk/by-id/fake-4", 4_000_000_000_000, IsBlank: false),
        ];
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/pools/diskweaver-pool/expand", new ExpansionRequest(["fake-4"]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostExpandExecute_PoolChangedSincePreview_ReturnsConflictAndRunsNothing()
    {
        // Between previewing an expansion and clicking Execute, the pool itself changed (another
        // expand/teardown ran, or a disk was manually pulled from the array) -- the cached
        // ExecutionPlan's mdadm --add/--grow steps target array state that may no longer exist.
        using var factory = new DaemonWebApplicationFactory();
        var client = factory.CreateClient();

        var planResponse = await client.PostAsJsonAsync(
            "/pools/diskweaver-pool/expand", new ExpansionRequest(["fake-2"]));
        var body = await planResponse.Content.ReadFromJsonAsync<ExpansionOptionsResponse>();
        // The pool's a single 2-disk mirror with room to reach Dwr2, so both a protection
        // (Dwr1->Dwr2 upgrade) and a space (grow-in-place) option come back here -- this test only
        // cares about plan-id/staleness mechanics, so it picks one deterministically.
        var plan = body!.Options.Single(o => o.Intent == ExpansionOptionsPlanner.SpaceIntent);

        var originalPool = factory.PoolState.Pools[0];
        factory.PoolState.Pools = [originalPool with { VolumeName = "renamed-data" }];

        var response = await client.PostAsync($"/pools/diskweaver-pool/expand/{plan.PlanId}/execute", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(factory.StepRunner.Invocations);
    }

    [Fact]
    public async Task PostPoolTeardown_SamePoolNameRebuiltSincePriorTeardown_RunsAgainInsteadOfReplayingStaleJournal()
    {
        // Real incident: a pool was torn down successfully, then a new pool was built from scratch
        // under the same default "diskweaver-pool" name (e.g. a loop-device test rig rebuilt with
        // `testkit`). Teardown's execution id used to be just "pool-{poolName}-teardown" -- no
        // fingerprint of the pool's actual tiers -- so the second teardown collided with the first
        // one's persisted Succeeded journal and ExecutionRunner.AdvanceOneStep silently replayed
        // "already done" without running a single real command against the rebuilt pool's actual
        // array/LV. Folding ExecutionPlanCache.ComputeFingerprint into the id (same fix /expand
        // already has) makes a rebuilt pool get a distinct id instead.
        using var factory = new DaemonWebApplicationFactory();
        var firstPool = new ExistingPoolState(
            "diskweaver-pool", "data",
            [new ExistingTier("/dev/md127", 2_000_000_000_000, ["/dev/fake-0", "/dev/fake-1"], RaidLevel.Mirror,
                ["/dev/fake-0-part1", "/dev/fake-1-part1"])]);
        factory.PoolState.Pools = [firstPool];
        var client = factory.CreateClient();

        var firstResponse = await client.PostAsync("/pools/diskweaver-pool/teardown", content: null);
        firstResponse.EnsureSuccessStatusCode();
        Assert.Contains(factory.StepRunner.Invocations, s => s.Arguments.Contains("/dev/md127"));

        var rebuiltPool = new ExistingPoolState(
            "diskweaver-pool", "data",
            [new ExistingTier("/dev/md126", 4_000_000_000_000, ["/dev/fake-2", "/dev/fake-3", "/dev/fake-4"], RaidLevel.Raid5,
                ["/dev/fake-2-part1", "/dev/fake-3-part1", "/dev/fake-4-part1"])]);
        factory.PoolState.Pools = [rebuiltPool];

        var secondResponse = await client.PostAsync("/pools/diskweaver-pool/teardown", content: null);
        secondResponse.EnsureSuccessStatusCode();

        // The second teardown must actually run commands against the rebuilt pool's real array,
        // not silently no-op because the first pool's teardown already reached Succeeded.
        Assert.Contains(factory.StepRunner.Invocations, s => s.Arguments.Contains("/dev/md126"));
    }

    [Fact]
    public async Task PostExecute_Build_PoolAlreadyExists_IsIdempotentAndRunsNothing()
    {
        // The "already done" case is decided by asking live state (does a pool by this name exist
        // right now?), never by trusting a persisted journal -- see Program.cs's /plan/{id}/execute
        // comment. Simulates the pool genuinely having been built (by adding it to the fake pool
        // state, since FakeStepRunner doesn't itself mutate FakePoolStateSource) before re-executing.
        using var factory = new DaemonWebApplicationFactory();
        factory.PoolState.Pools = [];
        var client = factory.CreateClient();

        var planResponse = await client.PostAsJsonAsync(
            "/plan", new PlanRequest(["fake-0", "fake-1"], "dwr1", "test-new-pool"));
        var plan = await planResponse.Content.ReadFromJsonAsync<PlanResponse>();

        await client.PostAsync($"/plan/{plan!.Id}/execute", content: null);
        var firstCount = factory.StepRunner.Invocations.Count;

        factory.PoolState.Pools =
        [
            new ExistingPoolState("test-new-pool", "data",
                [new ExistingTier("/dev/md/test-new-pool-tier0", 2_000_000_000_000,
                    ["/dev/disk/by-id/fake-0", "/dev/disk/by-id/fake-1"], RaidLevel.Mirror,
                    ["/dev/disk/by-id/fake-0-part1", "/dev/disk/by-id/fake-1-part1"])]),
        ];

        var second = await client.PostAsync($"/plan/{plan.Id}/execute", content: null);
        var secondJournal = await second.Content.ReadFromJsonAsync<ExecutionJournal>();

        Assert.Equal(ExecutionJournalStatus.Succeeded, secondJournal!.Status);
        Assert.Equal(firstCount, factory.StepRunner.Invocations.Count);
    }

    [Fact]
    public async Task PostExecute_Build_PoolResetToGoneAndDisksBlankAgain_RunsAgainInsteadOfReplayingStaleJournal()
    {
        // The bug this whole redesign targets: a build succeeds for real, then something external
        // (rebuilt test rig, reverted snapshot) resets the pool and its disks back to exactly the
        // pre-build state. Re-executing the same plan id must run every step again for real, not
        // silently replay whatever the previous request's journal happened to record.
        using var factory = new DaemonWebApplicationFactory();
        factory.PoolState.Pools = [];
        var client = factory.CreateClient();

        var planResponse = await client.PostAsJsonAsync(
            "/plan", new PlanRequest(["fake-0", "fake-1"], "dwr1", "test-new-pool"));
        var plan = await planResponse.Content.ReadFromJsonAsync<PlanResponse>();

        await client.PostAsync($"/plan/{plan!.Id}/execute", content: null);
        var firstCount = factory.StepRunner.Invocations.Count;
        Assert.True(firstCount > 0);

        // External reset: the pool is gone again and the disks are back to blank.
        factory.PoolState.Pools = [];
        factory.Inventory.Disks =
        [
            new Disk("/dev/disk/by-id/fake-0", 2_000_000_000_000),
            new Disk("/dev/disk/by-id/fake-1", 2_000_000_000_000),
        ];

        var second = await client.PostAsync($"/plan/{plan.Id}/execute", content: null);
        var secondJournal = await second.Content.ReadFromJsonAsync<ExecutionJournal>();

        Assert.Equal(ExecutionJournalStatus.Succeeded, secondJournal!.Status);
        Assert.Equal(firstCount * 2, factory.StepRunner.Invocations.Count);
    }

    [Fact]
    public async Task PostExpandExecute_PoolResetToPreExpandState_RunsAgainInsteadOfReplayingStaleJournal()
    {
        // Same bug, for expand: an expansion succeeds for real, then something external resets the
        // pool/disks back to exactly the pre-expand state (this fingerprint-matches the plan's own
        // expected precondition, which is exactly why the old "only check if journal is null" logic
        // let a stale Succeeded journal short-circuit a second, genuinely-needed run).
        using var factory = new DaemonWebApplicationFactory();
        var client = factory.CreateClient();
        var originalPool = factory.PoolState.Pools[0];

        var planResponse = await client.PostAsJsonAsync(
            "/pools/diskweaver-pool/expand", new ExpansionRequest(["fake-2"]));
        var planBody = await planResponse.Content.ReadFromJsonAsync<ExpansionOptionsResponse>();
        // Same pool shape as PostExpandExecute_PoolChangedSincePreview above -- both a protection
        // and a space option come back; this test is only about journal/staleness mechanics.
        var plan = planBody!.Options.Single(o => o.Intent == ExpansionOptionsPlanner.SpaceIntent);

        await client.PostAsync($"/pools/diskweaver-pool/expand/{plan.PlanId}/execute", content: null);
        var firstCount = factory.StepRunner.Invocations.Count;
        Assert.True(firstCount > 0);

        // External reset: pool and disks revert to exactly their pre-expand state.
        factory.PoolState.Pools = [originalPool];
        factory.Inventory.Disks =
        [
            new Disk("/dev/disk/by-id/fake-0", 2_000_000_000_000),
            new Disk("/dev/disk/by-id/fake-1", 2_000_000_000_000),
            new Disk("/dev/disk/by-id/fake-2", 4_000_000_000_000),
            new Disk("/dev/disk/by-id/fake-3", 4_000_000_000_000),
        ];

        var second = await client.PostAsync($"/pools/diskweaver-pool/expand/{plan.PlanId}/execute", content: null);
        var secondJournal = await second.Content.ReadFromJsonAsync<ExecutionJournal>();

        Assert.Equal(ExecutionJournalStatus.Succeeded, secondJournal!.Status);
        Assert.Equal(firstCount * 2, factory.StepRunner.Invocations.Count);
    }

    [Fact]
    public async Task PostPoolTeardown_IdenticalRebuild_RunsAgainInsteadOfReplayingStaleJournal()
    {
        // Sharper variant of PostPoolTeardown_SamePoolNameRebuiltSincePriorTeardown_... above: that
        // test's rebuilt pool has a different shape (different array device/RAID level/disks), so
        // it was already guaranteed a different fingerprint/id even under the old design. This one
        // rebuilds an IDENTICALLY-shaped pool -- same fingerprint, same execution id -- which only
        // the "never trust a persisted journal" redesign (not the fingerprint-in-id trick alone)
        // actually fixes.
        using var factory = new DaemonWebApplicationFactory();
        var pool = new ExistingPoolState(
            "diskweaver-pool", "data",
            [new ExistingTier("/dev/md127", 2_000_000_000_000, ["/dev/fake-0", "/dev/fake-1"], RaidLevel.Mirror,
                ["/dev/fake-0-part1", "/dev/fake-1-part1"])]);
        factory.PoolState.Pools = [pool];
        var client = factory.CreateClient();

        var firstResponse = await client.PostAsync("/pools/diskweaver-pool/teardown", content: null);
        firstResponse.EnsureSuccessStatusCode();
        var firstCount = factory.StepRunner.Invocations.Count;
        Assert.True(firstCount > 0);

        // Rebuilt identically -- same tiers, same disks, same array device name, same fingerprint.
        factory.PoolState.Pools = [pool];

        var secondResponse = await client.PostAsync("/pools/diskweaver-pool/teardown", content: null);
        secondResponse.EnsureSuccessStatusCode();

        Assert.Equal(firstCount * 2, factory.StepRunner.Invocations.Count);
    }

    [Fact]
    public async Task PostExpand_SamePoolAndDisksButPoolChangedBetweenPreviews_GetsADifferentId()
    {
        // Regression test: the expansion cache id used to be poolName + addedDiskIds only, so two
        // previews of the *same* pool/disk-ids tuple taken before/after some other change to the
        // pool collided on the same id -- the second Store silently overwrote the first client's
        // still-outstanding plan, repointing its already-shown execute URL at a plan it never
        // reviewed. Folding the pool's fingerprint into the id means a changed pool can never
        // collide with an earlier preview's id.
        using var factory = new DaemonWebApplicationFactory();
        var client = factory.CreateClient();

        var firstResponse = await client.PostAsJsonAsync(
            "/pools/diskweaver-pool/expand", new ExpansionRequest(["fake-2"]));
        var firstBody = await firstResponse.Content.ReadFromJsonAsync<ExpansionOptionsResponse>();
        // Same pool shape as the tests above -- both a protection and a space option come back;
        // this test is only about id-collision mechanics, so it picks one deterministically.
        var firstPlan = firstBody!.Options.Single(o => o.Intent == ExpansionOptionsPlanner.SpaceIntent);

        var originalPool = factory.PoolState.Pools[0];
        factory.PoolState.Pools = [originalPool with { VolumeName = "renamed-data" }];

        var secondResponse = await client.PostAsJsonAsync(
            "/pools/diskweaver-pool/expand", new ExpansionRequest(["fake-2"]));
        var secondBody = await secondResponse.Content.ReadFromJsonAsync<ExpansionOptionsResponse>();
        var secondPlan = secondBody!.Options.Single(o => o.Intent == ExpansionOptionsPlanner.SpaceIntent);

        Assert.NotEqual(firstPlan.PlanId, secondPlan.PlanId);

        // The first plan's own id must still resolve to a script -- it wasn't silently evicted --
        // even though it no longer matches the pool's current (renamed) state.
        var firstScript = await client.GetAsync($"/pools/diskweaver-pool/expand/{firstPlan.PlanId}/script");
        firstScript.EnsureSuccessStatusCode();
    }

    // Segment size mirrors PartitionLayout.ForPlanning's 2 MiB/disk reserve (matches
    // FakePoolStateSource's own convention) -- without it, TieringPlanner's recomputed desired
    // tiers (built from PartitionLayout.ForPlanning's reduced sizes) never exactly/subset-match
    // these fixtures' existing tiers, and they'd wrongly show up as orphaned instead.
    private const long ReservedBytesPerDisk = 2 * 1024 * 1024;

    [Fact]
    public async Task PostExpand_AllJbodPool_DefaultMode_OffersBothCompletingAndLeavingIndependent()
    {
        // A pool built entirely of independent RedundancyLevel.None (JBOD) tiers, offered two
        // disks big enough to complete both into real mirrors: the default two-option response
        // should surface that as the "protection" candidate, and a "space" candidate that instead
        // groups the same 2 new (same-size) disks into their own new protected tier, leaving the
        // existing JBOD tiers untouched -- see ExpansionOptionsPlannerTests for the underlying
        // planner-level coverage of this exact matrix.
        using var factory = new DaemonWebApplicationFactory();
        factory.PoolState.Pools =
        [
            new ExistingPoolState("diskweaver-pool", "data",
            [
                new ExistingTier("/dev/md127", 2_000_000_000_000 - ReservedBytesPerDisk, ["/dev/disk/by-id/fake-0"], RaidLevel.Mirror,
                    ["/dev/disk/by-id/fake-0-part1"], ConfiguredMemberCount: 2, IsUnprotectedByDesign: true),
                new ExistingTier("/dev/md126", 2_000_000_000_000 - ReservedBytesPerDisk, ["/dev/disk/by-id/fake-1"], RaidLevel.Mirror,
                    ["/dev/disk/by-id/fake-1-part1"], ConfiguredMemberCount: 2, IsUnprotectedByDesign: true),
            ]),
        ];
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/pools/diskweaver-pool/expand", new ExpansionRequest(["fake-2", "fake-3"]));

        if (!response.IsSuccessStatusCode)
        {
            Assert.Fail(await response.Content.ReadAsStringAsync());
        }
        var body = await response.Content.ReadFromJsonAsync<ExpansionOptionsResponse>();

        Assert.NotNull(body);
        Assert.Contains(body.Options, o => o.Intent == ExpansionOptionsPlanner.ProtectionIntent);
        Assert.Contains(body.Options, o => o.Intent == ExpansionOptionsPlanner.SpaceIntent);

        // fake-2/fake-3 are both large enough to complete a tier alone, so one of them completes
        // both existing tiers and the other (entirely unneeded for completion) becomes its own new
        // leftover tier -- only the two *completed* tiers should have 2 disks each.
        var protection = body.Options.Single(o => o.Intent == ExpansionOptionsPlanner.ProtectionIntent);
        var completedTiers = protection.DesiredPlan.Tiers.Where(t => t.DegradedSlots == 0);
        Assert.Equal(2, completedTiers.Count());
        Assert.All(completedTiers, t => Assert.Equal(2, t.DiskIds.Count));
    }

    [Fact]
    public async Task PostExpand_MixedRedundancyPool_DefaultMode_CompletesTheEligibleJbodTierOnly()
    {
        // A pool with one JBOD tier and one already-protected Raid5 tier: the default expand no
        // longer refuses outright just because the pool doesn't agree on one redundancy -- it
        // completes whatever's actually eligible (the JBOD tier) and leaves the rest alone. The
        // offered disk (2TB) is too small to grow the Raid5 tier's 4TB segment, so no space option
        // comes back either.
        using var factory = new DaemonWebApplicationFactory();
        factory.PoolState.Pools =
        [
            new ExistingPoolState("diskweaver-pool", "data",
            [
                new ExistingTier("/dev/md127", 2_000_000_000_000 - ReservedBytesPerDisk, ["/dev/disk/by-id/fake-0"], RaidLevel.Mirror,
                    ["/dev/disk/by-id/fake-0-part1"], ConfiguredMemberCount: 2, IsUnprotectedByDesign: true),
                new ExistingTier("/dev/md126", 4_000_000_000_000 - ReservedBytesPerDisk,
                    ["/dev/disk/by-id/fake-2", "/dev/disk/by-id/fake-3"], RaidLevel.Raid5,
                    ["/dev/disk/by-id/fake-2-part1", "/dev/disk/by-id/fake-3-part1"]),
            ]),
        ];
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/pools/diskweaver-pool/expand", new ExpansionRequest(["fake-1"]));

        if (!response.IsSuccessStatusCode)
        {
            Assert.Fail(await response.Content.ReadAsStringAsync());
        }
        var body = await response.Content.ReadFromJsonAsync<ExpansionOptionsResponse>();

        Assert.NotNull(body);
        var option = Assert.Single(body.Options);
        Assert.Equal(ExpansionOptionsPlanner.ProtectionIntent, option.Intent);
        var completedTier = Assert.Single(option.DesiredPlan.Tiers, t => t.DiskIds.Contains("/dev/disk/by-id/fake-1"));
        Assert.Equal(2, completedTier.DiskIds.Count);
    }

    [Fact]
    public async Task PostExpand_ProtectedRaid5Pool_DefaultMode_OffersDwr1ToDwr2UpgradeAlongsideSpaceGrowth()
    {
        // Real user question: a single 3-disk RAID5 tier (2,2,4TB disks, already Dwr1) gets 2 new
        // 4TB disks offered. Since the default targetProtection now opportunistically tries one
        // level above the pool's current redundancy (Dwr1 -> Dwr2), and there's only one existing
        // tier (so no merge conflict), a genuine protection upgrade is achievable and must be
        // offered -- this is exactly the case that used to silently never surface an DWR-2 option
        // just because nobody explicitly asked for it.
        using var factory = new DaemonWebApplicationFactory();
        factory.PoolState.Pools =
        [
            new ExistingPoolState("diskweaver-pool", "data",
            [
                new ExistingTier("/dev/md127", 2_000_000_000_000 - ReservedBytesPerDisk,
                    ["/dev/disk/by-id/fake-0", "/dev/disk/by-id/fake-1", "/dev/disk/by-id/fake-2"], RaidLevel.Raid5,
                    ["/dev/disk/by-id/fake-0-part1", "/dev/disk/by-id/fake-1-part1", "/dev/disk/by-id/fake-2-part1"]),
            ]),
        ];
        factory.Inventory.Disks =
        [
            .. factory.Inventory.Disks,
            new Disk("/dev/disk/by-id/fake-4", 4_000_000_000_000),
        ];
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/pools/diskweaver-pool/expand", new ExpansionRequest(["fake-3", "fake-4"]));

        if (!response.IsSuccessStatusCode)
        {
            Assert.Fail(await response.Content.ReadAsStringAsync());
        }
        var body = await response.Content.ReadFromJsonAsync<ExpansionOptionsResponse>();

        Assert.NotNull(body);
        var protection = body.Options.Single(o => o.Intent == ExpansionOptionsPlanner.ProtectionIntent);
        Assert.Equal("dwr2", protection.AchievedRedundancy);
        var space = body.Options.Single(o => o.Intent == ExpansionOptionsPlanner.SpaceIntent);

        // Real bug caught live: both candidates share the same poolName/addedDiskIds/pool
        // fingerprint, so without discriminating the cache id by intent, the second Store call
        // silently overwrote the first candidate's plan -- executing the "protection" planId would
        // actually run the "space" plan. Each candidate must get its own distinct plan id.
        Assert.NotEqual(protection.PlanId, space.PlanId);

        var protectionScript = await client.GetAsync($"/pools/diskweaver-pool/expand/{protection.PlanId}/script");
        protectionScript.EnsureSuccessStatusCode();
        Assert.Contains("--level=6", await protectionScript.Content.ReadAsStringAsync());

        var spaceScript = await client.GetAsync($"/pools/diskweaver-pool/expand/{space.PlanId}/script");
        spaceScript.EnsureSuccessStatusCode();
        Assert.DoesNotContain("--level=6", await spaceScript.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PostExpand_ExplicitDwr1OnMultiTierJbodPool_ReturnsMergeConflict()
    {
        // Explicitly requesting Dwr1 for a pool made of several independent JBOD tiers requires
        // merging those already-built arrays into one shared tier -- not achievable incrementally.
        // Must surface the new clear merge-conflict message, not the old confusing
        // "doesn't correspond to any tier" orphaned-tier error.
        using var factory = new DaemonWebApplicationFactory();
        factory.PoolState.Pools =
        [
            new ExistingPoolState("diskweaver-pool", "data",
            [
                new ExistingTier("/dev/md127", 2_000_000_000_000 - ReservedBytesPerDisk, ["/dev/disk/by-id/fake-0"], RaidLevel.Mirror,
                    ["/dev/disk/by-id/fake-0-part1"], ConfiguredMemberCount: 2, IsUnprotectedByDesign: true),
                new ExistingTier("/dev/md126", 2_000_000_000_000 - ReservedBytesPerDisk, ["/dev/disk/by-id/fake-1"], RaidLevel.Mirror,
                    ["/dev/disk/by-id/fake-1-part1"], ConfiguredMemberCount: 2, IsUnprotectedByDesign: true),
            ]),
        ];
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/pools/diskweaver-pool/expand", new ExpansionRequest(["fake-2", "fake-3"], Redundancy: "dwr1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("/dev/md127", body);
        Assert.Contains("/dev/md126", body);
        Assert.Contains("merging", body);
        Assert.Contains("rebuild", body);
    }

    [Fact]
    public async Task PostExpand_DefaultRedundancy_OnAlreadyProtectedIndependentTiers_SilentlyGrowsIndependently()
    {
        // Real feedback: presenting the merge-conflict refusal as an error the user has to react to
        // (clicking a "grow independently instead" fallback) is wrong for the default case -- nobody
        // explicitly asked for the shared/merged redundancy that conflicts, it's just the pool's own
        // inferred redundancy applied automatically. The default behavior should silently be the most
        // protected storage achievable without a rebuild (growIndependently), with the rebuild-capacity
        // gap surfaced only as a plain informational note, never as an error requiring an extra click.
        using var factory = new DaemonWebApplicationFactory();
        factory.PoolState.Pools =
        [
            new ExistingPoolState("diskweaver-pool", "data",
            [
                new ExistingTier("/dev/md127", 2_000_000_000_000 - ReservedBytesPerDisk,
                    ["/dev/disk/by-id/fake-0", "/dev/disk/by-id/fake-1"], RaidLevel.Mirror,
                    ["/dev/disk/by-id/fake-0-part1", "/dev/disk/by-id/fake-1-part1"]),
                new ExistingTier("/dev/md126", 2_000_000_000_000 - ReservedBytesPerDisk,
                    ["/dev/disk/by-id/fake-2", "/dev/disk/by-id/fake-3"], RaidLevel.Mirror,
                    ["/dev/disk/by-id/fake-2-part1", "/dev/disk/by-id/fake-3-part1"]),
            ]),
        ];
        factory.Inventory.Disks =
        [
            .. factory.Inventory.Disks,
            new Disk("/dev/disk/by-id/fake-4", 2_000_000_000_000),
            new Disk("/dev/disk/by-id/fake-5", 2_000_000_000_000),
        ];
        var client = factory.CreateClient();

        // No explicit redundancy/targetProtection -- the plain "just add these disks" default
        // flow. Both tiers are already fully protected, so only a "space" option comes back.
        var response = await client.PostAsJsonAsync(
            "/pools/diskweaver-pool/expand", new ExpansionRequest(["fake-4", "fake-5"]));

        if (!response.IsSuccessStatusCode)
        {
            Assert.Fail(await response.Content.ReadAsStringAsync());
        }
        var body = await response.Content.ReadFromJsonAsync<ExpansionOptionsResponse>();
        Assert.NotNull(body);
        // Both a "protection" option (a brand-new protected tier from fake-4+fake-5) and this
        // "space" option (growing both existing mirrors in place) are legitimately available here.
        var preview = body.Options.Single(o => o.Intent == ExpansionOptionsPlanner.SpaceIntent);
        Assert.Equal(2, preview.DesiredPlan.Tiers.Count);
        Assert.All(preview.DesiredPlan.Tiers, t => Assert.Equal(3, t.DiskIds.Count));
        Assert.All(preview.DesiredPlan.Tiers, t => Assert.Equal(RaidLevel.Raid5, t.RaidLevel));
    }

    [Fact]
    public async Task PostExpand_DefaultMode_GrowsBothIndependentMirrorsInPlace_AndCanBeExecuted()
    {
        // Same pool shape as the test above (2 already-protected independent 2-disk mirrors) --
        // the real scenario reported live: user had built exactly this pool, then tried adding 6
        // more 4TB disks and got a merge-conflict refusal under the old design. Under the new
        // two-option model this is just the default "space" candidate; this test carries it
        // through to Execute and checks the actual mdadm --grow steps.
        using var factory = new DaemonWebApplicationFactory();
        factory.PoolState.Pools =
        [
            new ExistingPoolState("diskweaver-pool", "data",
            [
                new ExistingTier("/dev/md127", 2_000_000_000_000 - ReservedBytesPerDisk,
                    ["/dev/disk/by-id/fake-0", "/dev/disk/by-id/fake-1"], RaidLevel.Mirror,
                    ["/dev/disk/by-id/fake-0-part1", "/dev/disk/by-id/fake-1-part1"]),
                new ExistingTier("/dev/md126", 2_000_000_000_000 - ReservedBytesPerDisk,
                    ["/dev/disk/by-id/fake-2", "/dev/disk/by-id/fake-3"], RaidLevel.Mirror,
                    ["/dev/disk/by-id/fake-2-part1", "/dev/disk/by-id/fake-3-part1"]),
            ]),
        ];
        factory.Inventory.Disks =
        [
            .. factory.Inventory.Disks,
            new Disk("/dev/disk/by-id/fake-4", 2_000_000_000_000),
            new Disk("/dev/disk/by-id/fake-5", 2_000_000_000_000),
        ];
        var client = factory.CreateClient();

        var previewResponse = await client.PostAsJsonAsync(
            "/pools/diskweaver-pool/expand", new ExpansionRequest(["fake-4", "fake-5"]));

        if (!previewResponse.IsSuccessStatusCode)
        {
            Assert.Fail(await previewResponse.Content.ReadAsStringAsync());
        }
        var body = await previewResponse.Content.ReadFromJsonAsync<ExpansionOptionsResponse>();
        Assert.NotNull(body);
        var preview = body.Options.Single(o => o.Intent == ExpansionOptionsPlanner.SpaceIntent);
        Assert.Equal(2, preview.DesiredPlan.Tiers.Count);
        Assert.All(preview.DesiredPlan.Tiers, t => Assert.Equal(3, t.DiskIds.Count));
        Assert.All(preview.DesiredPlan.Tiers, t => Assert.Equal(RaidLevel.Raid5, t.RaidLevel));

        var executeResponse = await client.PostAsync($"/pools/diskweaver-pool/expand/{preview.PlanId}/execute", content: null);
        executeResponse.EnsureSuccessStatusCode();
        Assert.Contains(factory.StepRunner.Invocations, s => s.Command == "mdadm"
            && s.Arguments.Contains("--grow") && s.Arguments.Contains("/dev/md127") && s.Arguments.Contains("--raid-devices=3"));
        Assert.Contains(factory.StepRunner.Invocations, s => s.Command == "mdadm"
            && s.Arguments.Contains("--grow") && s.Arguments.Contains("/dev/md126") && s.Arguments.Contains("--raid-devices=3"));
    }

    [Fact]
    public async Task PostExpand_DefaultMode_HypotheticalRebuildCapacityExceedsAchieved()
    {
        // Same pool/disks as the default-mode success test above: 2 independent 2-disk mirrors
        // each growing to a 3-disk RAID5 achieves less total capacity than tearing down and
        // rebuilding all 6 same-size disks together as one shared RAID5 would -- the preview should
        // surface that gap so a user can weigh the option, per the "default to the most protected
        // storage available, and tell the user what a rebuild could get" feedback.
        using var factory = new DaemonWebApplicationFactory();
        factory.PoolState.Pools =
        [
            new ExistingPoolState("diskweaver-pool", "data",
            [
                new ExistingTier("/dev/md127", 2_000_000_000_000 - ReservedBytesPerDisk,
                    ["/dev/disk/by-id/fake-0", "/dev/disk/by-id/fake-1"], RaidLevel.Mirror,
                    ["/dev/disk/by-id/fake-0-part1", "/dev/disk/by-id/fake-1-part1"]),
                new ExistingTier("/dev/md126", 2_000_000_000_000 - ReservedBytesPerDisk,
                    ["/dev/disk/by-id/fake-2", "/dev/disk/by-id/fake-3"], RaidLevel.Mirror,
                    ["/dev/disk/by-id/fake-2-part1", "/dev/disk/by-id/fake-3-part1"]),
            ]),
        ];
        factory.Inventory.Disks =
        [
            .. factory.Inventory.Disks,
            new Disk("/dev/disk/by-id/fake-4", 2_000_000_000_000),
            new Disk("/dev/disk/by-id/fake-5", 2_000_000_000_000),
        ];
        var client = factory.CreateClient();

        var previewResponse = await client.PostAsJsonAsync(
            "/pools/diskweaver-pool/expand", new ExpansionRequest(["fake-4", "fake-5"]));

        if (!previewResponse.IsSuccessStatusCode)
        {
            Assert.Fail(await previewResponse.Content.ReadAsStringAsync());
        }
        var body = await previewResponse.Content.ReadFromJsonAsync<ExpansionOptionsResponse>();
        Assert.NotNull(body);
        var preview = body.Options.Single(o => o.Intent == ExpansionOptionsPlanner.SpaceIntent);
        Assert.NotNull(body.HypotheticalRebuildCapacityBytes);
        Assert.True(body.HypotheticalRebuildCapacityBytes > preview.AchievedCapacityBytes);
    }

    [Fact]
    public async Task PostExpand_TargetArrayDevice_CompletesOneUnprotectedTierIntoARealMirror()
    {
        // "Advanced" mode: send a disk toward completing exactly one named tier.
        using var factory = new DaemonWebApplicationFactory();
        factory.PoolState.Pools =
        [
            new ExistingPoolState("diskweaver-pool", "data",
            [
                new ExistingTier("/dev/md127", 2_000_000_000_000 - ReservedBytesPerDisk, ["/dev/disk/by-id/fake-0"], RaidLevel.Mirror,
                    ["/dev/disk/by-id/fake-0-part1"], ConfiguredMemberCount: 2, IsUnprotectedByDesign: true),
            ]),
        ];
        var client = factory.CreateClient();

        var previewResponse = await client.PostAsJsonAsync(
            "/pools/diskweaver-pool/expand", new ExpansionRequest(["fake-1"], TargetArrayDevice: "/dev/md127"));

        if (!previewResponse.IsSuccessStatusCode)
        {
            Assert.Fail(await previewResponse.Content.ReadAsStringAsync());
        }
        var body = await previewResponse.Content.ReadFromJsonAsync<ExpansionOptionsResponse>();
        Assert.NotNull(body);
        var preview = Assert.Single(body.Options);
        Assert.Equal("manual", preview.Intent);
        var tier = Assert.Single(preview.DesiredPlan.Tiers);
        Assert.Equal(2, tier.DiskIds.Count);
        Assert.Contains("/dev/disk/by-id/fake-1", tier.DiskIds);

        var executeResponse = await client.PostAsync($"/pools/diskweaver-pool/expand/{preview.PlanId}/execute", content: null);
        executeResponse.EnsureSuccessStatusCode();
        // Filling an already-configured missing slot is a plain `mdadm --add` -- no `--grow`, since
        // the slot count itself isn't changing (confirmed live as the only mechanism that actually
        // works for this; a real --grow --raid-devices=2 step is only needed when growing beyond
        // the array's already-configured slot count, not when filling an existing missing one).
        Assert.Contains(factory.StepRunner.Invocations, s => s.Command == "mdadm"
            && s.Arguments.Contains("--add") && s.Arguments.Contains("/dev/md127"));
        Assert.DoesNotContain(factory.StepRunner.Invocations, s => s.Command == "mdadm" && s.Arguments.Contains("--grow"));
    }

    [Fact]
    public async Task PostExpand_TargetArrayDevice_NotEligibleTier_ReturnsBadRequest()
    {
        // A tier that's already a healthy 2-disk mirror isn't a valid target for this mode.
        using var factory = new DaemonWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/pools/diskweaver-pool/expand", new ExpansionRequest(["fake-2"], TargetArrayDevice: "/dev/md/diskweaver-tier0"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostExpand_DefaultMode_CompletesTwoIndependentTiersFromOneLargerDisk()
    {
        // The user's exact repro: 2x2GB unprotected tiers, one 4GB disk arrives -- both tiers
        // should be completed into real mirrors from slices of the same disk, as the default
        // "protection" candidate (auto-protect completion is always attempted, no flag needed).
        using var factory = new DaemonWebApplicationFactory();
        var tierSegmentBytes = 2_000_000_000_000 - ReservedBytesPerDisk;
        factory.PoolState.Pools =
        [
            new ExistingPoolState("diskweaver-pool", "data",
            [
                new ExistingTier("/dev/md127", tierSegmentBytes, ["/dev/disk/by-id/fake-0"], RaidLevel.Mirror,
                    ["/dev/disk/by-id/fake-0-part1"], ConfiguredMemberCount: 2, IsUnprotectedByDesign: true),
                new ExistingTier("/dev/md126", tierSegmentBytes, ["/dev/disk/by-id/fake-1"], RaidLevel.Mirror,
                    ["/dev/disk/by-id/fake-1-part1"], ConfiguredMemberCount: 2, IsUnprotectedByDesign: true),
            ]),
        ];
        factory.Inventory.Disks =
        [
            .. factory.Inventory.Disks,
            new Disk("/dev/disk/by-id/fake-4", 2 * tierSegmentBytes + ReservedBytesPerDisk),
        ];
        var client = factory.CreateClient();

        var previewResponse = await client.PostAsJsonAsync(
            "/pools/diskweaver-pool/expand", new ExpansionRequest(["fake-4"]));

        if (!previewResponse.IsSuccessStatusCode)
        {
            Assert.Fail(await previewResponse.Content.ReadAsStringAsync());
        }
        var body = await previewResponse.Content.ReadFromJsonAsync<ExpansionOptionsResponse>();
        Assert.NotNull(body);
        var preview = Assert.Single(body.Options);
        Assert.Equal(ExpansionOptionsPlanner.ProtectionIntent, preview.Intent);
        Assert.Equal(2, preview.DesiredPlan.Tiers.Count);
        Assert.All(preview.DesiredPlan.Tiers, t => Assert.Equal(2, t.DiskIds.Count));

        var executeResponse = await client.PostAsync($"/pools/diskweaver-pool/expand/{preview.PlanId}/execute", content: null);
        executeResponse.EnsureSuccessStatusCode();
        Assert.Contains(factory.StepRunner.Invocations, s => s.Command == "mdadm"
            && s.Arguments.Contains("--add") && s.Arguments.Contains("/dev/md127"));
        Assert.Contains(factory.StepRunner.Invocations, s => s.Command == "mdadm"
            && s.Arguments.Contains("--add") && s.Arguments.Contains("/dev/md126"));
    }

    [Fact]
    public async Task PostExpand_TargetArrayDevice_DiskWithoutEnoughSpareCapacity_ReturnsBadRequest()
    {
        // Splitting one disk across two "protect this tier" calls: the second call must fail if
        // the disk's remaining (unclaimed) capacity is too small for the second tier's segment.
        using var factory = new DaemonWebApplicationFactory();
        var tierSegmentBytes = 2_000_000_000_000 - ReservedBytesPerDisk;
        factory.PoolState.Pools =
        [
            new ExistingPoolState("diskweaver-pool", "data",
            [
                new ExistingTier("/dev/md127", tierSegmentBytes, ["/dev/disk/by-id/fake-0"], RaidLevel.Mirror,
                    ["/dev/disk/by-id/fake-0-part1"], ConfiguredMemberCount: 2, IsUnprotectedByDesign: true),
                // Already claims fake-4's entire spare capacity via a *different*, larger tier --
                // nothing left for a second "protect /dev/md127" request against the same disk.
                new ExistingTier("/dev/md125", tierSegmentBytes, ["/dev/disk/by-id/fake-4"], RaidLevel.Mirror,
                    ["/dev/disk/by-id/fake-4-part1"]),
            ]),
        ];
        factory.Inventory.Disks =
        [
            .. factory.Inventory.Disks,
            new Disk("/dev/disk/by-id/fake-4", tierSegmentBytes + ReservedBytesPerDisk, IsBlank: false),
        ];
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/pools/diskweaver-pool/expand", new ExpansionRequest(["fake-4"], TargetArrayDevice: "/dev/md127"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("spare capacity", body);
    }
}
