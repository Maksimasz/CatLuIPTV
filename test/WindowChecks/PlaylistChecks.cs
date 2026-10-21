using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IptvPlayer;

internal static class PlaylistChecks
{
    public static void Run(MainWindow window)
    {
        var type = typeof(MainWindow);
        var settings = (PlayerSettings)type.GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        var restoring = type.GetField("restoring", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var method = type.GetMethod("ChangePlaylist", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var files = new[] { Path.Combine(AppPaths.ResourcesDirectory, Guid.NewGuid() + ".m3u"), Path.Combine(AppPaths.ResourcesDirectory, Guid.NewGuid() + ".m3u") };
        void Check(bool value) { if (!value) throw new Exception("Playlist isolation or management failed"); }
        bool Change(SavedPlaylist? previous, SavedPlaylist? replacement)
        {
            bool result = false;
            Exception? error = null;
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(async () =>
            {
                try { result = await (Task<bool>)method.Invoke(window, [previous, replacement])!; }
                catch (Exception e) { error = e; }
                finally { frame.Continue = false; }
            });
            Dispatcher.PushFrame(frame);
            if (error is not null) throw error;
            return result;
        }
        try
        {
            restoring.SetValue(window, true); // Keep the user's settings untouched.
            for (int i = 0; i < files.Length; i++) File.WriteAllText(files[i], $"#EXTM3U\n#EXTINF:-1,List{i}\nhttps://example.invalid/{i}\n");
            var first = new SavedPlaylist { Name = "First", Source = files[0] };
            var second = new SavedPlaylist { Name = "Second", Source = files[1] };
            var channels = (ListBox)window.FindName("Channels");
            Check(Change(null, first));
            Check(channels.Items.Count == 1 && ((Channel)channels.Items[0]).Name == "List0");
            Check(Change(null, second));
            Check(settings.Playlists.Count == 2 && channels.Items.Count == 1 && ((Channel)channels.Items[0]).Name == "List1");
            var edited = new SavedPlaylist { Id = second.Id, Name = "Renamed", Source = files[0] };
            Check(Change(second, edited));
            Check(settings.Playlists.Count == 2 && channels.Items.Count == 1 && ((Channel)channels.Items[0]).Name == "List0");
            Check(Change(edited, null) && settings.ActivePlaylistId == first.Id);
            Check(Change(first, null) && channels.Items.Count == 0 && settings.PlaylistPath is null);
            Check(files.All(File.Exists));
            var settingsPath = files[0] + ".json";
            try
            {
                new PlayerSettings { PlaylistPath = files[0] }.Save(settingsPath);
                var migrated = PlayerSettings.Read(settingsPath);
                Check(migrated.Playlists.Count == 1 && migrated.Playlists[0].Source == files[0]);
                migrated.Playlists.Add(second);
                migrated.ActivePlaylistId = second.Id;
                migrated.Save(settingsPath);
                var restored = PlayerSettings.Read(settingsPath);
                Check(restored.Playlists.Count == 2 && restored.ActivePlaylistId == second.Id && restored.PlaylistPath == second.Source);
                restored.Playlists.Clear(); restored.ActivePlaylistId = null; restored.PlaylistPath = null;
                restored.Save(settingsPath);
                Check(PlayerSettings.Read(settingsPath).Playlists.Count == 0);
            }
            finally { if (File.Exists(settingsPath)) File.Delete(settingsPath); }
            if (Environment.GetEnvironmentVariable("CATLU_QA_IMAGE") is { Length: > 0 } imagePath)
            {
                var dialog = PlaylistManager.Create(window, [first, second], (_, _) => Task.FromResult(true));
                var content = (FrameworkElement)dialog.Content;
                ((Panel)content).Background = window.Background;
                content.Measure(new Size(600, 900)); content.Arrange(new Rect(0, 0, 600, content.DesiredSize.Height)); content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(600, (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(imagePath); encoder.Save(output);
                dialog.Close();
            }
        }
        finally
        {
            restoring.SetValue(window, false);
            foreach (var path in files) if (File.Exists(path)) File.Delete(path);
        }
    }
}
