using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.FileProviders;
using DiskWeaver.Core.Executor.Abstractions;
using DiskWeaver.Core.Inventory.Abstractions;
using DiskWeaver.Daemon;
using DiskWeaver.Executor;
using DiskWeaver.Inventory;
using DiskWeaver.Planner;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IDiskInventorySource, LsblkDiskInventorySource>();
builder.Services.AddSingleton<IArrayMembershipSource, ProcMdstatArrayMembershipSource>();
builder.Services.AddSingleton<ICommandRunner>(_ => new ProcessCommandRunner());
builder.Services.AddSingleton<IPoolStateSource>(sp =>
    new MdadmLvmPoolStateSource(diskInventory: sp.GetRequiredService<IDiskInventorySource>()));
builder.Services.AddSingleton<PlanCache>();
builder.Services.AddSingleton<ExecutionPlanCache>();
builder.Services.AddSingleton<IStepRunner, ProcessStepRunner>();
builder.Services.AddSingleton<IJournalStore>(_ => new FileJournalStore(
    Environment.GetEnvironmentVariable("DISKWEAVER_JOURNAL_DIR") ?? "/var/lib/diskweaver/journal"));
builder.Services.AddSingleton<DiskWeaverAccessPolicy>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));

// Cookie session for the standalone SPA's own TCP listener (below) -- Cockpit never hits this:
// it authenticates its own session before cockpit-bridge ever proxies onto the Unix socket, and
// the socket's Group=diskweaver/UMask=0007 (packaging/diskweaverd.service) is what gates that
// path. Minimal-API JSON endpoints, not MVC pages, so the redirect-to-login-page behavior the
// cookie handler defaults to (a 302) is wrong here -- overridden below to plain 401/403.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "diskweaver_session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

// Production (Cockpit) transport is a Unix socket; set DISKWEAVER_SOCKET to opt into it.
// Without it, ASP.NET Core's normal configuration applies (ASPNETCORE_URLS, or the .NET 8+
// container images' default ASPNETCORE_HTTP_PORTS=8080) -- much easier to `curl` during
// development/Docker testing than reaching into a socket file.
var socketPath = Environment.GetEnvironmentVariable("DISKWEAVER_SOCKET");
if (socketPath is not null)
{
    builder.WebHost.ConfigureKestrel(options => options.ListenUnixSocket(socketPath, listenOptions =>
    {
        // Tags every connection this specific listener accepts -- see TrustedTransportFeature's
        // own comment for why this replaced trying to identify the listener after the fact.
        listenOptions.Use(next => connectionContext =>
        {
            connectionContext.Features.Set(new TrustedTransportFeature());
            return next(connectionContext);
        });
    }));
}

// Standalone SPA transport: a second, independent listener alongside the Unix socket (both can
// be active at once -- Cockpit keeps using the socket, the SPA uses this). Loopback-only unless
// DISKWEAVER_HTTP_BIND says otherwise -- e.g. behind a reverse proxy terminating TLS on the same
// host. Off by default (no DISKWEAVER_HTTP_PORT) since it's a new, separate attack surface
// authenticated only by PAM + DiskWeaverAccessPolicy rather than the socket's OS-level permissions.
var httpPortSetting = Environment.GetEnvironmentVariable("DISKWEAVER_HTTP_PORT");
if (httpPortSetting is not null)
{
    if (!int.TryParse(httpPortSetting, out var httpPort))
    {
        throw new InvalidOperationException($"DISKWEAVER_HTTP_PORT '{httpPortSetting}' is not a valid port number.");
    }

    var bindAddress = Environment.GetEnvironmentVariable("DISKWEAVER_HTTP_BIND") is { } bindSetting
        ? IPAddress.Parse(bindSetting)
        : IPAddress.Loopback;
    builder.WebHost.ConfigureKestrel(options => options.Listen(bindAddress, httpPort));
}

var app = builder.Build();

// Serves the standalone SPA's own index.html/js/css (DiskWeaver.Cockpit/standalone/ at build
// time -- see esbuild.config.mjs's second entry point) when DISKWEAVER_WEBROOT points at an
// install of it. Unset by default: Cockpit serves its own plugin files itself and never needs
// this, and there's no reason to serve static files at all over the Unix socket. Placed before
// the auth gate below (not exempted via path prefix like /auth/*) so the SPA shell itself -- the
// part that renders the login form -- loads without already being logged in; a request that
// doesn't match a real file here just falls through to the gate/API routes as normal.
var webRoot = Environment.GetEnvironmentVariable("DISKWEAVER_WEBROOT");
if (webRoot is not null)
{
    var fileProvider = new PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
}

app.UseAuthentication();

// Everything except /auth/* requires either the trusted transport (Unix socket -- Cockpit,
// tagged at accept-time above) or a signed-in cookie session (the standalone SPA's TCP listener).
app.Use(async (context, next) =>
{
    var viaTrustedSocket = context.Features.Get<TrustedTransportFeature>() is not null;
    var isAuthEndpoint = context.Request.Path.StartsWithSegments("/auth");
    if (!viaTrustedSocket && !isAuthEndpoint && context.User.Identity?.IsAuthenticated != true)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next(context);
});

