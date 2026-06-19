namespace VibeCast.Feeds;

internal sealed record FeedRefreshResult(bool Success, int AddedCount, string? Error)
{
    public static FeedRefreshResult Ok(int addedCount) => new(true, addedCount, null);

    public static FeedRefreshResult Failed(string error) => new(false, 0, error);
}
