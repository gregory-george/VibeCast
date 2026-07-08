using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeCast.Data;

namespace VibeCast.Feeds;

/// <summary>
/// Best-effort fetch of a YouTube video's duration. videos.xml carries no duration
/// field, so each YouTube episode's watch page is scraped individually for the
/// "lengthSeconds" field embedded in its player-response JSON. Never throws -- a
/// failed or missing duration fetch must not block a refresh; the episode simply
/// keeps a null DurationSeconds.
///
/// A scheduled premiere/live stream reports lengthSeconds=0 before it airs; that is
/// treated as "unknown, retry later" (null duration) rather than a real zero-length
/// episode, and its scheduledStartTime is captured so the UI can flag it as upcoming.
/// <see cref="BackfillFeedAsync"/> re-visits recent episodes still missing a duration
/// on each refresh, so a premiere's real length lands once it airs.
/// </summary>
internal sealed partial class YouTubeDurationService(
    HttpClient httpClient,
    IDbContextFactory<AppDbContext> dbContextFactory,
    ILogger<YouTubeDurationService> logger)
{
    // Each watch page is an independent network round-trip, so scrape them concurrently
    // but bounded rather than one-at-a-time. The DB writes are then applied afterward
    // through a single context (never shared across threads, per the AppDbContext rules).
    private const int MaxConcurrentScrapes = 4;

    // How far back BackfillFeedAsync will re-scrape episodes still missing a duration.
    // Wide enough to span a premiere scheduled well ahead of air plus a buffer, but
    // bounded so a permanently-durationless back catalog isn't re-scraped every refresh.
    private static readonly TimeSpan RescrapeWindow = TimeSpan.FromDays(45);

    /// <summary>
    /// Re-scrapes every YouTube episode in the feed that is recent yet still missing a
    /// real duration: brand-new items, premieres whose length only appears once they air
    /// (reported 0 beforehand), and rows a prior build left at 0. Bounded to recent
    /// pubdates by <see cref="RescrapeWindow"/>. Best-effort; never throws.
    /// </summary>
    public async Task BackfillFeedAsync(int feedId, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - RescrapeWindow;

        List<Episode> targets;
        await using (var db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            // DurationSeconds == 0 heals rows written by an older build that stored the
            // premiere's bogus zero; going forward a missing duration is stored as null.
            targets = await db.Episodes
                .Where(e => e.FeedId == feedId
                    && e.YouTubeVideoId != null
                    && (e.DurationSeconds == null || e.DurationSeconds == 0)
                    && e.PublishedAtUtc >= cutoff)
                .ToListAsync(ct);
        }

        await BackfillAsync(targets, ct);
    }

    public async Task BackfillAsync(IReadOnlyList<Episode> episodes, CancellationToken ct)
    {
        var targets = episodes.Where(e => e.YouTubeVideoId is not null).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var scraped = new ConcurrentDictionary<int, ScrapedVideoInfo>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxConcurrentScrapes,
            CancellationToken = ct,
        };

        await Parallel.ForEachAsync(targets, options, async (episode, token) =>
        {
            var info = await TryScrapeAsync(episode.YouTubeVideoId!, token);
            // Only record a usable scrape: a real duration, or a scheduled start for an
            // unaired premiere. A blank result (fetch failed, or neither field present)
            // leaves the row untouched to be retried on a later refresh.
            if (info.DurationSeconds is not null || info.ScheduledStartUtc is not null)
            {
                scraped[episode.Id] = info;
            }
        });

        if (scraped.IsEmpty)
        {
            return;
        }

        // Apply results through the tracking context rather than mutating the detached
        // instances passed in from the refresh pass.
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        foreach (var (episodeId, info) in scraped)
        {
            var tracked = await db.Episodes.FindAsync([episodeId], ct);
            if (tracked is not null)
            {
                // Both fields are written together: an aired video yields (duration, null),
                // which also clears any scheduled-start left over from when it was upcoming.
                tracked.DurationSeconds = info.DurationSeconds;
                tracked.ScheduledStartUtc = info.ScheduledStartUtc;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<ScrapedVideoInfo> TryScrapeAsync(string videoId, CancellationToken ct)
    {
        string html;
        try
        {
            html = await httpClient.GetStringAsync($"https://www.youtube.com/watch?v={videoId}", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Duration scrape failed for YouTube video {VideoId}", videoId);
            return default;
        }

        int? duration = null;
        var lengthMatch = LengthSecondsPattern().Match(html);
        // A premiere/live stream that hasn't aired reports lengthSeconds "0"; treat that as
        // "unknown, retry later", never a real zero-length episode.
        if (lengthMatch.Success && int.TryParse(lengthMatch.Groups["seconds"].Value, out var seconds) && seconds > 0)
        {
            duration = seconds;
        }

        DateTime? scheduledStart = null;
        // scheduledStartTime is only present while the video is unaired; once it airs the
        // field drops off and the real duration takes over.
        if (duration is null)
        {
            var startMatch = ScheduledStartPattern().Match(html);
            if (startMatch.Success && long.TryParse(startMatch.Groups["epoch"].Value, out var epoch))
            {
                scheduledStart = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
            }
        }

        return new ScrapedVideoInfo(duration, scheduledStart);
    }

    private readonly record struct ScrapedVideoInfo(int? DurationSeconds, DateTime? ScheduledStartUtc);

    [GeneratedRegex("\"lengthSeconds\"\\s*:\\s*\"(?<seconds>\\d+)\"")]
    private static partial Regex LengthSecondsPattern();

    [GeneratedRegex("\"scheduledStartTime\"\\s*:\\s*\"(?<epoch>\\d+)\"")]
    private static partial Regex ScheduledStartPattern();
}