app.MapPost("/auth/login", async (HttpContext context, LoginRequest request, DiskWeaverAccessPolicy accessPolicy) =>
{
    if (!PamAuthenticator.Authenticate("diskweaver", request.Username, request.Password, out var authError))
    {
        return TextError(StatusCodes.Status401Unauthorized, authError ?? "Authentication failed.");
    }

    if (!accessPolicy.IsAuthorized(request.Username))
    {
        return TextError(StatusCodes.Status403Forbidden,
            $"'{request.Username}' authenticated but isn't authorized -- must be root or a member of the diskweaver group.");
    }

    var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, request.Username)], CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Ok();
});

app.MapPost("/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
});

// Lets the standalone SPA tell, on load, whether its stored cookie (if any) is still a valid
// session -- without this it would have no way to skip straight to the app for a returning user
// instead of always showing the login form first. This path falls under the blanket "/auth"
// exemption in the gate above (deliberately -- it needs to be reachable pre-login to answer "no"),
// so unlike every other protected endpoint it has to check authentication itself instead of
// relying on that gate.
app.MapGet("/auth/session", (HttpContext context) => context.User.Identity?.IsAuthenticated == true
    ? Results.Ok(new SessionResponse(context.User.Identity.Name!))
    : Results.StatusCode(StatusCodes.Status401Unauthorized));

app.MapGet("/inventory", (IDiskInventorySource inventory, IArrayMembershipSource arrayMembership, ICommandRunner commandRunner) =>
{
    try
    {
        var disks = inventory.GetDisks();
        // Cross-references any disk lsblk flagged as carrying a RAID/LVM signature (but couldn't
        // itself say whose) against live mdadm/LVM state, so the Cockpit UI can tell a leftover
        // DiskWeaver pool disk apart from an unrelated foreign RAID/LVM disk. See
        // Disk.RaidLvmSignatureOwner and DiskSignatureOwnership's doc comments.
        var annotated = DiskSignatureOwnership.Annotate(disks, inventory.GetPartitionPaths(), arrayMembership, commandRunner);
        return Results.Ok(annotated);
    }
    catch (InvalidOperationException ex)
    {
        return TextError(StatusCodes.Status500InternalServerError, ex.Message);
    }
});

app.MapGet("/pools", (IPoolStateSource poolState) =>
{
    try
    {
        return Results.Ok(poolState.GetPools());
    }
    catch (InvalidOperationException ex)
    {
        return TextError(StatusCodes.Status500InternalServerError, ex.Message);
    }
});

app.MapPost("/plan", (PlanRequest request, IDiskInventorySource inventory, IPoolStateSource poolState, PlanCache cache) =>
{
    if (!TryParseRedundancy(request.Redundancy, out var redundancy))
    {
        return TextError(StatusCodes.Status400BadRequest, $"Unknown redundancy level '{request.Redundancy}'. Use none, dwr1, or dwr2.");
    }

    if (!TryNormalizePoolName(request.PoolName, out var poolName, out var poolNameError))
    {
        return TextError(StatusCodes.Status400BadRequest, poolNameError!);
    }

    if (!CommandPlanner.ValidChunkSizesKb.Contains(request.ChunkSizeKb))
    {
        return TextError(StatusCodes.Status400BadRequest,
            $"Unsupported chunkSizeKb {request.ChunkSizeKb} -- use one of: "
            + $"{string.Join(", ", CommandPlanner.ValidChunkSizesKb)}.");
    }

    if (!TryParseRaid5ConsistencyPolicy(request.Raid5ConsistencyPolicy, out var raid5ConsistencyPolicy))
    {
        return TextError(StatusCodes.Status400BadRequest,
            $"Unknown raid5ConsistencyPolicy '{request.Raid5ConsistencyPolicy}'. Use resync, bitmap, or ppl.");
    }

    if (poolState.GetPools().Any(p => p.PoolName == poolName))
    {
        return TextError(StatusCodes.Status400BadRequest,
            $"Pool '{poolName}' already exists -- choose a different poolName, or expand the existing "
            + $"one via POST /pools/{poolName}/expand instead.");
    }

    IReadOnlyList<Disk> selectedDisks;
    try
    {
        var allDisks = inventory.GetDisks();
        selectedDisks = DiskSelector.Select(allDisks, request.DiskIds);
        // A brand-new pool must only ever be built on disks that are actually blank -- refuses a
        // disk with a partition table, a mounted filesystem, or a foreign RAID/LVM signature
        // before it ever reaches CommandPlanner.Build's parted/mdadm/wipefs commands.
        DiskSelector.EnsureBlank(selectedDisks);
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        return TextError(StatusCodes.Status400BadRequest, ex.Message);
    }

    PoolPlan plan;
    try
    {
        plan = PartitionLayout.PlanForRealDisks(selectedDisks, redundancy);
    }
    catch (ArgumentException ex)
    {
        return TextError(StatusCodes.Status400BadRequest, ex.Message);
    }

    var id = cache.Store(
        selectedDisks, redundancy, poolName, plan, request.ThinProvisioned, request.AssumeClean, request.ChunkSizeKb,
        raid5ConsistencyPolicy);
    return Results.Ok(new PlanResponse(id, plan));
});

