using VibeCast.Logging;
using Xunit;

namespace VibeCast.Tests;

public class LogRetentionTests
{
    [Fact]
    public void PruneOldLogs_DeletesOnlyLogsOlderThanWindow()
    {
        var dir = NewTempDir();
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            string LogName(DateOnly d) => $"vibecast-{d:yyyyMMdd}.log";

            var old40 = LogName(today.AddDays(-40));
            var old31 = LogName(today.AddDays(-31));
            var exactly30 = LogName(today.AddDays(-30)); // 30 days old is NOT "older than 30"
            var recent = LogName(today.AddDays(-29));
            var todayLog = LogName(today);
            var foreign = "notes.txt";
            var unparseable = "vibecast-notadate.log";

            foreach (var name in new[] { old40, old31, exactly30, recent, todayLog, foreign, unparseable })
            {
                File.WriteAllText(Path.Combine(dir, name), "x");
            }

            LogRetention.PruneOldLogs(dir, maxAgeDays: 30);

            Assert.False(File.Exists(Path.Combine(dir, old40)));
            Assert.False(File.Exists(Path.Combine(dir, old31)));
            Assert.True(File.Exists(Path.Combine(dir, exactly30)));
            Assert.True(File.Exists(Path.Combine(dir, recent)));
            Assert.True(File.Exists(Path.Combine(dir, todayLog)));

            // Non-log and unparsable files are never touched.
            Assert.True(File.Exists(Path.Combine(dir, foreign)));
            Assert.True(File.Exists(Path.Combine(dir, unparseable)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PruneOldLogs_MissingDirectory_DoesNotThrow()
    {
        var missing = Path.Combine(Path.GetTempPath(), "vibecast-absent-" + Guid.NewGuid().ToString("N"));
        LogRetention.PruneOldLogs(missing); // no exception
        Assert.False(Directory.Exists(missing));
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vibecast-logtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
