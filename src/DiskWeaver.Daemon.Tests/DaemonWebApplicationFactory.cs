using DiskWeaver.Core.Executor.Abstractions;
using DiskWeaver.Core.Inventory.Abstractions;
using DiskWeaver.Executor.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DiskWeaver.Daemon.Tests;

/// <summary>
/// Boots the daemon in-process with the real lsblk/mdadm/lvm-backed sources swapped for fakes
/// -- lets every endpoint be tested on any OS, without real disks, lsblk, mdadm, or Docker involved.
/// </summary>
public sealed class DaemonWebApplicationFactory : WebApplicationFactory<Program>
{
    public FakeDiskInventorySource Inventory { get; } = new();
    public FakeArrayMembershipSource ArrayMembership { get; } = new();
    public FakePoolStateSource PoolState { get; } = new();
    public FakeStepRunner StepRunner { get; } = new();
    public InMemoryJournalStore JournalStore { get; } = new();
    public FakeCommandRunner CommandRunner { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDiskInventorySource>();
            services.AddSingleton<IDiskInventorySource>(Inventory);
            services.RemoveAll<IArrayMembershipSource>();
            services.AddSingleton<IArrayMembershipSource>(ArrayMembership);
            services.RemoveAll<IPoolStateSource>();
            services.AddSingleton<IPoolStateSource>(PoolState);
            services.RemoveAll<IStepRunner>();
            services.AddSingleton<IStepRunner>(StepRunner);
            services.RemoveAll<IJournalStore>();
            services.AddSingleton<IJournalStore>(JournalStore);
            services.RemoveAll<ICommandRunner>();
            services.AddSingleton<ICommandRunner>(CommandRunner);
            services.AddSingleton<IStartupFilter, TrustedSocketStartupFilter>();
        });
    }
}
