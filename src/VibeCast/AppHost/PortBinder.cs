using System.Net;
using VibeCast.Components;
using VibeCast.Opml;
using VibeCast.Playback;

namespace VibeCast.AppHost;

internal static class PortBinder
{
    public const int DefaultPort = 5123;
    private const int MaxAttempts = 50;

    /// <summary>
    /// Builds a fresh WebApplicationBuilder per attempt and tries to bind Kestrel to
    /// 127.0.0.1 starting at <paramref name="preferredPort"/>, walking up to the next
    /// free port on a bind failure. A WebApplication is consumed by Build()/StartAsync(),
    /// so a failed attempt must be disposed and a new one built for the next port.
    /// </summary>
    public static async Task<(WebApplication App, int Port)> BuildAndBindAsync(
        string[] args,
        int preferredPort,
        Action<WebApplicationBuilder> configureServices)
    {
        var port = preferredPort;

        for (var attempt = 0; attempt < MaxAttempts; attempt++, port++)
        {
            var builder = WebApplication.CreateBuilder(args);
            configureServices(builder);
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));

            var app = builder.Build();
            ConfigurePipeline(app);

            try
            {
                await app.StartAsync();
                return (app, port);
            }
            catch (IOException)
            {
                await app.DisposeAsync();
            }
        }

        throw new InvalidOperationException(
            $"Could not bind to any port starting at {preferredPort} after {MaxAttempts} attempts.");
    }

    private static void ConfigurePipeline(WebApplication app)
    {
        // No HTTPS redirection / HSTS: http://localhost is a secure context and the
        // app never binds anything but 127.0.0.1, so there is no TLS in this app at all.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapMediaEndpoints();
        app.MapOpmlEndpoints();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
    }
}
