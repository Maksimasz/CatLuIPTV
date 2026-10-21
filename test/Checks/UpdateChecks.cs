using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using IptvPlayer;

public static class UpdateChecks
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iptv-update-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var archive = Path.Combine(directory, "source.xml.gz");
        var target = Path.Combine(directory, "small.xml.gz");
        var now = DateTimeOffset.UtcNow;
        string Stamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyyMMddHHmmss") + " +0000";
        XElement Programme(DateTimeOffset start, string title) => new("programme",
            new XAttribute("channel", "test"), new XAttribute("start", Stamp(start)),
            new XAttribute("stop", Stamp(start.AddHours(1))), new XElement("title", title));
        void WriteArchive(string xml)
        {
            using var file = File.Create(archive);
            using var gzip = new GZipStream(file, CompressionLevel.Fastest);
            gzip.Write(Encoding.UTF8.GetBytes(xml));
        }
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        try
        {
            var channel = new XElement("channel", new XAttribute("id", "test"), new XElement("display-name", "Тест"));
            var first = new DateTimeOffset(now.UtcDateTime.Date.AddDays(-3), TimeSpan.Zero);
            var last = new DateTimeOffset(now.UtcDateTime.Date.AddDays(4), TimeSpan.Zero).AddSeconds(-1);
            var valid = new XElement("tv", channel,
                Programme(first.AddSeconds(-1), "Слишком рано"), Programme(first, "Первая граница"),
                Programme(now, "Текущая передача"), Programme(last, "Последняя граница"),
                Programme(last.AddSeconds(1), "Слишком поздно")).ToString();
            WriteArchive(valid);
            var result = EpgUpdater.InstallArchive(archive, target, now);
            Check(result.Count == 3 && result.Guide.Upcoming("test").Count() == 2, "Update date boundaries and EPG reload");
            var original = File.ReadAllBytes(target);
            Check(!EpgUpdater.NeedsUpdate(target, now), "Fresh cache skips automatic download");
            Check(EpgUpdater.NeedsUpdate(target, now.AddHours(13)), "Old cache refreshes automatically");
            Check(EpgUpdater.NeedsUpdate(target + ".missing", now), "Missing cache downloads automatically");
            File.WriteAllText(archive, valid);
            Check(EpgUpdater.InstallArchive(archive, target, now).Count == 3, "Uncompressed Israel XML is accepted");


            foreach (var invalid in new[] { "<tv><broken>", "<html/>", "<tv/>",
                new XElement("tv", channel, Programme(now, "No Data")).ToString(),
                new XElement("tv", channel, Programme(now.AddDays(-2), "Только прошлое")).ToString(),
                new XElement("tv", Programme(now, "Нет описания канала")).ToString() })
            {
                WriteArchive(invalid);
                bool rejected = false;
                try { EpgUpdater.InstallArchive(archive, target, now); } catch { rejected = true; }
                Check(rejected && File.ReadAllBytes(target).SequenceEqual(original), "Invalid update must preserve working EPG");
            }
            File.WriteAllText(archive, "not gzip");
            bool badGzip = false;
            try { EpgUpdater.InstallArchive(archive, target, now); } catch (System.Xml.XmlException) { badGzip = true; }
            Check(badGzip && File.ReadAllBytes(target).SequenceEqual(original), "Invalid gzip preserves EPG");

            WriteArchive(valid);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            bool stopped = false;
            try { EpgUpdater.InstallArchive(archive, target, now, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped && File.ReadAllBytes(target).SequenceEqual(original), "Cancellation preserves EPG");

            EpgUpdater.InstallArchive(archive, target, now);
            Check(File.ReadAllBytes(target + ".previous.gz").SequenceEqual(original), "Previous EPG backup");
            EpgUpdater.InstallArchive(archive, target, now);
            Check(File.Exists(target + ".previous.gz"), "Repeated update replaces backup");
            Check(!Directory.EnumerateFiles(directory, "*.tmp.gz").Any(), "Temporary files cleaned");
            Console.WriteLine("Проверки обновления, дат, резервной копии и отказов: OK");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
