using System.Text;
using IptvPlayer;

var archiveNow = DateTimeOffset.Parse("2026-10-21T12:00:00Z");
var recording = new Programme(archiveNow.AddHours(-2), archiveNow.AddHours(-1), "Replay");
if (recording.SeekPosition(10, -30, archiveNow) != 0 || recording.SeekPosition(10, 30, archiveNow) != 40 ||
    recording.SeekPosition(3590, 30, archiveNow) != 3599) throw new Exception("Archive seek boundaries");
var ongoing = recording with { Stop = archiveNow.AddHours(1) };
if (ongoing.SeekPosition(7190, 30, archiveNow) != 7199) throw new Exception("Archive seek cannot pass the live edge");
var archived = Playlist.Parse(new[] { "#EXTM3U", "#EXTINF:-1 tvg-rec=\"3\",Replay", "https://example.invalid/live?token=synthetic&utc=1&lutc=2" }).Single();
if (archived.ArchiveDays != 3 || !archived.CanReplay(recording, archiveNow)) throw new Exception("Archive metadata");
var replayQuery = archived.ArchiveStream(recording, archiveNow).Query;
if (!archived.ArchiveStream(recording with { Start = recording.Start.AddSeconds(30) }, archiveNow).Query.Contains($"utc={recording.Start.AddSeconds(30).ToUnixTimeSeconds()}"))
    throw new Exception("Archive seek uses shifted provider timestamp");
if (!replayQuery.Contains("token=synthetic") || !replayQuery.Contains($"utc={recording.Start.ToUnixTimeSeconds()}") || !replayQuery.Contains($"lutc={archiveNow.ToUnixTimeSeconds()}") || replayQuery.Split('&').Length != 3) throw new Exception("Archive URL");
if (archived.CanReplay(recording with { Start = archiveNow.AddDays(-4) }, archiveNow) || archived.CanReplay(recording with { Start = archiveNow.AddHours(1) }, archiveNow)) throw new Exception("Archive limits");
using (var archiveXml = new MemoryStream(Encoding.UTF8.GetBytes($"<tv><programme channel='replay' start='{recording.Start:yyyyMMddHHmmss} +0000' stop='{recording.Stop:yyyyMMddHHmmss} +0000'><title>Replay</title></programme></tv>")))
{
    var archiveGuide = Epg.Parse(archiveXml);
    if (archiveGuide.Archive("replay", archived, recording.Start.ToLocalTime().Date, archiveNow).Count() != 1 || archiveGuide.Archive("replay", archived, recording.Start.ToLocalTime().Date.AddDays(1), archiveNow).Any()) throw new Exception("Archive schedule");
}

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
    var installedRoot = Path.Combine(Path.GetPathRoot(pathsTest)!, "iptv-installed-check-" + Guid.NewGuid());
    Check(AppPaths.ResolveDataDirectory(installedRoot, Path.Combine(pathsTest, "user")) == Path.Combine(pathsTest, "user", "CatLu IPTV"), "Installed app uses user profile without fixed drive");
    var nestedSettings = Path.Combine(pathsTest, "new-profile", "settings.json");
    new PlayerSettings().Save(nestedSettings);
    Check(File.Exists(nestedSettings), "First run creates writable settings directory");
    Console.WriteLine("Проверки путей установленного плеера и совместимости рабочей копии: OK");
}
finally { Directory.Delete(pathsTest, recursive: true); }

