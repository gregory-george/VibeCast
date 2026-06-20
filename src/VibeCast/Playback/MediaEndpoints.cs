using Microsoft.EntityFrameworkCore;
using VibeCast.AppHost;
using VibeCast.Data;
using VibeCast.Feeds;

namespace VibeCast.Playback;

/// <summary>
/// Loopback-only (the host binds 127.0.0.1, see CLAUDE.md) media endpoint serving
/// downloaded RSS files to the HTML5 player. Range support comes from ASP.NET
/// Core's built-in <c>enableRangeProcessing</c>, which is what makes seek/scrub work.
/// </summary>
internal static class MediaEndpoints
{
    public static void MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/media/episodes/{id:int}", async (int id, IDbContextFactory<AppDbContext> dbContextFactory, CancellationToken ct) =>
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(ct);
            var episode = await db.Episodes.Include(e => e.Feed).FirstOrDefaultAsync(e => e.Id == id, ct);
            if (episode is null || !episode.IsDownloaded || episode.DownloadedFileName is null)
            {
                return Results.NotFound();
            }

            var path = Path.Combine(AppPaths.DownloadsDirectory, episode.Feed.Slug, episode.DownloadedFileName);
            if (!File.Exists(path))
            {
                return Results.NotFound();
            }

            var contentType = string.IsNullOrWhiteSpace(episode.EnclosureMediaType)
                ? "application/octet-stream"
                : episode.EnclosureMediaType;

            return Results.File(path, contentType, enableRangeProcessing: true);
        });

        app.MapGet("/media/feeds/{id:int}/artwork", async (int id, IDbContextFactory<AppDbContext> dbContextFactory, CancellationToken ct) =>
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(ct);
            var feed = await db.Feeds.FindAsync([id], ct);
            if (feed?.ArtworkFileName is null)
            {
                return Results.NotFound();
            }

            var path = Path.Combine(AppPaths.DownloadsDirectory, feed.Slug, feed.ArtworkFileName);
            if (!File.Exists(path))
            {
                return Results.NotFound();
            }

            return Results.File(path, ArtworkContentType.ToContentType(feed.ArtworkFileName));
        });
    }
}
