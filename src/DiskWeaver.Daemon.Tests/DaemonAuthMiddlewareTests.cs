using DiskWeaver.Core.Executor.Abstractions;
using DiskWeaver.Core.Inventory.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DiskWeaver.Daemon.Tests;

/// <summary>
/// Exercises the auth gate itself on the *untrusted* (standalone SPA / TCP) path -- unlike
/// DaemonWebApplicationFactory (which fakes a Unix-socket connection to represent Cockpit's
/// already-trusted transport), this uses a plain factory with no such feature, so requests here
/// land exactly where a real TCP client without a session cookie would.
/// </summary>
/// <remarks>
/// Deliberately doesn't exercise <c>POST /auth/login</c> itself: that calls into
/// <see cref="PamAuthenticator"/>, which P/Invokes libpam.so.0 -- present on the real Linux hosts
/// this daemon runs on, not on this test suite's Windows dev machine or necessarily a CI runner.
/// That path is verified manually against a real PAM-backed login instead (see docs/deployment.md).
/// </remarks>
public class DaemonAuthMiddlewareTests : IClassFixture<DaemonAuthMiddlewareTests.UntrustedTransportFactory>
{
    private readonly HttpClient _client;

    public DaemonAuthMiddlewareTests(UntrustedTransportFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetInventory_WithNoSessionCookie_ReturnsUnauthorized()
    {
        var response = await _client.GetAsync("/inventory");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public sealed class UntrustedTransportFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                // Only swapped so the host can start without real lsblk/mdadm on this machine --
                // this test never gets far enough to call them, since the auth gate rejects the
                // request first.
                services.RemoveAll<IDiskInventorySource>();
                services.AddSingleton<IDiskInventorySource>(new FakeDiskInventorySource());
            });
        }
    }
}
