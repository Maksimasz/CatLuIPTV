using System.Text;
using IptvPlayer;

static void Check(bool condition, string label) { if (!condition) throw new Exception(label); }
var channels = Playlist.Parse(new[] {
    "\uFEFF#EXTM3U", "#EXTINF:-1 tvg-id=\"test\" group-title=\"Новости, мир\",Тест, HD",
    "#EXTVLCOPT:ignored=1", "https://example.invalid/live?token=synthetic",
    "#EXTINF:-1,Ошибка", "file:///private/file", "#EXTINF:-1,Второй", "udp://@239.0.0.1:1234"
});
Check(channels.Count == 2 && channels[0].Name == "Тест, HD" && channels[0].Group == "Новости, мир", "M3U");
Check(channels[0].ToString() == "Тест, HD", "Secret-safe display");
Check(Epg.TryTime("20261020120000 +0300", out var time) && time.UtcDateTime.Hour == 9, "Timezone");
Check(!Epg.TryTime("bad", out _), "Invalid date");
using var xml = new MemoryStream(Encoding.UTF8.GetBytes("""
<?xml version="1.0"?><!DOCTYPE tv SYSTEM "https://example.invalid/never-load"><tv>
<channel id="test"><display-name>Тест</display-name></channel>
<channel id="other"><display-name>Общее</display-name></channel>
<channel id="third"><display-name>Общее</display-name></channel>
<programme channel="test" start="20991020120000 +0300" stop="20991020130000 +0300"><title>Передача</title></programme>
<programme channel="test" start="bad" stop="bad"><title>Ошибка</title></programme>
</tv>
"""));
var epg = Epg.Parse(xml);
Check(epg.Match(channels[0]) == "test" && epg.Upcoming("test").Count() == 1, "EPG id and programmes");
Check(epg.Match(new Channel { Name = "Тест", Stream = channels[0].Stream }) == "test", "EPG name");
Check(epg.Match(new Channel { Name = "Общее", Stream = channels[0].Stream }) is null, "Ambiguous EPG");
Console.WriteLine("Проверки M3U, XMLTV и часовых поясов: OK");
if (args.Length == 2 && args[0] != "--update-epg")
{
    var local = Playlist.Read(args[0]);
    Console.WriteLine($"Групп в плейлисте: {local.Select(c => c.Group).Distinct(StringComparer.OrdinalIgnoreCase).Count()}; без группы: {local.Count(c => c.Group.Length == 0)}");
    var guide = Epg.Read(args[1]);
    Console.WriteLine($"Каналов: {local.Count}; совпадений EPG: {local.Count(c => guide.Match(c) is not null)}");
}

var settingsFile = Path.Combine(Path.GetTempPath(), "iptv-settings-" + Guid.NewGuid() + ".json");
try
{
    Check(PlayerSettings.Read(settingsFile).PlaylistPath is not null, "Missing settings defaults");
    var saved = new PlayerSettings { PlaylistPath = "local.m3u8", EpgPath = "local.xml.gz", ChannelName = "Тест", ChannelGroup = "Новости" };
    saved.Save(settingsFile);
    var restored = PlayerSettings.Read(settingsFile);
    Check(restored.ChannelName == saved.ChannelName && restored.ChannelGroup == saved.ChannelGroup && restored.PlaylistPath == saved.PlaylistPath && restored.EpgPath == saved.EpgPath, "Settings round trip");
    Check(!File.ReadAllText(settingsFile).Contains("example.invalid"), "No stream URL in settings");
    saved.ChannelName = "Второй";
    saved.Save(settingsFile);
    Check(PlayerSettings.Read(settingsFile).ChannelName == "Второй" && !File.Exists(settingsFile + ".tmp"), "Settings overwrite");
    File.WriteAllText(settingsFile, "{broken");
    bool rejected = false;
    try { PlayerSettings.Read(settingsFile); } catch (System.Text.Json.JsonException) { rejected = true; }
    Check(rejected, "Corrupt settings detected");
    Console.WriteLine("Проверки сохранения настроек: OK");
}
finally { File.Delete(settingsFile); }

UpdateChecks.Run();
if (args.Length == 2 && args[0] == "--update-epg")
{
    var result = await EpgUpdater.UpdateAsync(args[1], new Progress<string>(Console.WriteLine));
    Console.WriteLine($"Обновление из сети: OK; передач: {result.Count}");
}

var programmeNow = new Programme(DateTimeOffset.UtcNow.AddMinutes(-30), DateTimeOffset.UtcNow.AddMinutes(30), "Тест");
Check(programmeNow.IsCurrent && programmeNow.Duration == "1 ч" && programmeNow.Progress > 49 && programmeNow.Progress < 51, "Current programme and progress");
Check(new Programme(DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddMinutes(150), "Далее").Duration == "1 ч 30 мин", "Programme duration");
Console.WriteLine("Проверки отображения программы: OK");

