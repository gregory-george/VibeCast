using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeCast.AppHost;
using VibeCast.Data;

namespace VibeCast.Feeds;

/// <summary>
/// Best-effort fetch of a feed's cover art to downloads/&lt;slug&gt;/cover.&lt;ext&gt;, so
/// it survives offline and doesn't depend on a third-party host staying reachable
/// (CLAUDE.md: portability, no phone-home beyond explicit refresh). Never throws --
/// a failed or missing artwork fetch must not block adding or refreshing a feed.
/// Skips the network round-trip once a file is on disk for the feed's current
/// ArtworkUrl; re-downloads when ArtworkUrl has changed since the last fetch.
/// </summary>
internal sealed class FeedArtworkService(
    HttpClient httpClient,
    IDbContextFactory<AppDbContext> dbContextFactory,
    ILogger<FeedArtworkService> logger)
{
    private const long MaxArtworkBytes = 5 * 1024 * 1024;
    private const int BufferSize = 65536;

    public async Task EnsureArtworkAsync(int feedId, CancellationToken ct)
    {
        try
        {
            await EnsureArtworkCoreAsync(feedId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Artwork fetch failed for feed {FeedId}", feedId);
        }
    }

    private async Task EnsureArtworkCoreAsync(int feedId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var feed = await db.Feeds.FindAsync([feedId], ct);
        if (feed is null || feed.ArtworkUrl is null)
        {
            return;
        }

        if (feed.ArtworkFileName is not null && feed.ArtworkDownloadedUrl == feed.ArtworkUrl)
        {
            var existingPath = Path.Combine(AppPaths.DownloadsDirectory, feed.Slug, feed.ArtworkFileName);
            if (File.Exists(existingPath))
            {
                return;
            }
        }

        using var response = await httpClient.GetAsync(feed.ArtworkUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        if (!ArtworkContentType.TryGetExtension(response.Content.Headers.ContentType?.MediaType, out var extension))
        {
            return;
        }

        if (response.Content.Headers.ContentLength is { } declaredLength && declaredLength > MaxArtworkBytes)
        {
            return;
        }

        var feedDir = Path.Combine(AppPaths.DownloadsDirectory, feed.Slug);
        Directory.CreateDirectory(feedDir);

        var fileName = $"cover{extension}";
        var finalPath = Path.Combine(feedDir, fileName);
        var partialPath = finalPath + ".partial";

        if (!await TryStreamToFileAsync(response, partialPath, ct))
        {
            File.Delete(partialPath);
            return;
        }

        File.Move(partialPath, finalPath, overwrite: true);

        if (feed.ArtworkFileName is not null && feed.ArtworkFileName != fileName)
        {
            var stalePath = Path.Combine(feedDir, feed.ArtworkFileName);
            if (File.Exists(stalePath))
            {
                File.Delete(stalePath);
            }
        }

        feed.ArtworkFileName = fileName;
        feed.ArtworkDownloadedUrl = feed.ArtworkUrl;
        await db.SaveChangesAsync(ct);
    }

    private static async Task<bool> TryStreamToFileAsync(HttpResponseMessage response, string partialPath, CancellationToken ct)
    {
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(
            partialPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

        var buffer = new byte[BufferSize];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            totalRead += bytesRead;
            if (totalRead > MaxArtworkBytes)
            {
                return false;
            }

            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
        }

        return true;
    }
}