app.MapGet("/plan/{id}/script", (string id, string? kind, PlanCache cache) =>
{
    if (!cache.TryGet(id, out var plan) || plan is null || !cache.TryGetPoolName(id, out var poolName))
    {
        return Results.NotFound();
    }

    cache.TryGetThinProvisioned(id, out var thinProvisioned);
    cache.TryGetAssumeClean(id, out var assumeClean);
    cache.TryGetChunkSizeKb(id, out var chunkSizeKb);
    cache.TryGetRaid5ConsistencyPolicy(id, out var raid5ConsistencyPolicy);
    var executionPlan = string.Equals(kind, "teardown", StringComparison.OrdinalIgnoreCase)
        ? CommandPlanner.BuildTeardown(plan, poolName!, thinProvisioned: thinProvisioned)
        : CommandPlanner.Build(
            plan, poolName!, thinProvisioned: thinProvisioned, assumeClean: assumeClean, chunkSizeKb: chunkSizeKb,
            raid5ConsistencyPolicy: raid5ConsistencyPolicy);

    return Results.Text(ShellScriptEmitter.Render(executionPlan), "text/plain");
});

// Runs synchronously to completion (or first failure) within the request. Steps are individually
// fast (parted/mdadm --create/pvcreate/etc. all return quickly; RAID resync continues in the
// background afterwards). Deliberately never seeds this run from a journal persisted by an
// earlier request -- a persisted journal is a record of a past attempt, not a trustworthy claim
// about current reality (it can't tell "nothing has changed" apart from "something reset back to
// looking the same"), so treating it as authoritative for what to skip is exactly the kind of
// shadow-config bug this used to have. Every call re-verifies its own preconditions against live
// state instead and always starts fresh -- see docs/execution.md's "Resuming and retrying".
app.MapPost("/plan/{id}/execute", (
    string id, string? kind, PlanCache cache, IDiskInventorySource inventory, IPoolStateSource poolState,
    IJournalStore journalStore, IStepRunner stepRunner) =>
{
    if (!cache.TryGet(id, out var plan) || plan is null || !cache.TryGetPoolName(id, out var poolName))
    {
        return Results.NotFound();
    }

    var normalizedKind = string.Equals(kind, "teardown", StringComparison.OrdinalIgnoreCase) ? "teardown" : "build";
    var executionId = $"{id}-{normalizedKind}";

    // "Already done" is decided by asking live reality, never a journal: a build is done if a
    // pool by this name already exists right now.
    if (normalizedKind == "build" && poolState.GetPools().Any(p => p.PoolName == poolName))
    {
        return Results.Ok(new ExecutionJournal(executionId, normalizedKind, ExecutionJournalStatus.Succeeded, []));
    }

    cache.TryGetSelectedDisks(id, out var selectedDisks);
    var conflict = normalizedKind == "teardown"
        ? TeardownTargetStillExists(plan, poolState.GetPools())
        : StaleDisks(selectedDisks!, inventory.GetDisks());
    if (conflict is not null)
    {
        return TextError(StatusCodes.Status409Conflict, conflict);
    }

    cache.TryGetThinProvisioned(id, out var thinProvisioned);
    cache.TryGetAssumeClean(id, out var assumeClean);
    cache.TryGetChunkSizeKb(id, out var chunkSizeKb);
    cache.TryGetRaid5ConsistencyPolicy(id, out var raid5ConsistencyPolicy);
    var executionPlan = normalizedKind == "teardown"
        ? CommandPlanner.BuildTeardown(plan, poolName!, thinProvisioned: thinProvisioned)
        : CommandPlanner.Build(
            plan, poolName!, thinProvisioned: thinProvisioned, assumeClean: assumeClean, chunkSizeKb: chunkSizeKb,
            raid5ConsistencyPolicy: raid5ConsistencyPolicy);

    ExecutionJournal? journal = null;
    do
    {
        journal = ExecutionRunner.AdvanceOneStep(executionId, normalizedKind, executionPlan, journal, stepRunner);
        journalStore.Save(journal);
    } while (journal.Status == ExecutionJournalStatus.Running);

    return Results.Ok(journal);
});

app.MapGet("/execute/{id}/status", (string id, IJournalStore journalStore) =>
{
    var journal = journalStore.Load(id);
    return journal is null ? Results.NotFound() : Results.Ok(journal);
});

