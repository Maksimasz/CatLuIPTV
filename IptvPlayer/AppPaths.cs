using System.IO;

namespace IptvPlayer;

public static class AppPaths
{
    public static readonly string DataDirectory = ResolveDataDirectory(AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    public static string ResourcesDirectory => Path.Combine(DataDirectory, "resurses");
    public static string SettingsPath => Path.Combine(DataDirectory, "player-settings.json");
    public static string PlaylistPath => Path.Combine(ResourcesDirectory, "playlist.m3u8");
    public static string EpgPath => Path.Combine(ResourcesDirectory, "epg_small.xml.gz");

    public static string ResolveDataDirectory(string baseDirectory, string localApplicationData)
    {
        // Рабочая копия сохраняет прежние данные; установленный плеер использует папку пользователя.
        for (var directory = new DirectoryInfo(baseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "IptvPlayer", "IptvPlayer.csproj")))
                return directory.FullName;
        return Path.Combine(localApplicationData, "CatLu IPTV");
    }
}
