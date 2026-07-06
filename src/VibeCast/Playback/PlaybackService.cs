using Microsoft.EntityFrameworkCore;
using VibeCast.AppHost;
using VibeCast.Data;
using VibeCast.Downloads;
using VibeCast.Episodes;

namespace VibeCast.Playback;

/// <summary>
/// Scoped per circuit. Holds what the persistent "now playing" bar (NowPlaying.razor,
/// hosted in MainLayout so it survives page navigation) is currently showing.
/// RSS play is download-first (CLAUDE.md): if the episode isn't downloaded yet,
/// this queues it and starts playback automatically once <see cref="DownloadProgressTracker"/>
/// reports it complete, rather than streaming straight from the enclosure URL.
/// </summary>
internal sealed class PlaybackService : IDisposable
{
    private readonly IDbContextFactory<AppDbContext> dbContextFactory;
    private readonly DownloadQueue downloadQueue;
    private readonly DownloadProgressTracker progressTracker;
    private readonly EpisodeStateService episodeStateService;
    private readonly AppConfig config;
    private int? pendingPlayEpisodeId;

    public PlaybackService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        DownloadQueue downloadQueue,
        DownloadProgressTracker progressTracker,
        EpisodeStateService episodeStateService,
        AppConfig config)
    {
        this.dbContextFactory = dbContextFactory;
        this.downloadQueue = downloadQueue;
        this.progressTracker = progressTracker;
        this.episodeStateService = episodeStateService;
        this.config = config;
        progressTracker.Changed += OnDownloadProgressChanged;
    }

    public event Action? Changed;

    public PlaybackState? Current { get; private set; }

    /// <summary>True while a Play click is waiting on a queued/in-flight download.</summary>
    public bool IsAwaitingDownload => pendingPlayEpisodeId is not null;

    /// <summary>
    /// Set when a download a Play click was waiting on failed or was canceled, so the
    /// UI can show why instead of hanging on "will play when ready". Cleared when the
    /// user dismisses it or starts another play.
    /// </summary>
    public string? PendingPlayError { get; private set; }

    /// <summary>Dismisses the <see cref="PendingPlayError"/> banner.</summary>
    public void DismissPendingPlayError()
    {
        if (PendingPlayError is not null)
        {
            PendingPlayError = null;
            Changed?.Invoke();
        }
    }

    public async Task RequestPlayRssAsync(int episodeId, CancellationToken ct)
    {
        PendingPlayError = null;

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var episode = await db.Episodes.Include(e => e.Feed).FirstOrDefaultAsync(e => e.Id == episodeId, ct);
        if (episode is null || episode.EnclosureUrl is null)
        {
            return;
        }

        if (episode.IsDownloaded && episode.DownloadedFileName is not null)
        {
            pendingPlayEpisodeId = null;
            SetCurrent(BuildRssState(episode));
            return;
        }

        pendingPlayEpisodeId = episodeId;
        Changed?.Invoke();
        var feedTitle = episode.Feed.Title ?? episode.Feed.OriginalUrl;
        downloadQueue.Enqueue(episode.Id, episode.Title, feedTitle);
    }

    public async Task PlayYouTubeAsync(int episodeId, CancellationToken ct)
    {
        PendingPlayError = null;

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var episode = await db.Episodes.Include(e => e.Feed).FirstOrDefaultAsync(e => e.Id == episodeId, ct);
        if (episode is null || episode.YouTubeVideoId is null)
        {
            return;
        }

        pendingPlayEpisodeId = null;
        var feedTitle = episode.Feed.Title ?? episode.Feed.OriginalUrl;
        SetCurrent(new PlaybackState(
            episode.Id,
            episode.Title,
            feedTitle,
            IsRss: false,
            MediaUrl: null,
            YouTubeVideoId: episode.YouTubeVideoId,
            ExternalTarget: $"https://www.youtube.com/watch?v={episode.YouTubeVideoId}",
            InitialPositionSeconds: episode.PlaybackPositionSeconds,
            IsVideo: true,
            ArtworkUrl: BuildArtworkUrl(episode.Feed)));
    }

    public Task SavePositionAsync(int episodeId, int positionSeconds, CancellationToken ct) =>
        episodeStateService.SavePlaybackPositionAsync(episodeId, positionSeconds, ct);

    /// <summary>
    /// Called when the in-app player reaches the end of the current episode. Only
    /// acts if the user opted into auto-mark-on-completion (default off) -- on
    /// *open* would delete the RSS file out from under the player, but completion
    /// is safe since playback is done. Clears the bar afterward since an RSS file
    /// marked played no longer exists to keep playing.
    /// </summary>
    public async Task HandlePlaybackEndedAsync(CancellationToken ct)
    {
        if (!config.AutoMarkOnCompletion || Current is not { } current)
        {
            return;
        }

        await episodeStateService.MarkAsPlayedAsync(current.EpisodeId, ct);
        Stop();
    }

    public void Stop()
    {
        pendingPlayEpisodeId = null;
        PendingPlayError = null;
        Current = null;
        Changed?.Invoke();
    }

    private void SetCurrent(PlaybackState state)
    {
        Current = state;
        Changed?.Invoke();
    }

    private static PlaybackState BuildRssState(Episode episode)
    {
        var feedTitle = episode.Feed.Title ?? episode.Feed.OriginalUrl;
        var filePath = Path.Combine(AppPaths.DownloadsDirectory, episode.Feed.Slug, episode.DownloadedFileName!);
        return new PlaybackState(
            episode.Id,
            episode.Title,
            feedTitle,
            IsRss: true,
            MediaUrl: $"/media/episodes/{episode.Id}",
            YouTubeVideoId: null,
            ExternalTarget: filePath,
            InitialPositionSeconds: episode.PlaybackPositionSeconds,
            IsVideo: episode.EnclosureMediaType is { } mediaType && mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase),
            ArtworkUrl: BuildArtworkUrl(episode.Feed));
    }

    private static string? BuildArtworkUrl(Feed feed) =>
        feed.ArtworkFileName is null ? null : $"/media/feeds/{feed.Id}/artwork";

    private void OnDownloadProgressChanged()
    {
        if (pendingPlayEpisodeId is not { } episodeId)
        {
            return;
        }

        var snapshot = progressTracker.Get(episodeId);
        if (snapshot is null)
        {
            return;
        }

        switch (snapshot.Status)
        {
            case DownloadStatus.Completed:
                pendingPlayEpisodeId = null;
                _ = LoadAndPlayAsync(episodeId);
                break;

            // The download the Play click was waiting on won't arrive. Clear the
            // pending state and surface why, so the "will play when ready" banner
            // doesn't hang forever (its only other exit was playing something else).
            case DownloadStatus.Failed:
            case DownloadStatus.Canceled:
                pendingPlayEpisodeId = null;
                PendingPlayError = snapshot.Status == DownloadStatus.Failed
                    ? $"Couldn't download this episode{(snapshot.ErrorMessage is { } msg ? $": {msg}" : ".")}"
                    : "Download canceled.";
                Changed?.Invoke();
                break;
        }
    }

    private async Task LoadAndPlayAsync(int episodeId)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        var episode = await db.Episodes.Include(e => e.Feed).FirstOrDefaultAsync(e => e.Id == episodeId);
        if (episode is { IsDownloaded: true, DownloadedFileName: not null })
        {
            SetCurrent(BuildRssState(episode));
        }
    }

    public void Dispose() => progressTracker.Changed -= OnDownloadProgressChanged;
}