// Tears down a pool using its actual on-disk state (GET /pools), not a regenerated plan --
// unlike POST /plan/{id}/execute?kind=teardown, this doesn't need the original disk
// selection/redundancy that produced the pool, so it works for any pool this host currently has,
// not just one planned in the current session. Same synchronous run-to-completion-or-failure
// model as /plan/{id}/execute. Deliberately never seeds this run from a previously-persisted
// journal -- see that endpoint's comment for why. Reaching this point already proves the pool
// currently exists (the null-check just above), so there's no "already torn down" case to special
// case here the way build has an "already built" one: if it were already gone, we'd have 404'd.
app.MapPost("/pools/{poolName}/teardown", (string poolName, IPoolStateSource poolState, IJournalStore journalStore, IStepRunner stepRunner) =>
{
    var pool = poolState.GetPools().FirstOrDefault(p => p.PoolName == poolName);
    if (pool is null)
    {
        return Results.NotFound();
    }

    if (pool.Error is not null)
    {
        return TextError(StatusCodes.Status409Conflict, pool.Error);
    }

    var executionPlan = CommandPlanner.BuildTeardownFromExisting(pool);
    // Folds in a content fingerprint of the pool's actual tiers (same one /expand uses) into the
    // execution id purely so a pool torn down and later rebuilt under the same name gets its own,
    // distinctly-named journal file for forensic/audit purposes -- not load-bearing for
    // correctness anymore, since the journal is never read back to decide anything.
    var executionId = $"pool-{poolName}-teardown-{ExecutionPlanCache.ComputeFingerprint(pool)}";

    ExecutionJournal? journal = null;
    do
    {
        journal = ExecutionRunner.AdvanceOneStep(executionId, "teardown", executionPlan, journal, stepRunner);
        journalStore.Save(journal);
    } while (journal.Status == ExecutionJournalStatus.Running);

    return Results.Ok(journal);
});

