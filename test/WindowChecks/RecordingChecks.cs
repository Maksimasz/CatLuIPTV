using System.IO;
using IptvPlayer;
using LibVLCSharp.Shared;

internal static class RecordingChecks
{
    public static async Task Run(string fixture)
    {
        Core.Initialize();
        using var vlc = new LibVLC("--quiet");
        var directory = Path.Combine(Path.GetTempPath(), "recording-'проверка-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "recording.ts");
        try
        {
            var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var recording = new StreamRecording(vlc, new Uri(Path.GetFullPath(fixture)), destination);
            recording.Finished += (_, failed) => finished.TrySetResult(!failed);
            if (!await finished.Task.WaitAsync(TimeSpan.FromSeconds(20))) throw new Exception("Recording input failed");
            var bytes = recording.Stop();
            if (bytes < 188 || File.Exists(recording.PartialPath)) throw new Exception("Recording not finalized");
            var data = File.ReadAllBytes(destination);
            if (data[0] != 0x47 || data[188] != 0x47) throw new Exception("Not an MPEG-TS recording");
            var verification = Environment.GetEnvironmentVariable("EPG_RECORDING_OUTPUT");
            if (!string.IsNullOrEmpty(verification)) File.Copy(destination, verification, overwrite: true);
            bool rejected = false;
            try { using var duplicate = new StreamRecording(vlc, new Uri(Path.GetFullPath(fixture)), destination); }
            catch (IOException) { rejected = true; }
            if (!rejected || !data.SequenceEqual(File.ReadAllBytes(destination))) throw new Exception("Existing recording overwritten");
            if (recording.Stop() != bytes) throw new Exception("Repeated stop changed recording");
            Console.WriteLine("Recording: native input, TS output, Unicode/quoted path, finalization and overwrite protection OK");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
