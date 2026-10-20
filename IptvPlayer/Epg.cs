using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace IptvPlayer;

public sealed record Programme(DateTimeOffset Start, DateTimeOffset Stop, string Title)
{
    public bool IsCurrent => Start <= DateTimeOffset.UtcNow && Stop > DateTimeOffset.UtcNow;
    public string TimeRange => $"{Start.ToLocalTime():dd.MM} / {Start.ToLocalTime():HH:mm} — {Stop.ToLocalTime():HH:mm}";
    public string Duration
    {
        get
        {
            var minutes = (int)(Stop - Start).TotalMinutes;
            return minutes < 60 ? $"{minutes} мин" : minutes % 60 == 0 ? $"{minutes / 60} ч" : $"{minutes / 60} ч {minutes % 60} мин";
        }
    }
    public double Progress => Stop <= Start ? 0 : Math.Clamp((DateTimeOffset.UtcNow - Start).TotalSeconds / (Stop - Start).TotalSeconds * 100, 0, 100);
    public string Display => $"{Start.ToLocalTime():dd.MM HH:mm}–{Stop.ToLocalTime():HH:mm}  {Title}";
}

public sealed class Epg
{
    private readonly Dictionary<string, List<Programme>> programmes = new(StringComparer.Ordinal);
    private readonly HashSet<string> ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> names = new(StringComparer.OrdinalIgnoreCase);

    public static Epg Read(string path)
    {
        using var file = File.OpenRead(path);
        using Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress) : file;
        return Parse(stream);
    }

    public static Epg Parse(Stream stream)
    {
        var epg = new Epg();
        // XMLTV может объявлять DTD; внешние ресурсы никогда не загружаем.
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name is not ("channel" or "programme")) continue;
            using var subtree = reader.ReadSubtree();
            var element = XElement.Load(subtree);
            if (element.Name == "channel")
            {
                var id = (string?)element.Attribute("id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                epg.ids.Add(id);
                foreach (var name in element.Elements("display-name"))
                {
                    var key = name.Value.Trim();
                    if (key.Length == 0) continue;
                    if (!epg.names.TryGetValue(key, out var matches)) epg.names[key] = matches = [];
                    matches.Add(id);
                }
            }
            else
            {
                var id = (string?)element.Attribute("channel");
                if (string.IsNullOrWhiteSpace(id) || !TryTime((string?)element.Attribute("start"), out var start) ||
                    !TryTime((string?)element.Attribute("stop"), out var stop) || stop <= start) continue;
                epg.ids.Add(id);
                if (!epg.programmes.TryGetValue(id, out var list)) epg.programmes[id] = list = [];
                list.Add(new Programme(start, stop, (string?)element.Element("title") ?? "Без названия"));
            }
        }
        foreach (var list in epg.programmes.Values) list.Sort((a, b) => a.Start.CompareTo(b.Start));
        return epg;
    }

    public string? Match(Channel channel)
    {
        if (channel.TvgId.Length > 0 && ids.Contains(channel.TvgId)) return channel.TvgId;
        foreach (var name in new[] { channel.TvgName, channel.Name })
            if (names.TryGetValue(name.Trim(), out var matches) && matches.Count == 1) return matches.First();
        return null;
    }

    public IEnumerable<Programme> Upcoming(string id) => programmes.TryGetValue(id, out var list)
        ? list.Where(p => p.Stop > DateTimeOffset.UtcNow).Take(30) : [];

    public static bool TryTime(string? value, out DateTimeOffset time)
    {
        time = default;
        if (value is null) return false;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2 || parts[0].Length != 14) return false;
        var offset = parts.Length == 1 ? "+0000" : parts[1];
        if (offset.Length != 5 || offset[0] is not ('+' or '-')) return false;
        return DateTimeOffset.TryParseExact(parts[0] + " " + offset.Insert(3, ":"),
            "yyyyMMddHHmmss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }
}