// Israel data must stay separate from primary IDs and non-Israeli broadcasts.
var israelChannel = new Channel { Name = "Channel 9 FHD IL", Group = "ישראלי", Stream = new Uri("https://example.invalid/israel") };
Check(Epg.IsraelChannelId(israelChannel) == "ישראלפלוס.il", "Israel channel 9 alias");
Check(Epg.IsraelChannelId(new Channel { Name = "Keshet 12 HD IL", Stream = israelChannel.Stream }) == "קשת.il", "Keshet alias and HD suffix");
Check(Epg.IsraelChannelId(new Channel { Name = "National Geographic", Stream = israelChannel.Stream }) is null, "Other regions do not use Israel schedule");
using var israelXml = new MemoryStream(Encoding.UTF8.GetBytes("""
<tv><channel id="ישראלפלוס.il"><display-name>IL - ישראל פלוס</display-name></channel>
<programme channel="ישראלפלוס.il" start="20991020120000 +0300" stop="20991020130000 +0300"><title>Israel programme</title></programme></tv>
"""));
var israelGuide = Epg.Parse(israelXml);
epg.AddIsrael(israelGuide, new[] { israelChannel });
var israelId = epg.Match(israelChannel);
Check(israelId == "israel:ישראלפלוס.il" && epg.Upcoming(israelId).Single().Title == "Israel programme", "Israel alias displays schedule");
Check(epg.Upcoming("test").Single().Title != "Israel programme", "Primary schedule survives merge");
using var emptyIsrael = new MemoryStream(Encoding.UTF8.GetBytes("<tv/>"));
epg.AddIsrael(Epg.Parse(emptyIsrael), new[] { israelChannel });
Check(epg.Match(israelChannel) is null, "Missing Israel mapping does not retain old match");
using var primaryIsraelXml = new MemoryStream(Encoding.UTF8.GetBytes("""
<tv><channel id="primary9"><display-name>Channel 9 FHD IL</display-name></channel>
<programme channel="primary9" start="20991020120000 +0300" stop="20991020130000 +0300"><title>Primary fallback</title></programme></tv>
"""));
var primaryIsrael = Epg.Parse(primaryIsraelXml);
primaryIsrael.AddIsrael(israelGuide, new[] { israelChannel });
Check(primaryIsrael.Upcoming(primaryIsrael.Match(israelChannel)!).Single().Title == "Israel programme", "Israel schedule takes priority");
using var expiredIsraelXml = new MemoryStream(Encoding.UTF8.GetBytes("""
<tv><channel id="ישראלפלוס.il"/><programme channel="ישראלפלוס.il" start="20001020120000 +0300" stop="20001020130000 +0300"><title>Expired</title></programme></tv>
"""));
primaryIsrael.AddIsrael(Epg.Parse(expiredIsraelXml), new[] { israelChannel });
Check(primaryIsrael.Match(israelChannel) == "primary9", "Expired Israel schedule falls back to primary");
Console.WriteLine("Israel merge, aliases and regional isolation: OK");
if (args.Length == 3 && args[0] == "--israel")
{
    var localChannels = Playlist.Read(args[1]);
    var installed = EpgUpdater.InstallArchive(args[2], EpgUpdater.IsraelPath, DateTimeOffset.UtcNow);
    var combined = Epg.Read(AppPaths.EpgPath);
    combined.AddIsrael(installed.Guide, localChannels);
    var israelChannels = localChannels.Where(c => c.Group == "ישראלי").ToList();
    Console.WriteLine($"Israel coverage: {israelChannels.Count(c => combined.Match(c) is string id && id.StartsWith("israel:") && combined.Upcoming(id).Any())}/{israelChannels.Count}");
    foreach (var name in new[] { "Channel 9 FHD IL", "Channel 11 HD IL", "Keshet 12 HD IL", "Reshet 13 HD IL" })
    {
        var channel = israelChannels.Single(c => c.Name == name);
        var id = combined.Match(channel);
        Check(id is not null && combined.Upcoming(id).Any(), "Real Israel schedule: " + name);
        Console.WriteLine(name + ": " + combined.Upcoming(id!).First().Title);
    }
}

