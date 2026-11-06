namespace VibeCast.Feeds;

/// <summary>
/// Process-wide signal that a feed refresh stored new episodes. <see cref="FeedRefreshService"/>
/// is scoped (one per circuit / background run), so open library pages can't subscribe to it
/// directly; this singleton bridges background refreshes (periodic timer, refresh-on-open,
/// another tab's "Refresh all") to every connected circuit.
/// </summary>
internal sealed class FeedRefreshNotifier
{
    /// <summary>
    /// Raised with the feed id after a refresh added at least one episode. Fires on the
    /// refreshing thread -- subscribers marshal to their own context (Blazor: InvokeAsync).
    /// </summary>
    public event Action<int>? EpisodesAdded;

    public void NotifyEpisodesAdded(int feedId) => EpisodesAdded?.Invoke(feedId);
}
