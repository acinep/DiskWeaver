using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace DiskWeaver.Daemon.Tests;

// DaemonWebApplicationFactory's in-memory TestServer never goes through Program.cs's real
// ListenUnixSocket(...).Use(...) connection tagging (see TrustedTransportFeature), so every
// request there would otherwise look untrusted and 401. DaemonWebApplicationFactory represents
// Cockpit's access path (the Unix socket -- see packaging/diskweaverd.service), so this sets the
// same marker feature a real socket connection gets tagged with, matching what a real
// Cockpit-facing request would actually look like. Tests for the standalone SPA's TCP+cookie path
// use a plain WebApplicationFactory<Program> without this filter instead -- see
// DaemonAuthMiddlewareTests.
internal sealed class TrustedSocketStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            context.Features.Set(new TrustedTransportFeature());
            await nextMiddleware();
        });
        next(app);
    };
}