// Plans adding disks to a pool found via GET /pools. Returns 0, 1, or 2 candidate plans by
// default -- one that adds protection, one that adds space (see docs/algorithm.md's expand
// tenets and ExpansionOptionsPlanner) -- rather than requiring the caller to already know which
// internal mechanism fits this pool's current shape. targetArrayDevice/redundancy remain as
// advanced/manual escape hatches that bypass the two-option computation and always return exactly
// one plan, built precisely as requested.
app.MapPost("/pools/{poolName}/expand", (string poolName, ExpansionRequest request, IPoolStateSource poolState, IDiskInventorySource inventory, ExecutionPlanCache cache) =>
{
    var pool = poolState.GetPools().FirstOrDefault(p => p.PoolName == poolName);
    if (pool is null)
    {
        return Results.NotFound();
    }

    if (pool.Error is not null)
    {
        return TextError(StatusCodes.Status409Conflict, pool.Error);
    }

    var modesRequested = new[] { request.TargetProtection is not null, request.Redundancy is not null, request.TargetArrayDevice is not null }.Count(b => b);
    if (modesRequested > 1)
    {
        return TextError(StatusCodes.Status400BadRequest,
            "Specify at most one of targetProtection, redundancy, or targetArrayDevice -- "
            + "redundancy/targetArrayDevice are advanced/manual modes that bypass the default two-option preview.");
    }

    if (!CommandPlanner.ValidChunkSizesKb.Contains(request.ChunkSizeKb))
    {
        return TextError(StatusCodes.Status400BadRequest,
            $"Unsupported chunkSizeKb {request.ChunkSizeKb} -- use one of: "
            + $"{string.Join(", ", CommandPlanner.ValidChunkSizesKb)}.");
    }

    if (!TryParseRaid5ConsistencyPolicy(request.Raid5ConsistencyPolicy, out var raid5ConsistencyPolicy))
    {
        return TextError(StatusCodes.Status400BadRequest,
            $"Unknown raid5ConsistencyPolicy '{request.Raid5ConsistencyPolicy}'. Use resync, bitmap, or ppl.");
    }

    var candidates = new List<(string Intent, PoolPlan Desired, RedundancyLevel? AchievedRedundancy)>();

    if (request.TargetArrayDevice is not null)
    {
        // "Advanced" mode: send every requested disk toward completing this one named tier into a
        // real mirror, rather than recomputing tiering from scratch. See ProtectionPlanner's doc
        // for why only a single-real-member Mirror tier (unprotected-by-design or degraded from a
        // disk failure) is eligible.
        var targetTier = pool.Tiers.FirstOrDefault(t => t.ArrayDevice == request.TargetArrayDevice);
        if (targetTier is null)
        {
            return TextError(StatusCodes.Status400BadRequest, $"Pool '{poolName}' has no tier '{request.TargetArrayDevice}'.");
        }

        if (targetTier.RaidLevel != RaidLevel.Mirror || targetTier.DiskIds.Count >= targetTier.ConfiguredMemberCountOrDefault)
        {
            return TextError(StatusCodes.Status400BadRequest,
                $"Tier '{request.TargetArrayDevice}' isn't eligible to be completed this way -- it needs to "
                + $"currently be missing a real member (unprotected-by-design or degraded), but has "
                + $"{targetTier.DiskIds.Count} of {targetTier.ConfiguredMemberCountOrDefault} configured ({targetTier.RaidLevel}).");
        }

        IReadOnlyList<Disk> targetDisks;
        try
        {
            targetDisks = DiskSelector.Select(inventory.GetDisks(), request.DiskIds);
            // Unlike a plain blank-disk requirement, a disk being completed here might already be
            // a member of *another* tier in this same pool (splitting one larger disk across
            // several separate "protect this tier" calls) -- only its remaining spare capacity
            // needs to be free, not the whole disk.
            foreach (var disk in targetDisks)
            {
                DiskCapacityValidator.EnsureSpareCapacity(pool, disk, targetTier.SegmentSizeBytes);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return TextError(StatusCodes.Status400BadRequest, ex.Message);
        }

        var manualDesired = new PoolPlan(
            pool.Tiers.Select(t => t.ArrayDevice == request.TargetArrayDevice
                ? new Tier(t.SegmentSizeBytes, [.. t.DiskIds, .. targetDisks.Select(d => d.Id)], t.RaidLevel, t.SegmentSizeBytes)
                : new Tier(t.SegmentSizeBytes, t.DiskIds, t.RaidLevel, t.UsableBytes))
                .ToList(),
            []);
        candidates.Add(("manual", manualDesired, null));
    }
    else if (request.Redundancy is not null)
    {
        // Advanced/manual mode: an explicit pool-wide redundancy request. "none" keeps the added
        // disk(s) out of TieringPlanner's normal shared-boundary grouping entirely -- each becomes
        // its own independent unprotected tier -- a deliberate per-tier choice, not the usual
        // "recompute the whole pool from all disks together."
        if (!TryParseRedundancy(request.Redundancy, out var explicitRedundancy))
        {
            return TextError(StatusCodes.Status400BadRequest, $"Unknown redundancy level '{request.Redundancy}'. Use none, dwr1, or dwr2.");
        }

        IReadOnlyList<Disk> newDisks;
        IReadOnlyList<Disk> selectedDisks;
        try
        {
            var allDisks = inventory.GetDisks();
            // Only the newly-requested disks need to be blank -- the pool's existing disks are
            // legitimately non-blank (they're already mdadm/LVM members) and are trusted because they
            // came from IPoolStateSource, not from user input.
            newDisks = DiskSelector.Select(allDisks, request.DiskIds);
            DiskSelector.EnsureBlank(newDisks);

            var existingDiskIds = pool.Tiers.SelectMany(t => t.DiskIds).Distinct();
            var wantedDiskIds = existingDiskIds.Concat(request.DiskIds).Distinct().ToArray();
            selectedDisks = DiskSelector.Select(allDisks, wantedDiskIds);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return TextError(StatusCodes.Status400BadRequest, ex.Message);
        }

        PoolPlan manualDesired;
        try
        {
            if (explicitRedundancy == RedundancyLevel.None)
            {
                var existingRedundancyForNone = InferRedundancy(pool);
                var existingDisks = DiskSelector.Select(selectedDisks, pool.Tiers.SelectMany(t => t.DiskIds).Distinct().ToArray());
                var existingDesired = PartitionLayout.PlanForRealDisks(existingDisks, existingRedundancyForNone);
                var newUnprotected = PartitionLayout.PlanForRealDisks(newDisks, RedundancyLevel.None);
                manualDesired = new PoolPlan([.. existingDesired.Tiers, .. newUnprotected.Tiers], existingDesired.Reserved);
            }
            else
            {
                manualDesired = PartitionLayout.PlanForRealDisks(selectedDisks, explicitRedundancy);
            }
        }
        catch (ArgumentException ex)
        {
            return TextError(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // A client-fixable conflict (pass a disk selection that doesn't need existing-redundancy
            // inference), not a server bug -- see InferRedundancy's own comment on why a pool's tiers
            // might not agree on one redundancy.
            return TextError(StatusCodes.Status409Conflict, ex.Message);
        }

        candidates.Add(("manual", manualDesired, explicitRedundancy));
    }
    else
    {
        // Default: compute up to two candidates (protection, space) rather than picking one
        // mechanism the caller has to already know applies. TargetProtection steers the protection
        // candidate's pool-wide redundancy-upgrade fallback (only reached when nothing degraded
        // needs completing -- see ExpansionOptionsPlanner). Omitting it opportunistically tries one
        // level *above* the pool's current redundancy (capped at Dwr2) rather than the same level --
        // defaulting to "same as current" would trivially never offer an upgrade at all, which is
        // exactly why a healthy Dwr1 pool getting disks that could reach Dwr2 used to never surface
        // that as an option unless the caller already knew to ask for it explicitly.
        RedundancyLevel targetProtection;
        if (request.TargetProtection is not null)
        {
            if (!TryParseRedundancy(request.TargetProtection, out targetProtection))
            {
                return TextError(StatusCodes.Status400BadRequest, $"Unknown targetProtection level '{request.TargetProtection}'. Use none, dwr1, or dwr2.");
            }
        }
        else
        {
            try
            {
                var currentRedundancy = InferRedundancy(pool);
                targetProtection = currentRedundancy < RedundancyLevel.Dwr2 ? currentRedundancy + 1 : currentRedundancy;
            }
            catch (InvalidOperationException)
            {
                // A mixed pool (tiers at different redundancy levels) doesn't have one obvious
                // "current" level to try one above -- Dwr1 is a reasonable baseline, same fallback
                // used for HypotheticalRebuildCapacityBytes below.
                targetProtection = RedundancyLevel.Dwr1;
            }
        }

        IReadOnlyList<Disk> newDisks;
        IReadOnlyList<Disk> allDisks;
        try
        {
            var inventoryDisks = inventory.GetDisks();
            newDisks = DiskSelector.Select(inventoryDisks, request.DiskIds);
            DiskSelector.EnsureBlank(newDisks);

            var allDiskIds = pool.Tiers.SelectMany(t => t.DiskIds).Concat(request.DiskIds).Distinct().ToArray();
            allDisks = DiskSelector.Select(inventoryDisks, allDiskIds);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return TextError(StatusCodes.Status400BadRequest, ex.Message);
        }

        var options = ExpansionOptionsPlanner.ComputeOptions(pool, allDisks, newDisks, targetProtection);
        if (options.Protection is not null)
        {
            candidates.Add((ExpansionOptionsPlanner.ProtectionIntent, options.Protection.Desired, options.Protection.AchievedRedundancy));
        }

        if (options.Space is not null)
        {
            candidates.Add((ExpansionOptionsPlanner.SpaceIntent, options.Space.Desired, options.Space.AchievedRedundancy));
        }
    }

    var responseOptions = new List<ExpansionOption>();
    foreach (var (intent, desired, achievedRedundancy) in candidates)
    {
        ExecutionPlan executionPlan;
        long achievedCapacityBytes;
        try
        {
            executionPlan = CommandPlanner.BuildIncremental(
                pool, desired, assumeClean: request.AssumeClean, chunkSizeKb: request.ChunkSizeKb,
                raid5ConsistencyPolicy: raid5ConsistencyPolicy);
            achievedCapacityBytes = CommandPlanner.AchievedCapacityBytes(pool, desired);
        }
        catch (InvalidOperationException ex)
        {
            // Only "manual" mode can reach here with just one candidate -- ExpansionOptionsPlanner's
            // own candidates are always incrementally buildable by construction.
            return TextError(StatusCodes.Status400BadRequest, ex.Message);
        }

        var id = cache.Store(poolName, request.DiskIds, executionPlan, ExecutionPlanCache.ComputeFingerprint(pool), intent);
        responseOptions.Add(new ExpansionOption(intent, id, desired, achievedCapacityBytes, achievedRedundancy?.ToString().ToLowerInvariant()));
    }

    // Best-effort/informational only -- e.g. a mixed-redundancy pool where TierRedundancy.Infer
    // itself refuses to guess. Never blocks the real candidates above.
    long? hypotheticalRebuildCapacityBytes = null;
    try
    {
        var allDiskIds = pool.Tiers.SelectMany(t => t.DiskIds).Concat(request.DiskIds).Distinct().ToArray();
        var allDisks = DiskSelector.Select(inventory.GetDisks(), allDiskIds);
        var rebuildRedundancy = TierRedundancy.Infer(pool);
        if (rebuildRedundancy == RedundancyLevel.None)
        {
            // "Rebuild as JBOD" isn't an interesting best case to show -- Dwr1 is a reasonable
            // baseline for "what if this were protected" instead.
            rebuildRedundancy = RedundancyLevel.Dwr1;
        }

        hypotheticalRebuildCapacityBytes = CommandPlanner.HypotheticalFullRebuildCapacityBytes(allDisks, rebuildRedundancy);
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
    }

    return Results.Ok(new ExpansionOptionsResponse(responseOptions, hypotheticalRebuildCapacityBytes));
});

app.MapGet("/pools/{poolName}/expand/{id}/script", (string poolName, string id, ExecutionPlanCache cache) =>
{
    if (!cache.TryGet(id, out var plan) || plan is null)
    {
        return Results.NotFound();
    }

    return Results.Text(ShellScriptEmitter.Render(plan), "text/plain");
});

app.MapPost("/pools/{poolName}/expand/{id}/execute", (
    string poolName, string id, ExecutionPlanCache cache, IPoolStateSource poolState, IDiskInventorySource inventory,
    IJournalStore journalStore, IStepRunner stepRunner) =>
{
    if (!cache.TryGet(id, out var executionPlan) || executionPlan is null)
    {
        return Results.NotFound();
    }

    var executionId = $"{id}-expand";

    // Always re-verified against live state, never gated behind "only if no journal exists yet"
    // -- see /plan/{id}/execute's comment for why a persisted journal is never trusted here. If
    // the pool genuinely reverted to exactly the state this plan was computed against (its
    // fingerprint matches), that's not staleness -- it's confirmation that replaying this same
    // cached plan is still exactly correct.
    var currentPool = poolState.GetPools().FirstOrDefault(p => p.PoolName == poolName);
    if (currentPool is null)
    {
        return TextError(StatusCodes.Status409Conflict, $"Pool '{poolName}' no longer exists -- it may have been torn down since this plan was created.");
    }

    cache.TryGetValidation(id, out var addedDiskIds, out var expectedFingerprint);
    if (ExecutionPlanCache.ComputeFingerprint(currentPool) != expectedFingerprint)
    {
        return TextError(StatusCodes.Status409Conflict,
            $"Pool '{poolName}' has changed since this plan was created -- re-run POST /pools/{poolName}/expand to get a fresh plan.");
    }

    try
    {
        DiskSelector.EnsureBlank(DiskSelector.Select(inventory.GetDisks(), addedDiskIds!));
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        return TextError(StatusCodes.Status409Conflict, ex.Message);
    }

    ExecutionJournal? journal = null;
    do
    {
        journal = ExecutionRunner.AdvanceOneStep(executionId, "expand", executionPlan, journal, stepRunner);
        journalStore.Save(journal);
    } while (journal.Status == ExecutionJournalStatus.Running);

    return Results.Ok(journal);
});

// Clears a stale/foreign filesystem/RAID/LVM signature off disk(s) so they pass
// DiskSelector.EnsureBlank and can be selected by POST /plan or /pools/{poolName}/expand
// afterward. Unlike teardown, doesn't assume the disks belong to any DiskWeaver-built pool --
// takes whatever partition paths lsblk currently reports (IDiskInventorySource.GetPartitionPaths)
// and, per disk, zeroes each partition's RAID superblock before wiping the parent disk itself.
app.MapPost("/disks/wipe", (
    WipeRequest request, IDiskInventorySource inventory, IArrayMembershipSource arrayMembership,
    IJournalStore journalStore, IStepRunner stepRunner) =>
{
    IReadOnlyList<Disk> selectedDisks;
    try
    {
        selectedDisks = DiskSelector.Select(inventory.GetDisks(), request.DiskIds);
    }
    catch (ArgumentException ex)
    {
        return TextError(StatusCodes.Status400BadRequest, ex.Message);
    }

    var diskIds = selectedDisks.Select(d => d.Id).ToList();
    var partitionPaths = inventory.GetPartitionPaths();

    // mdadm --zero-superblock refuses to touch a partition that's still an active/spare member of a
    // currently-assembled array (e.g. one left behind by a grow/reshape that failed partway
    // through) -- catching that here up front gives a clear, actionable message naming the array,
    // instead of running the step anyway and surfacing mdadm's own much less helpful "Couldn't open
    // ... for write - not zeroing" buried in the journal.
    var membership = arrayMembership.GetArrayMembership();
    var stillInArray = diskIds
        .SelectMany(diskId => partitionPaths.GetValueOrDefault(diskId, []).Select(partition => (diskId, partition)))
        .Where(p => membership.ContainsKey(p.partition))
        .Select(p => $"{p.partition} (on {p.diskId}) is still a member of {membership[p.partition]}")
        .ToList();
    if (stillInArray.Count > 0)
    {
        return TextError(StatusCodes.Status409Conflict,
            "Can't wipe -- " + string.Join("; ", stillInArray)
            + ". Stop the array first (mdadm --stop <array>) or remove just this member "
            + "(mdadm <array> --remove <partition>), then retry.");
    }

    var executionPlan = CommandPlanner.BuildWipe(diskIds, partitionPaths);

    // Slashes in a disk id (e.g. "/dev/loop3") aren't safe as part of a journal file name --
    // FileJournalStore uses the execution id as-is (see its PathFor) -- so the id is built from
    // each disk's trailing path component instead, the same de-slashing the wizard/inventory UI
    // already does when suggesting `--only`/`--disks` names.
    var executionId = "wipe-" + string.Join('_', diskIds.Select(id => id.Split('/')[^1]));

    // Never seeded from a previously-persisted journal -- see /plan/{id}/execute's comment for
    // why. The membership check above already re-verifies live state unconditionally on every
    // call, so this endpoint never needed the "only if no journal yet" gate the others had.
    ExecutionJournal? journal = null;
    do
    {
        journal = ExecutionRunner.AdvanceOneStep(executionId, "wipe", executionPlan, journal, stepRunner);
        journalStore.Save(journal);
    } while (journal.Status == ExecutionJournalStatus.Running);

    return Results.Ok(journal);
});

// Brings up any mdadm array/LVM volume group that exists on disk but isn't currently
// assembled/active -- the situation left behind by installing a fresh OS onto disks that already
// hold a DiskWeaver pool (the `diskweaver-managed` VG tag lives in LVM's own on-disk metadata, so
// it survives the reinstall, but the kernel still needs telling to reassemble/activate it before
// GET /pools can see it -- see MdadmLvmPoolStateSource's "[unknown]" PV handling). Unconditional,
// not scoped to any one pool: there's nothing to select yet, that's exactly the problem this fixes.
app.MapPost("/arrays/reassemble", (IJournalStore journalStore, IStepRunner stepRunner) =>
{
    var executionPlan = CommandPlanner.BuildReassemble();
    var executionId = "arrays-reassemble";

    ExecutionJournal? journal = null;
    do
    {
        journal = ExecutionRunner.AdvanceOneStep(executionId, "reassemble", executionPlan, journal, stepRunner);
        journalStore.Save(journal);
    } while (journal.Status == ExecutionJournalStatus.Running);

    return Results.Ok(journal);
});

app.Run();

// Every error response goes through this rather than Results.BadRequest(string)/Conflict(string)/
// Problem(string): those serialize the message as a JSON string, and Cockpit's cockpit.http()
// client only surfaces a response body into the error it throws when Content-Type is text/plain --
// otherwise app.js's error banner shows nothing but the generic HTTP reason phrase ("Bad Request",
// "Conflict"), silently discarding whatever specific, actionable message the daemon actually sent
// (confirmed against cockpit.js's own source: it only attaches the body when
// `Content-Type.indexOf("text/plain") === 0`). GET endpoints that already return real content
// (e.g. /plan/{id}/script) already use Results.Text for the same reason.
static IResult TextError(int statusCode, string message) => Results.Text(message, "text/plain", statusCode: statusCode);

// Compares the disks a build plan was computed against with live inventory right before Execute
// runs a single command. Returns null when nothing relevant changed, or a message naming every
// disk that did -- gone, resized, or no longer blank (already partitioned/mounted/foreign-owned).
static string? StaleDisks(IReadOnlyList<Disk> expected, IReadOnlyList<Disk> current)
{
    var byId = current.ToDictionary(d => d.Id);
    var problems = new List<string>();

    foreach (var disk in expected)
    {
        if (!byId.TryGetValue(disk.Id, out var now))
        {
            problems.Add($"{disk.Id} is no longer present");
        }
        else if (now.SizeBytes != disk.SizeBytes)
        {
            problems.Add($"{disk.Id} changed size ({disk.SizeBytes:N0} -> {now.SizeBytes:N0} bytes)");
        }
        else if (!now.IsBlank)
        {
            problems.Add($"{disk.Id} is no longer blank -- it already has a partition table, a mounted filesystem, or a foreign RAID/LVM signature");
        }
    }

    return problems.Count == 0
        ? null
        : "Disk state changed since this plan was created -- re-run POST /plan to get a fresh plan: " + string.Join("; ", problems);
}

// A `kind=teardown` execute reverses a pool CommandPlanner.Build already actually built, so its
// disks are legitimately non-blank -- StaleDisks' blank check doesn't apply here. Instead, confirm
// a DiskWeaver-owned pool (per GetPools(), tag-filtered) still exists whose disks are exactly the
// ones this teardown plan was computed against.
static string? TeardownTargetStillExists(PoolPlan plan, IReadOnlyList<ExistingPoolState> pools)
{
    var expectedDiskIds = plan.Tiers.SelectMany(t => t.DiskIds).ToHashSet();
    var stillExists = pools.Any(p => p.Tiers.SelectMany(t => t.DiskIds).ToHashSet().SetEquals(expectedDiskIds));

    return stillExists
        ? null
        : "No matching DiskWeaver pool found on this host for this teardown plan -- it may have already "
            + "been torn down, or its disks changed since this plan was created. Use GET /pools and "
            + "POST /pools/{poolName}/teardown instead, which always acts on current state.";
}

// Moved to DiskWeaver.Executor.TierRedundancy.Infer so IndependentGrowPlanner can share the same
// inference logic; kept as a thin alias here since the call site above reads better without the
// namespace-qualified name.
static RedundancyLevel InferRedundancy(ExistingPoolState pool) => TierRedundancy.Infer(pool);

static bool TryParseRedundancy(string value, out RedundancyLevel redundancy)
{
    switch (value.Trim().ToLowerInvariant())
    {
        case "none":
        case "0":
            redundancy = RedundancyLevel.None;
            return true;
        case "dwr1":
        case "1":
            redundancy = RedundancyLevel.Dwr1;
            return true;
        case "dwr2":
        case "2":
            redundancy = RedundancyLevel.Dwr2;
            return true;
        default:
            redundancy = default;
            return false;
    }
}

static bool TryParseRaid5ConsistencyPolicy(string value, out Raid5ConsistencyPolicy raid5ConsistencyPolicy)
{
    switch (value.Trim().ToLowerInvariant())
    {
        case "resync":
            raid5ConsistencyPolicy = Raid5ConsistencyPolicy.Resync;
            return true;
        case "bitmap":
            raid5ConsistencyPolicy = Raid5ConsistencyPolicy.Bitmap;
            return true;
        case "ppl":
            raid5ConsistencyPolicy = Raid5ConsistencyPolicy.Ppl;
            return true;
        default:
            raid5ConsistencyPolicy = default;
            return false;
    }
}

// Empty/omitted PoolName means "use the single-pool default", matching behavior before
// multi-pool support existed. A non-default name is restricted to what's safe to embed directly
// in a VG name and an mdadm array name (`/dev/md/{poolName}-tier<N>`) -- alphanumeric, starting
// with a letter/digit, only `-`/`_`/`.` otherwise. This never reaches a shell (ExecutionStep
// arguments are passed via ArgumentList, not string-interpolated into a command line), but a
// name that looks like a command flag (e.g. starting with `-`) is still worth refusing outright.
static bool TryNormalizePoolName(string? requested, out string poolName, out string? error)
{
    poolName = string.IsNullOrWhiteSpace(requested) ? "diskweaver-pool" : requested;
    error = PoolNamePattern().IsMatch(poolName)
        ? null
        : $"Invalid poolName '{poolName}': must start with a letter or digit and contain only "
            + "letters, digits, '-', '_', or '.'.";
    return error is null;
}

public partial class Program
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$")]
    private static partial System.Text.RegularExpressions.Regex PoolNamePattern();
}
