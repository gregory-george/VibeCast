using Microsoft.Extensions.Logging;
using VibeCast.AppHost;

namespace VibeCast.Downloads;

/// <summary>
/// Resolves and safely removes downloaded episode files. Deletion is the dangerous
/// part: the in-app player holds the file open through the loopback media endpoint,
/// so a mark-as-played (or retention sweep) firing mid-playback used to throw and
/// crash the caller. <see cref="TryDelete"/> never throws on a locked/in-use file --
/// it reports failure so the caller can leave the DB record pointing at the file and
/// let a later sweep (refresh/shutdown) retry the delete once the lock is gone.
/// </summary>
internal static class DownloadFileStore
{
    public static string PathFor(string feedSlug, string fileName) =>
        Path.Combine(AppPaths.DownloadsDirectory, feedSlug, fileName);

    /// <summary>
    /// Attempts to delete the file. Returns true when the file is gone afterwards
    /// (deleted now, or already absent); false when it still exists because it is
    /// locked/in use -- the caller should defer and retry later. Never throws for the
    /// expected lock/permission cases.
    /// </summary>
    public static bool TryDelete(string filePath, ILogger logger)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return true;
            }

            File.Delete(filePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Almost always "file in use" by the player. Expected and recoverable --
            // a later refresh/shutdown sweep retries once the file is released.
            logger.LogInformation(ex, "Deferred delete of locked download file {FilePath}", filePath);
            return false;
        }
    }
}
