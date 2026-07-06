using VibeCast.Downloads;
using Xunit;

namespace VibeCast.Tests;

public class DownloadProgressSnapshotTests
{
    private static DownloadProgressSnapshot Snapshot(DownloadStatus status, long bytes = 0, long? total = null) =>
        new(EpisodeId: 1, EpisodeTitle: "Ep", FeedTitle: "Feed", status, bytes, total, ErrorMessage: null);

    // DownloadStatus is internal, so it can't be a public [Theory] parameter -- each
    // non-downloading status gets its own fact instead.
    [Fact]
    public void FormatProgress_Queued_IsEmpty() =>
        Assert.Equal(string.Empty, Snapshot(DownloadStatus.Queued).FormatProgress());

    [Fact]
    public void FormatProgress_Completed_IsEmpty() =>
        Assert.Equal(string.Empty, Snapshot(DownloadStatus.Completed).FormatProgress());

    [Fact]
    public void FormatProgress_Failed_IsEmpty() =>
        Assert.Equal(string.Empty, Snapshot(DownloadStatus.Failed).FormatProgress());

    [Fact]
    public void FormatProgress_Canceled_IsEmpty() =>
        Assert.Equal(string.Empty, Snapshot(DownloadStatus.Canceled).FormatProgress());

    [Fact]
    public void FormatProgress_WithKnownTotal_ShowsPercent()
    {
        var result = Snapshot(DownloadStatus.Downloading, bytes: 5 * 1024 * 1024, total: 10 * 1024 * 1024).FormatProgress();
        // Percent is culture-independent; the MB figures use current-culture formatting.
        Assert.StartsWith("50%", result);
        Assert.Contains("MB", result);
    }

    [Fact]
    public void FormatProgress_WithUnknownTotal_ShowsBytesOnly_NoPercent()
    {
        var result = Snapshot(DownloadStatus.Downloading, bytes: 3 * 1024 * 1024, total: null).FormatProgress();
        Assert.DoesNotContain("%", result);
        Assert.EndsWith("MB", result);
    }
}
