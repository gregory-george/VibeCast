namespace VibeCast.Downloads;

internal sealed record DownloadProgressSnapshot(
    int EpisodeId,
    string EpisodeTitle,
    string FeedTitle,
    DownloadStatus Status,
    long BytesDownloaded,
    long? TotalBytes,
    string? ErrorMessage)
{
    public string FormatProgress()
    {
        if (Status != DownloadStatus.Downloading)
        {
            return string.Empty;
        }

        var downloadedMb = BytesDownloaded / 1024.0 / 1024.0;
        if (TotalBytes is { } total && total > 0)
        {
            var percent = (int)(BytesDownloaded * 100 / total);
            var totalMb = total / 1024.0 / 1024.0;
            return $"{percent}% ({downloadedMb:F1} / {totalMb:F1} MB)";
        }

        return $"{downloadedMb:F1} MB";
    }
}
