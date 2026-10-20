using System.IO;
using System.Text.Json;

namespace IptvPlayer;

// shortcut: одинаковые название и группа имеют общую отметку; при появлении дублей добавить идентификатор провайдера.
public sealed record FavoriteChannel(string Name, string Group);

public sealed class PlayerSettings
{
    public string? PlaylistPath { get; set; } = AppPaths.PlaylistPath;
    public string? EpgPath { get; set; } = AppPaths.EpgPath;
    public string? ChannelName { get; set; }
    public string? ChannelGroup { get; set; }
    public HashSet<FavoriteChannel> Favorites { get; set; } = [];
    public string? SelectedGroup { get; set; }
    public bool FavoritesOnly { get; set; }

    public static PlayerSettings Read(string path)
    {
        var settings = File.Exists(path)
            ? JsonSerializer.Deserialize<PlayerSettings>(File.ReadAllText(path)) ?? throw new JsonException()
            : new PlayerSettings();
        settings.Favorites ??= [];
        return settings;
    }

    public void Save(string path)
    {
        // Сначала записываем целый файл, чтобы сбой записи не испортил предыдущие настройки.
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
