namespace VibeCast.Feeds;

internal sealed record AddFeedResult(bool Success, int? FeedId, int EpisodeCount, string? Error)
{
    public static AddFeedResult Ok(int feedId, int episodeCount) => new(true, feedId, episodeCount, null);

    public static AddFeedResult Invalid(string error) => new(false, null, 0, error);
}
