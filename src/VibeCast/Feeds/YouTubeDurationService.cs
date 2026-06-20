using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeCast.Data;

namespace VibeCast.Feeds;

/// <summary>
/// Best-effort fetch of a YouTube video's duration. videos.xml carries no duration
/// field, so each new YouTube episode's watch page is scraped individually for the
/// "lengthSeconds" field embedded in its player-response JSON. Never throws -- a
/// failed or missing duration fetch must not block a refresh; the episode simply
/// keeps a null DurationSeconds, same as today.
/// </summary>
internal sealed partial class YouTubeDurationService(
    HttpClient httpClient,
    IDbContextFactory<AppDbContext> dbContextFactory,
    ILogger<YouTubeDurationService> logger)
{
    public async Task BackfillAsync(IReadOnlyList<Episode> episodes, CancellationToken ct)
    {
        var targets = episodes.Where(e => e.YouTubeVideoId is not null).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var changed = false;
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        foreach (var episode in targets)
        {
            var seconds = await TryGetDurationSecondsAsync(episode.YouTubeVideoId!, ct);
            if (seconds is null)
            {
                continue;
            }

            // Re-fetch via the context tracking this episode's row rather than
            // mutating the detached instance passed in from the refresh pass.
            var tracked = await db.Episodes.FindAsync([episode.Id], ct);
            if (tracked is null)
            {
                continue;
            }

            tracked.DurationSeconds = seconds;
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<int?> TryGetDurationSecondsAsync(string videoId, CancellationToken ct)
    {
        string html;
        try
        {
            html = await httpClient.GetStringAsync($"https://www.youtube.com/watch?v={videoId}", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Duration scrape failed for YouTube video {VideoId}", videoId);
            return null;
        }

        var match = LengthSecondsPattern().Match(html);
        return match.Success && int.TryParse(match.Groups["seconds"].Value, out var seconds) ? seconds : null;
    }

    [GeneratedRegex("\"lengthSeconds\"\\s*:\\s*\"(?<seconds>\\d+)\"")]
    private static partial Regex LengthSecondsPattern();
}