RegionalChecks.Run();
if (args.Length == 3 && args[0] == "--regional")
{
    var localChannels = Playlist.Read(args[1]);
    var installed = EpgUpdater.InstallArchive(args[2], EpgUpdater.RegionalPath, DateTimeOffset.UtcNow);
    var combined = Epg.Read(AppPaths.EpgPath);
    combined.AddIsrael(Epg.Read(EpgUpdater.IsraelPath), localChannels);
    var before = localChannels.Count(c => combined.Match(c) is string id && combined.Upcoming(id).Any());
    combined.AddRegional(installed.Guide, localChannels);
    var after = localChannels.Count(c => combined.Match(c) is string id && combined.Upcoming(id).Any());
    Console.WriteLine($"Actual playlist coverage: {before} -> {after} / {localChannels.Count}; added: {after - before}");
    Check(after > before, "Regional source improves actual playlist coverage");
    foreach (var channel in localChannels.Where(c => combined.Match(c)?.StartsWith("regional:") == true).Take(15))
        Console.WriteLine(channel.Name + ": " + combined.Upcoming(combined.Match(channel)!).First().Title);
}

BcuChecks.Run();
if (args.Length == 3 && args[0] == "--bcu")
{
    var localChannels = Playlist.Read(args[1]);
    var installed = EpgUpdater.InstallArchive(args[2], EpgUpdater.BcuPath, DateTimeOffset.UtcNow);
    var combined = Epg.Read(AppPaths.EpgPath);
    combined.AddIsrael(Epg.Read(EpgUpdater.IsraelPath), localChannels);
    combined.AddRegional(Epg.Read(EpgUpdater.RegionalPath), localChannels);
    var before = localChannels.Count(c => combined.Match(c) is string id && combined.Upcoming(id).Any());
    combined.AddBcu(installed.Guide, localChannels);
    var after = localChannels.Count(c => combined.Match(c) is string id && combined.Upcoming(id).Any());
    Console.WriteLine($"Actual BCU coverage: {before} -> {after} / {localChannels.Count}; added: {after - before}");
    Check(after > before, "BCU improves actual playlist coverage");
    foreach (var channel in localChannels.Where(c => combined.Match(c)?.StartsWith("bcu:") == true))
        Console.WriteLine(channel.Name + ": " + combined.Upcoming(combined.Match(channel)!).First().Title);
}

ExtraChecks.Run();
if (args.Length == 3 && args[0] == "--extras")
{
    var localChannels = Playlist.Read(args[1]);
    var combined = Epg.Read(AppPaths.EpgPath);
    combined.AddIsrael(Epg.Read(EpgUpdater.IsraelPath), localChannels);
    combined.AddRegional(Epg.Read(EpgUpdater.RegionalPath), localChannels);
    combined.AddBcu(Epg.Read(EpgUpdater.BcuPath), localChannels);
    var before = localChannels.Count(c => combined.Match(c) is string id && combined.Upcoming(id).Any());
    var iptvx = EpgUpdater.InstallArchive(Path.Combine(args[2], "epg (2).xml.gz"), EpgUpdater.IptvxPath, DateTimeOffset.UtcNow);
    combined.AddRegional(iptvx.Guide, localChannels, "iptvx");
    var middle = localChannels.Count(c => combined.Match(c) is string id && combined.Upcoming(id).Any());
    Console.WriteLine($"IPTVX coverage: {before} -> {middle}");
    var tvteam = EpgUpdater.InstallArchive(Path.Combine(args[2], "7.2.xml"), EpgUpdater.TvteamPath, DateTimeOffset.UtcNow);
    combined.AddRegional(tvteam.Guide, localChannels, "tvteam");
    var after = localChannels.Count(c => combined.Match(c) is string id && combined.Upcoming(id).Any());
    Check(after > before, "Both new sources improve actual coverage");
    Console.WriteLine($"TVTeam coverage: {middle} -> {after} / {localChannels.Count}; total added: {after - before}");
    foreach (var name in new[] { "Channel 9 FHD IL", "Channel 11 HD IL", "Keshet 12 HD IL", "Reshet 13 HD IL" })
    {
        var channel = localChannels.Single(c => c.Name == name);
        var id = combined.Match(channel);
        Check(id?.StartsWith("iptvx:") == true && combined.HasRussianSchedule(id), "Actual Russian Israel schedule: " + name);
        Console.WriteLine(name + " -> " + id + ": " + combined.Upcoming(id!).First().Title);
    }
}
