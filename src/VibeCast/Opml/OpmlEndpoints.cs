namespace VibeCast.Opml;

/// <summary>
/// Loopback-only export endpoint (the host binds 127.0.0.1, see CLAUDE.md). Import
/// is handled in-browser via Settings.razor's InputFile + FeedSubscriptionService,
/// since duplicate detection and per-feed defaults already live there.
/// </summary>
internal static class OpmlEndpoints
{
    public static void MapOpmlEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/opml/export", async (OpmlService opmlService, CancellationToken ct) =>
        {
            var bytes = await opmlService.ExportAsync(ct);
            return Results.File(bytes, "text/x-opml", "vibecast-subscriptions.opml");
        });
    }
}
