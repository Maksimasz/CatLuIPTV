using System.IO;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace IptvPlayer;

public sealed class Channel : INotifyPropertyChanged
{
    public required string Name { get; init; }
    public required Uri Stream { get; init; }
    public string TvgId { get; init; } = "";
    public string TvgName { get; init; } = "";
    public string Group { get; init; } = "";
    private bool favorite;
    public bool IsFavorite
    {
        get => favorite;
        set
        {
            if (favorite == value) return;
            favorite = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FavoriteGlyph)));
        }
    }
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public string GroupDisplay => Group.Length == 0 ? "Без группы" : Group;
    public event PropertyChangedEventHandler? PropertyChanged;
    // Не включаем адрес с возможными токенами в диагностическое представление.
    public override string ToString() => Name;
}

public static class Playlist
{
    public static List<Channel> Filter(IEnumerable<Channel> channels, string query, string? group, bool favoritesOnly) =>
        channels.Where(c => (favoritesOnly || group is null || string.Equals(c.Group, group, StringComparison.OrdinalIgnoreCase)) &&
            (!favoritesOnly || c.IsFavorite) &&
            (c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || c.Group.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();

    public static List<Channel> Read(string path) => Parse(File.ReadLines(path));

    public static List<Channel> Parse(IEnumerable<string> lines)
    {
        var result = new List<Channel>();
        string? info = null;
        string group = "";
        foreach (var raw in lines)
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase)) { info = line; group = ""; continue; }
            if (info is not null && line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
            {
                group = line[8..].Trim();
                continue;
            }
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (info is null) continue;
            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https" or "udp" or "rtp" or "rtsp")
            {
                // Запятая внутри кавычек атрибута не отделяет название.
                bool quoted = false;
                int comma = -1;
                for (int i = 0; i < info.Length; i++)
                {
                    if (info[i] == '"') quoted = !quoted;
                    if (info[i] == ',' && !quoted) { comma = i; break; }
                }
                string Attr(string name) => Regex.Match(info[..(comma < 0 ? info.Length : comma)],
                    "(?:^|\\s)" + name + "=\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value;
                var name = comma >= 0 ? info[(comma + 1)..].Trim() : Attr("tvg-name");
                result.Add(new Channel { Name = string.IsNullOrWhiteSpace(name) ? "Канал без названия" : name,
                    Stream = uri, TvgId = Attr("tvg-id"), TvgName = Attr("tvg-name"), Group = string.IsNullOrWhiteSpace(Attr("group-title")) ? group : Attr("group-title").Trim() });
            }
            info = null;
        }
        return result;
    }
}
