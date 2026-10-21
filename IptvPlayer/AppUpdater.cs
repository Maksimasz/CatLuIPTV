using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace IptvPlayer;

public sealed record AppRelease(Version Version, string FileName, Uri InstallerUrl, Uri ChecksumsUrl);

public sealed class AppUpdater
{
    private const string RepositoryUrl = "https://github.com/Maksimasz/windows-iptv-player/";
    private readonly HttpClient client;
    public AppUpdater(HttpClient? client = null) => this.client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    public static bool IsDue(DateTimeOffset now, DateTimeOffset lastCheck) => now - lastCheck >= TimeSpan.FromHours(24);
    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    private async Task<HttpResponseMessage> Get(Uri url, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("CatLu-IPTV/1.0");
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
    }

    public static AppRelease? ParseRelease(string json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) throw new InvalidDataException("Некорректная версия обновления.");
        if (Normalize(version) <= Normalize(current)) return null;
        version = Normalize(version);
        var file = $"CatLu-IPTV-{version.ToString(3)}-Setup-x64.exe";
        Uri Asset(string name)
        {
            var asset = root.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == name);
            if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("В релизе отсутствует установщик или контрольная сумма.");
            var url = asset.GetProperty("browser_download_url").GetString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                !uri.AbsoluteUri.StartsWith(RepositoryUrl + "releases/download/", StringComparison.Ordinal))
                throw new InvalidDataException("Некорректный адрес обновления.");
            return uri;
        }
        return new AppRelease(version, file, Asset(file), Asset("SHA256SUMS.txt"));
    }

    private static async Task<byte[]> ReadLimited(HttpResponseMessage response, int limit, CancellationToken cancellation)
    {
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var data = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellation)) > 0)
        {
            if (data.Length + read > limit) throw new InvalidDataException("Ответ сервера слишком большой.");
            data.Write(buffer, 0, read);
        }
        return data.ToArray();
    }

    public async Task<AppRelease?> CheckAsync(Version current, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await Get(new Uri("https://api.github.com/repos/Maksimasz/windows-iptv-player/releases/latest"), timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidDataException("Источник обновлений недоступен (GitHub 404).");
        return ParseRelease(System.Text.Encoding.UTF8.GetString(await ReadLimited(response, 1024 * 1024, timeout.Token)), current);
    }

    public async Task<string> DownloadAsync(AppRelease release, string directory, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var sums = await Get(release.ChecksumsUrl, timeout.Token);
        var manifest = System.Text.Encoding.UTF8.GetString(await ReadLimited(sums, 64 * 1024, timeout.Token));
        var expected = manifest.Split('\n').Select(line => line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(parts => parts.Length == 2 && parts[1].TrimStart('*') == release.FileName)?[0];
        if (expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            throw new InvalidDataException("Не найдена контрольная сумма установщика.");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, release.FileName);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var response = await Get(release.InstallerUrl, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[81920]; long size = 0; int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    size += read;
                    if (size > 512L * 1024 * 1024) throw new InvalidDataException("Установщик слишком большой.");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }
            }
            await using var downloaded = File.OpenRead(temporary);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, timeout.Token));
            if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Контрольная сумма не совпала. Установка отменена.");
            downloaded.Close();
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