var grouped = Playlist.Parse(new[] {
    "#EXTM3U", "#EXTINF:-1,Новости", "#EXTGRP: новости ", "https://example.invalid/1",
    "#EXTINF:-1 group-title=\"Спорт\",Спорт", "#EXTGRP:Другие", "https://example.invalid/2",
    "#EXTINF:-1,Без группы", "https://example.invalid/3"
});
Check(grouped.Select(c => c.Group).SequenceEqual(new[] { "новости", "Спорт", "" }), "EXTGRP, group-title priority and no group leak");
grouped[0].IsFavorite = grouped[2].IsFavorite = true;
Check(Playlist.Filter(grouped, "НОВ", "НОВОСТИ", true).SequenceEqual(new[] { grouped[0] }), "Search group favorites intersection");
Check(Playlist.Filter(grouped, "", "", false).SequenceEqual(new[] { grouped[2] }), "Ungrouped channels");
Check(Playlist.Filter(grouped, "", null, true).Count == 2 && Playlist.Filter(grouped, "", "Спорт", true).Count == 2, "Favorites span all groups even with saved group filter");
Check(grouped[0].FavoriteGlyph == "★" && grouped[1].FavoriteGlyph == "☆", "Favorite state display");
var favoritePath = Path.Combine(Path.GetTempPath(), "iptv-favorites-" + Guid.NewGuid() + ".json");
try
{
    File.WriteAllText(favoritePath, "{\"ChannelName\":\"Тест\",\"ChannelGroup\":\"\"}");
    var migrated = PlayerSettings.Read(favoritePath);
    Check(migrated.Favorites.Count == 0 && migrated.SelectedGroup is null, "Old settings migration");
    migrated.Favorites.Add(new FavoriteChannel("Тест", "новости"));
    migrated.Favorites.Add(new FavoriteChannel("Второй", "Спорт"));
    migrated.SelectedGroup = "новости";
    migrated.FavoritesOnly = true;
    migrated.Save(favoritePath);
    var reread = PlayerSettings.Read(favoritePath);
    Check(reread.Favorites.Contains(new FavoriteChannel("Тест", "новости")) && !reread.Favorites.Contains(new FavoriteChannel("Тест", "Спорт")), "Favorite identity and persistence");
    Check(reread.SelectedGroup == "новости" && reread.FavoritesOnly, "Filter persistence");
    Check(reread.Favorites.Count == 2 && reread.Favorites.Contains(new FavoriteChannel("Второй", "Спорт")), "Multiple favorites survive restart");
    reread.Favorites.Remove(new FavoriteChannel("Тест", "новости"));
    reread.Save(favoritePath);
    var remaining = PlayerSettings.Read(favoritePath);
    Check(remaining.Favorites.Count == 1 && remaining.Favorites.Contains(new FavoriteChannel("Второй", "Спорт")), "Removing one favorite preserves the other");
    File.WriteAllText(favoritePath, "{\"Favorites\":null}");
    Check(PlayerSettings.Read(favoritePath).Favorites.Count == 0, "Null favorites migration");
}
finally { File.Delete(favoritePath); }
Console.WriteLine("Проверки групп, избранного и совместимости настроек: OK");

var pathsTest = Path.Combine(Path.GetTempPath(), "iptv-paths-" + Guid.NewGuid());
try
{
    Directory.CreateDirectory(Path.Combine(pathsTest, "project", "IptvPlayer"));
    File.WriteAllText(Path.Combine(pathsTest, "project", "IptvPlayer", "IptvPlayer.csproj"), "<Project/>");
    var developmentRoot = Path.Combine(pathsTest, "project");
    Check(AppPaths.ResolveDataDirectory(Path.Combine(developmentRoot, "IPTV"), pathsTest) == developmentRoot, "Existing development profile preserved");
    Check(AppPaths.ResolveDataDirectory(Path.Combine(developmentRoot, "IptvPlayer", "bin", "Debug"), pathsTest) == developmentRoot, "Dotnet run uses project profile");
    var installedRoot = Path.Combine(pathsTest, "installed");
    Check(AppPaths.ResolveDataDirectory(installedRoot, Path.Combine(pathsTest, "user")) == Path.Combine(pathsTest, "user", "CatLu IPTV"), "Installed app uses user profile without fixed drive");
    var nestedSettings = Path.Combine(pathsTest, "new-profile", "settings.json");
    new PlayerSettings().Save(nestedSettings);
    Check(File.Exists(nestedSettings), "First run creates writable settings directory");
    Console.WriteLine("Проверки путей установленного плеера и совместимости рабочей копии: OK");
}
finally { Directory.Delete(pathsTest, recursive: true); }
