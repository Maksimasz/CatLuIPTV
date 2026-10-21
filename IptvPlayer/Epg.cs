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
                if (IsPlaceholderTitle((string?)element.Element("title"))) continue;
                if (!epg.programmes.TryGetValue(id, out var list)) epg.programmes[id] = list = [];
                list.Add(new Programme(start, stop, (string?)element.Element("title") ?? "Без названия"));
            }
        }
        foreach (var list in epg.programmes.Values) list.Sort((a, b) => a.Start.CompareTo(b.Start));
        return epg;
    }

    private readonly Dictionary<(string Name, string Group), string> israelMatches = [];

    public void AddIsrael(Epg guide, IEnumerable<Channel> channels)
    {
        israelMatches.Clear();
        foreach (var channel in channels)
        {
            var id = IsraelChannelId(channel);
            if (id is null || !guide.programmes.TryGetValue(id, out var schedule)) continue;
            var key = "israel:" + id;
            programmes[key] = schedule;
            ids.Add(key);
            israelMatches[(channel.Name, channel.Group)] = key;
        }
    }

    public static string? IsraelChannelId(Channel channel)
    {
        if (channel.Group != "ישראלי" && !channel.Name.EndsWith(" IL", StringComparison.OrdinalIgnoreCase)) return null;
        var name = System.Text.RegularExpressions.Regex.Replace(channel.Name,
            @"\s+(?:(?:FHD|UHD|HD|SD|4K)\s+)?IL$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (israelAliases.TryGetValue(name, out var id)) return id;
        return name.Replace(" ", "") + ".il";
    }

    private static readonly Dictionary<string, string> israelAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Keshet 12"] = "קשת.il", ["Reshet 13"] = "רשת.il",
        ["Channel 9"] = "ישראלפלוס.il", ["Channel 11"] = "כאן11.il",
        ["Channel 14"] = "ערוץ14.il", ["Channel 24"] = "ערוץ24החדש.il",
        ["Channel 98"] = "ערוץ98.il", ["Kan Educational"] = "ערוץ23חינוכית.il",
        ["Makan 33"] = "מכאן.il", ["Knesset Channel"] = "ערוץהכנסת.il",
        ["Hop!"] = "הופ!.il", ["Baby TV"] = "בייבי.il", ["Luli"] = "HOTLuli.il",
        ["Disney Channel"] = "דיסניHD.il", ["WIZ"] = "ערוץWiZ.il",
        ["Hidabrut"] = "ערוץהידברות.il", ["Home Plus"] = "בית+.il",
        ["Good Life"] = "החייםהטובים.il", ["Health"] = "ערוץבריאות.il",
        ["Food Network"] = "ערוץהאוכל.il", ["Yam Tichoni"] = "יםתיכוני.il",
        ["Yam Tichoni+"] = "הערוץהיםתיכוני+.il", ["Viva Telenovelas"] = "ויוה.il",
        ["Turkish Dramas Plus"] = "ערוץהדרמותהטורקיות+.il",
        ["Turkish Dramas 2"] = "ערוץהדרמותהטורקיות2.il",
        ["Turkish Dramas 3"] = "ערוץהדרמותהטורקיות3.il",
        ["History"] = "היסטוריהHD.il", ["Discovery Channel"] = "דיסקבריHD.il",
        ["National Geographic Wild"] = "NGWILD.il", ["One"] = "ONE.il", ["One 2"] = "ONE2.il",
        ["5Sport"] = "5sportHD.il", ["5Sport Live"] = "5LIVEHD.il",
        ["5Sport Gold"] = "5GOLD.il", ["5Sport Stars"] = "5STARS.il",
        ["5Sport Plus"] = "5PLUSHD.il", ["Sport 1"] = "ספורט1HD.il",
        ["Sport 2"] = "ספורט2HD.il", ["Sport 3"] = "ספורט3HD.il", ["Sport 4"] = "ספורט4HD.il",
        ["Yes Israeli"] = "yesקולנועישראלי.il", ["Yes Docu"] = "yesדוקו.il",
        ["Yes TV Drama"] = "yesTVDRAMA.il", ["Yes TV Comedy"] = "yesTVCOMEDY.il",
        ["Yes TV Action"] = "yesTVACTION.il", ["Yes Movies Drama"] = "yesMOVIESDRAMA.il",
        ["Yes Movies Comedy"] = "yesMOVIESCOMEDY.il", ["Yes Movies Action"] = "yesMOVIESACTION.il",
        ["Yes Movies Kids"] = "yesMOVIESKIDS.il", ["HOT Comedy"] = "HOTComedyCentral.il",
        ["HOT Bidur"] = "HOTEntertainment.il", ["Style Channel"] = "ערוץהסטייל.il",
        ["Design Channel"] = "ערוץהעיצוב.il", ["Humor Channel"] = "ערוץההומור.il",
        ["Reality Channel"] = "ערוץהריאליטי.il", ["Game Show Channel"] = "ערוץהשעשועונים.il",
        ["Vacation Channel"] = "ערוץהנופש.il", ["Ego Channel"] = "ערוץאגו.il",
        ["TeenNick"] = "Teennick.il", ["Zee TV"] = "ZEETV.il"
    };

    private readonly Dictionary<(string Name, string Group), string> regionalMatches = [];
    private readonly Dictionary<(string Name, string Group), string> iptvxMatches = [];
    private readonly Dictionary<(string Name, string Group), string> tvteamMatches = [];

    public void AddRegional(Epg guide, IEnumerable<Channel> channels, string source = "regional")
    {
        var target = source switch { "iptvx" => iptvxMatches, "tvteam" => tvteamMatches, _ => regionalMatches };
        target.Clear();
        var exact = new Dictionary<(string Region, string Name), HashSet<string>>();
        var relaxed = new Dictionary<(string Region, string Name), HashSet<string>>();
        var russian = new Dictionary<(string Region, string Name), HashSet<string>>();
        var regions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, matches) in guide.names)
        {
            var prefix = System.Text.RegularExpressions.Regex.Match(name, @"^([A-Z]{2})\s*[-:]\s*(.+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string region, label;
            if (prefix.Success)
            {
                region = prefix.Groups[1].Value.ToUpperInvariant();
                label = prefix.Groups[2].Value;
            }
            else
            {
                var suffix = System.Text.RegularExpressions.Regex.Match(name, @"^(.*?)\s*\[([A-Z]{2})\]\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!suffix.Success) continue;
                label = suffix.Groups[1].Value;
                region = suffix.Groups[2].Value.ToUpperInvariant();
            }
            if (region is "HD" or "SD") continue;
            regions.Add(region);
            void Add(Dictionary<(string, string), HashSet<string>> index, string key)
            {
                if (!index.TryGetValue((region, key), out var values)) index[(region, key)] = values = [];
                values.UnionWith(matches);
            }
            Add(exact, RegionalName(label, false));
            Add(relaxed, RegionalName(label, true));
            if (region == "IL" && label.EndsWith(" (ru)", StringComparison.OrdinalIgnoreCase))
                Add(russian, RegionalName(label[..^5], true));
        }
        foreach (var channel in channels)
        {
            var name = channel.Name;
            regionalGroups.TryGetValue(channel.Group, out var region);
            var suffix = System.Text.RegularExpressions.Regex.Match(name, @"\s+([A-Z]{2})$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (suffix.Success && regions.Contains(suffix.Groups[1].Value))
            {
                region = suffix.Groups[1].Value.ToUpperInvariant();
                name = name[..suffix.Index];
            }
            if (region is null && System.Text.RegularExpressions.Regex.IsMatch(name, @"[А-Яа-яЁё]")) region = "RU";
            string? id = source == "regional" ? null : guide.Match(channel);
            if (region == "IL")
            {
                var baseName = System.Text.RegularExpressions.Regex.Replace(name, @"\s+(?:FHD|UHD|HD|SD|4K)$", "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var number = baseName.ToLowerInvariant() switch
                {
                    "channel 9" => "9", "channel 11" => "11", "keshet 12" => "12", "reshet 13" => "13", _ => null
                };
                if (number is not null)
                {
                    var alias = source == "iptvx" && number == "9" ? "9 канал" : "Channel " + number;
                    exact.TryGetValue((region, RegionalName(alias, false)), out var aliases);
                    if (aliases?.Count != 1) relaxed.TryGetValue((region, RegionalName(alias, true)), out aliases);
                    if (aliases?.Count == 1) id = aliases.Single();
                }
            }
            if (id is null && region is not null)
            {
                exact.TryGetValue((region, RegionalName(name, false)), out var matches);
                if (matches?.Count != 1) relaxed.TryGetValue((region, RegionalName(name, true)), out matches);
                if (matches?.Count == 1) id = matches.Single();
            }
            if (region == "IL" && russian.TryGetValue((region, RegionalName(name, true)), out var variants) &&
                variants.Count == 1 && guide.HasRussianSchedule(variants.Single())) id = variants.Single();
            if (id is null || !guide.programmes.TryGetValue(id, out var schedule)) continue;
            var key = source + ":" + id;
            programmes[key] = schedule;
            ids.Add(key);
            target[(channel.Name, channel.Group)] = key;
        }
    }

    private static string RegionalName(string name, bool removeQuality)
    {
        if (removeQuality) name = System.Text.RegularExpressions.Regex.Replace(name, @"\b(?:FHD|UHD|HD|SD|4K|HEVC)\b", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\p{Nd}+]+", "");
    }

    private static readonly Dictionary<string, string> regionalGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USA"] = "US", ["türk"] = "TR", ["moldovenească"] = "MD", ["українські"] = "UA",
        ["ישראלי"] = "IL", ["o'zbek"] = "UZ", ["azərbaycan"] = "AZ", ["Հայկական"] = "AM",
        ["ქართული"] = "GE", ["қазақстан"] = "KZ", ["точик"] = "TJ"
    };

    private readonly Dictionary<(string Name, string Group), string> bcuMatches = [];

    public void AddBcu(Epg guide, IEnumerable<Channel> channels)
    {
        bcuMatches.Clear();
        foreach (var channel in channels)
        {
            var id = guide.Match(channel);
            if (id is null || !guide.programmes.TryGetValue(id, out var schedule)) continue;
            var key = "bcu:" + id;
            programmes[key] = schedule;
            ids.Add(key);
            bcuMatches[(channel.Name, channel.Group)] = key;
        }
    }

    public string? Match(Channel channel)
    {
        if (IsraelChannelId(channel) is not null)
            foreach (var source in new[] { iptvxMatches, tvteamMatches })
                if (source.TryGetValue((channel.Name, channel.Group), out var russianId) && HasRussianSchedule(russianId)) return russianId;
        if (israelMatches.TryGetValue((channel.Name, channel.Group), out var israelId) && Upcoming(israelId).Any()) return israelId;
        string? primary = null;
        if (channel.TvgId.Length > 0 && ids.Contains(channel.TvgId)) primary = channel.TvgId;
        else
            foreach (var name in new[] { channel.TvgName, channel.Name })
                if (names.TryGetValue(name.Trim(), out var matches) && matches.Count == 1) { primary = matches.First(); break; }
        if (primary is not null && Upcoming(primary).Any()) return primary;
        if (regionalMatches.TryGetValue((channel.Name, channel.Group), out var regionalId) && Upcoming(regionalId).Any()) return regionalId;
        if (bcuMatches.TryGetValue((channel.Name, channel.Group), out var bcuId) && Upcoming(bcuId).Any()) return bcuId;
        if (iptvxMatches.TryGetValue((channel.Name, channel.Group), out var iptvxId) && Upcoming(iptvxId).Any()) return iptvxId;
        if (tvteamMatches.TryGetValue((channel.Name, channel.Group), out var tvteamId) && Upcoming(tvteamId).Any()) return tvteamId;
        return primary;
    }

    public bool HasRussianSchedule(string id)
    {
        var upcoming = Upcoming(id).ToList();
        return upcoming.Any(p => System.Text.RegularExpressions.Regex.IsMatch(p.Title, @"[А-Яа-яЁё]") && p.Title != "Без названия") &&
            upcoming.All(p => !System.Text.RegularExpressions.Regex.IsMatch(p.Title, @"[֐-׿ієїґІЄЇҐ]"));
    }

    public IEnumerable<Programme> Upcoming(string id) => programmes.TryGetValue(id, out var list)
        ? list.Where(p => p.Stop > DateTimeOffset.UtcNow).Take(30) : [];

    public static bool IsPlaceholderTitle(string? title) =>
        string.Equals(title?.Trim(), "No Data", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(title?.Trim(), "Программа не найдена", StringComparison.OrdinalIgnoreCase);

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
