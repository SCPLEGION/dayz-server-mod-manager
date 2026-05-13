using System.Text.Json;
using System.IO;

namespace DayZModManager;

internal sealed class AppConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public sealed class Config
    {
        public string? ModsRootPath { get; set; }
        public string? LocalModsTxtPath { get; set; }
        public string? CombineOutFileText { get; set; }
        public int? MergeModeSelectedIndex { get; set; }
    }

    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static Config Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return new Config();

            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<Config>(json, JsonOptions) ?? new Config();
        }
        catch
        {
            return new Config();
        }
    }

    public static void Save(Config config)
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
    }
}
