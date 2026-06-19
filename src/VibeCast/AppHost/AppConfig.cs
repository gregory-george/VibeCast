using System.Text.Json;

namespace VibeCast.AppHost;

internal sealed class AppConfig
{
    public int? PreferredPort { get; set; }

    /// <summary>
    /// When the in-app player reaches the end of an episode, automatically mark it
    /// played (RSS: deletes the file too) instead of requiring a manual click.
    /// Default off. Global setting -- full settings UI lands in a later phase; this
    /// is a working toggle in the meantime (see Feeds page).
    /// </summary>
    public bool AutoMarkOnCompletion { get; set; }

    public static AppConfig Load()
    {
        if (!File.Exists(AppPaths.ConfigFile))
        {
            return new AppConfig();
        }

        try
        {
            var json = File.ReadAllText(AppPaths.ConfigFile);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch (JsonException)
        {
            return new AppConfig();
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(AppPaths.ConfigFile, json);
    }
}
