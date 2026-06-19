using System.Text.Json;

namespace VibeCast.AppHost;

internal sealed class AppConfig
{
    public int? PreferredPort { get; set; }

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
