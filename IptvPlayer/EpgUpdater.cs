using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace IptvPlayer;

public static class EpgUpdater
{
    private static readonly HttpClient client = new();
    public const string TargetPath = @"D:\web\epg\resurses\epg_small.xml.gz";

    public static async Task<(Epg Guide, int Count)> UpdateAsync(string targetPath,
        IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        var downloadPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".download.gz";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            progress?.Report("Скачивание полного EPG…");
            using (var response = await client.GetAsync("http://epg.one/epg2.xml.gz",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token))
            {
                response.EnsureSuccessStatusCode();
                const long limit = 256L * 1024 * 1024;
                if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Архив слишком большой.");
                await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
                await using var output = new FileStream(downloadPath, FileMode.CreateNew, FileAccess.Write);
                var buffer = new byte[81920];
                long downloaded = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    downloaded += read;
                    if (downloaded > limit) throw new InvalidDataException("Архив слишком большой.");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }
            }
            progress?.Report("Подготовка и проверка облегчённого EPG…");
            return await Task.Run(() => InstallArchive(downloadPath, targetPath, DateTimeOffset.UtcNow, timeout.Token), timeout.Token);
        }
        finally { if (File.Exists(downloadPath)) File.Delete(downloadPath); }
    }

    public static (Epg Guide, int Count) InstallArchive(string archivePath, string targetPath,
        DateTimeOffset now, CancellationToken cancellation = default)
    {
        var temporary = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp.gz";
        var firstDay = now.UtcDateTime.Date.AddDays(-3);
        var lastDay = now.UtcDateTime.Date.AddDays(3);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var upcomingIds = new HashSet<string>(StringComparer.Ordinal);
        int count = 0;
        try
        {
            using (var input = File.OpenRead(archivePath))
            using (var xml = new GZipStream(input, CompressionMode.Decompress))
            using (var reader = XmlReader.Create(xml, new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 1_000_000_000 }))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            using (var writer = XmlWriter.Create(gzip, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
            {
                cancellation.ThrowIfCancellationRequested();
                reader.MoveToContent();
                if (reader.Name != "tv") throw new InvalidDataException("Ожидался XMLTV.");
                writer.WriteStartDocument();
                writer.WriteStartElement("tv");
                while (reader.Read())
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (reader.NodeType != XmlNodeType.Element || reader.Name is not ("channel" or "programme")) continue;
                    using var subtree = reader.ReadSubtree();
                    var element = XElement.Load(subtree);
                    if (element.Name == "channel")
                    {
                        var id = (string?)element.Attribute("id");
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        ids.Add(id);
                    }
                    else
                    {
                        var id = (string?)element.Attribute("channel");
                        if (string.IsNullOrWhiteSpace(id) ||
                            !Epg.TryTime((string?)element.Attribute("start"), out var start) ||
                            !Epg.TryTime((string?)element.Attribute("stop"), out var stop) || stop <= start ||
                            start.UtcDateTime.Date < firstDay || start.UtcDateTime.Date > lastDay) continue;
                        count++;
                        if (stop > now) upcomingIds.Add(id);
                    }
                    element.WriteTo(writer);
                }
                writer.WriteEndElement();
                writer.WriteEndDocument();
            }
            if (count == 0 || !upcomingIds.Overlaps(ids))
                throw new InvalidDataException("Нет актуальной программы передач.");
            var guide = Epg.Read(temporary);
            cancellation.ThrowIfCancellationRequested();
            // Заменяем только проверенный производный файл; полные исходники не изменяем.
            if (File.Exists(targetPath)) File.Replace(temporary, targetPath, targetPath + ".previous.gz");
            else File.Move(temporary, targetPath);
            return (guide, count);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
