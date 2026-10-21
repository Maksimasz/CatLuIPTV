using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using IptvPlayer;

internal static class SourceChecks
{
    public static async Task Run()
    {
        void Check(bool condition) { if (!condition) throw new Exception("Remote source check failed"); }
        Check(RemoteSource.IsUrl("https://example.invalid/list?token=test"));
        Check(!RemoteSource.IsUrl("file:///C:/list.m3u") && !RemoteSource.IsUrl("not a URL"));
        var url = "https://example.invalid/" + Guid.NewGuid();
        var playlistCache = RemoteSource.CachePath(url, ".m3u8");
        var epgCache = Path.Combine(AppPaths.ResourcesDirectory, Guid.NewGuid() + ".xml.gz");
        var handler = new ResponseHandler();
        using var transport = new HttpClient(handler);
        try
        {
            handler.Body = Encoding.UTF8.GetBytes("#EXTM3U\n#EXTINF:-1,Test\nhttps://example.invalid/live\n");
            var channels = await RemoteSource.ReadPlaylist(url, default, transport);
            Check(handler.UserAgent.Contains("VLC/"));
            Check(channels.Count == 1 && channels[0].Name == "Test" && File.Exists(playlistCache));
            handler.Status = HttpStatusCode.Forbidden;
            bool rejected = false;
            try { await RemoteSource.ReadPlaylist(url + "/blocked", default, transport); }
            catch (HttpRequestException error) { rejected = error.StatusCode == HttpStatusCode.Forbidden; }
            Check(rejected);
            handler.Status = HttpStatusCode.OK;
            handler.Body = Encoding.UTF8.GetBytes("<html>Invalid playlist</html>");
            Check((await RemoteSource.ReadPlaylist(url, default, transport)).Count == 1);
            var now = DateTime.UtcNow;
            var xml = $"<tv><channel id='test'><display-name>Test</display-name></channel><programme channel='test' start='{now:yyyyMMddHHmmss} +0000' stop='{now.AddHours(1):yyyyMMddHHmmss} +0000'><title>Test show</title></programme></tv>";
            foreach (var compressed in new[] { false, true })
            {
                byte[] body = Encoding.UTF8.GetBytes(xml);
                if (compressed)
                {
                    using var buffer = new MemoryStream();
                    using (var gzip = new GZipStream(buffer, CompressionMode.Compress, true)) gzip.Write(body);
                    body = buffer.ToArray();
                }
                await File.WriteAllBytesAsync(epgCache + ".input", body);
                var result = EpgUpdater.InstallArchive(epgCache + ".input", epgCache, DateTimeOffset.UtcNow);
                Check(result.Count == 1 && File.Exists(epgCache));
            }
        }
        finally
        {
            foreach (var path in new[] { playlistCache, epgCache, epgCache + ".previous.gz", epgCache + ".input" })
                if (File.Exists(path)) File.Delete(path);
        }
    }
    private sealed class ResponseHandler : HttpMessageHandler
    {
        public byte[] Body { get; set; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string UserAgent { get; private set; } = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent(Body) });
        }
    }
}
