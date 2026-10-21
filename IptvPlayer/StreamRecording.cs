using System.IO;
using LibVLCSharp.Shared;

namespace IptvPlayer;

// A separate input keeps recording the original channel when viewing another one.
public sealed class StreamRecording : IDisposable
{
    private readonly MediaPlayer recorder;
    private bool stopped;
    public string Destination { get; }
    public string PartialPath { get; }
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    public long Bytes => File.Exists(PartialPath) ? new FileInfo(PartialPath).Length : 0;
    public event Action<StreamRecording, bool>? Finished;

    public StreamRecording(LibVLC vlc, Uri source, string destination)
    {
        Destination = Path.GetFullPath(destination);
        if (File.Exists(Destination)) throw new IOException("Файл уже существует. Выберите другое имя.");
        PartialPath = Path.Combine(Path.GetDirectoryName(Destination)!, Guid.NewGuid().ToString("N") + ".part.ts");
        using (File.Open(PartialPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
        recorder = new MediaPlayer(vlc);
        recorder.EndReached += (_, _) => Finished?.Invoke(this, false);
        recorder.EncounteredError += (_, _) => Finished?.Invoke(this, true);
        try
        {
            using var media = new Media(vlc, source);
            var path = PartialPath.Replace('\\', '/').Replace("'", "\\'");
            media.AddOption($":sout=#std{{access=file,mux=ts,dst='{path}'}}");
            media.AddOption(":no-sout-all");
            if (!recorder.Play(media)) throw new IOException("Не удалось начать запись потока.");
        }
        catch
        {
            recorder.Dispose();
            if (Bytes == 0) File.Delete(PartialPath);
            throw;
        }
    }

    public long Stop()
    {
        if (stopped) return File.Exists(Destination) ? new FileInfo(Destination).Length : 0;
        stopped = true;
        try { recorder.Stop(); }
        finally { recorder.Dispose(); }
        var bytes = Bytes;
        if (bytes > 0) File.Move(PartialPath, Destination, overwrite: false);
        else File.Delete(PartialPath);
        return bytes;
    }

    public void Dispose() => Stop();
}
