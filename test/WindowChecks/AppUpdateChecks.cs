using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IptvPlayer;

internal static class AppUpdateChecks
{
    private const string Prefix = "https://github.com/Maksimasz/windows-iptv-player/releases/download/v0.0.7/";
    private const string FileName = "CatLu-IPTV-0.0.7-Setup-x64.exe";
    private static string Json(string tag = "v0.0.7", bool prerelease = false, string prefix = Prefix) => JsonSerializer.Serialize(new {
        tag_name = tag, draft = false, prerelease,
        assets = new[] {
            new { name = FileName, browser_download_url = prefix + FileName },
            new { name = "SHA256SUMS.txt", browser_download_url = prefix + "SHA256SUMS.txt" }
        }
    });

    public static async Task Run()
    {
        void Check(bool value, string label) { if (!value) throw new Exception(label); }
        var current = new Version(0, 0, 6, 0);
        Check(AppUpdater.ParseRelease(Json("v0.0.6"), current) is null, "Installed release does not update itself");
        Check(AppUpdater.ParseRelease(Json("v0.0.5"), current) is null, "No downgrade");
        Check(AppUpdater.ParseRelease(Json(prerelease: true), current) is null, "Skip prereleases");
        bool unsafeUrlRejected = false;
        try { AppUpdater.ParseRelease(Json(prefix: "https://example.invalid/"), current); }
        catch (InvalidDataException) { unsafeUrlRejected = true; }
        Check(unsafeUrlRejected, "Only repository release assets are accepted");
        var now = DateTimeOffset.UtcNow;
        Check(!AppUpdater.IsDue(now, now.AddHours(-23)) && AppUpdater.IsDue(now, now.AddHours(-24)), "24 hour schedule");

        var handler = new Handler { Metadata = Json() };
        using var client = new HttpClient(handler);
        var updater = new AppUpdater(client);
        var release = await updater.CheckAsync(current, default) ?? throw new Exception("New release not detected");
        var directory = Path.Combine(AppPaths.DataDirectory, "work", "update-check-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var sentinel = Path.Combine(directory, "player-settings.json");
        File.WriteAllText(sentinel, "preserved");
        try
        {
            var installer = await updater.DownloadAsync(release, directory, default);
            Check(File.ReadAllBytes(installer).SequenceEqual(handler.Payload), "Verified installer downloaded");
            File.Delete(installer);
            handler.BadHash = true;
            bool rejected = false;
            try { await updater.DownloadAsync(release, directory, default); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && !File.Exists(installer) && !Directory.GetFiles(directory, "*.download").Any(), "Corrupt installer removed");
            Check(File.ReadAllText(sentinel) == "preserved", "Downloading does not modify settings");
            handler.Missing = true;
            bool missing = false;
            try { await updater.CheckAsync(current, default); }
            catch (InvalidDataException) { missing = true; }
            Check(missing, "Unavailable releases never report that the app is up to date");
        }
        finally
        {
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
        Console.WriteLine("App updates: versions, schedule, download and checksum checks OK");
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string Metadata { get; set; } = "";
        public byte[] Payload { get; } = Encoding.ASCII.GetBytes("Synthetic installer; never execute");
        public bool BadHash { get; set; }
        public bool Missing { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var content = path.EndsWith("/latest") ? Encoding.UTF8.GetBytes(Metadata)
                : path.EndsWith("SHA256SUMS.txt") ? Encoding.ASCII.GetBytes($"{(BadHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(Payload)))}  {FileName}\n") : Payload;
            return Task.FromResult(new HttpResponseMessage(Missing ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }
}
