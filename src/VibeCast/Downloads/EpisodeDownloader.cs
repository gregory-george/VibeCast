using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using VibeCast.AppHost;
using VibeCast.Data;

namespace VibeCast.Downloads;

/// <summary>
/// Streams a single RSS enclosure to disk with Range-based resume. Always
/// re-requests the original enclosure URL (never a cached resolved redirect
/// target), so a resume naturally re-resolves through redirector chains
/// (podtrac/op3/chartable) rather than reusing a stale signed URL. If the server
/// answers a resume request with 200 instead of 206, restarts from byte zero.
/// </summary>
internal sealed class EpisodeDownloader(
    HttpClient httpClient,
    IDbContextFactory<AppDbContext> dbContextFactory,
    DownloadProgressTracker progressTracker)
{
    private const int BufferSize = 65536;

    public async Task DownloadAsync(int episodeId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var episode = await db.Episodes.Include(e => e.Feed).FirstOrDefaultAsync(e => e.Id == episodeId, ct);
        if (episode is null || episode.EnclosureUrl is null || episode.IsDownloaded)
        {
            // Gone, YouTube (never downloaded), or already have it.
            return;
        }

        var feedTitle = episode.Feed.Title ?? episode.Feed.OriginalUrl;
        progressTracker.Set(new DownloadProgressSnapshot(episode.Id, episode.Title, feedTitle, DownloadStatus.Downloading, 0, null, null));

        var feedDir = Path.Combine(AppPaths.DownloadsDirectory, episode.Feed.Slug);
        Directory.CreateDirectory(feedDir);

        var finalFileName = DownloadFileNaming.BuildFileName(episode);
        var finalPath = Path.Combine(feedDir, finalFileName);
        var partialPath = finalPath + ".partial";

        try
        {
            await StreamToFileAsync(episode, feedTitle, partialPath, ct);

            File.Move(partialPath, finalPath, overwrite: true);

            episode.IsDownloaded = true;
            episode.DownloadedFileName = finalFileName;
            await db.SaveChangesAsync(ct);

            var finalSize = new FileInfo(finalPath).Length;
            progressTracker.Set(new DownloadProgressSnapshot(episode.Id, episode.Title, feedTitle, DownloadStatus.Completed, finalSize, finalSize, null));
        }
        catch (OperationCanceledException)
        {
            progressTracker.Set(new DownloadProgressSnapshot(episode.Id, episode.Title, feedTitle, DownloadStatus.Canceled, 0, null, null));
        }
        catch (Exception ex)
        {
            progressTracker.Set(new DownloadProgressSnapshot(episode.Id, episode.Title, feedTitle, DownloadStatus.Failed, 0, null, ex.Message));
        }
    }

    private async Task StreamToFileAsync(Episode episode, string feedTitle, string partialPath, CancellationToken ct)
    {
        var existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, episode.EnclosureUrl);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var resumed = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (existingLength > 0 && !resumed)
        {
            // Server doesn't honor Range and sent the full body back: restart from zero.
            existingLength = 0;
        }

        var totalBytes = response.Content.Headers.ContentLength is { } contentLength
            ? contentLength + existingLength
            : (long?)null;

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(
            partialPath,
            resumed ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            useAsync: true);

        var buffer = new byte[BufferSize];
        var totalRead = existingLength;
        var lastReportedAt = DateTime.UtcNow;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;

            // Throttle progress UI updates so a fast download doesn't flood the tracker.
            var now = DateTime.UtcNow;
            if ((now - lastReportedAt).TotalMilliseconds > 250)
            {
                progressTracker.Set(new DownloadProgressSnapshot(episode.Id, episode.Title, feedTitle, DownloadStatus.Downloading, totalRead, totalBytes, null));
                lastReportedAt = now;
            }
        }
    }
}
