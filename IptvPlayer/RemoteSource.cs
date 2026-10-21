using System.IO;
using System.Net.Http;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IptvPlayer;

public static class RemoteSource
{
    private static readonly HttpClient client = new(new HttpClientHandler {
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = TimeSpan.FromMinutes(2) };
    private static readonly HttpClient directClient = new(new HttpClientHandler {
        AutomaticDecompression = DecompressionMethods.All, UseProxy = false
    }) { Timeout = TimeSpan.FromMinutes(2) };

    private static async Task<HttpResponseMessage> Download(string source, HttpClient? transport, CancellationToken cancellation)
    {
        HttpRequestMessage Request()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, source);
            request.Headers.UserAgent.ParseAdd("VLC/3.0.24 LibVLC/3.0.24");
            return request;
        }
        using var request = Request();
        try { return await (transport ?? client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation); }
        catch (HttpRequestException error) when (transport is null && error.StatusCode is null && !cancellation.IsCancellationRequested)
        {
            // VLC can connect directly even when the Windows proxy is unavailable.
            using var retry = Request();
            return await directClient.SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, cancellation);
        }
    }
    public static bool IsUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    public static string CachePath(string url, string extension) => Path.Combine(AppPaths.ResourcesDirectory,
        "remote-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + extension);

    public static async Task<List<Channel>> ReadPlaylist(string source, CancellationToken cancellation, HttpClient? transport = null)
    {
        if (!IsUrl(source)) return await Task.Run(() => Playlist.Read(source), cancellation);
        var cache = CachePath(source, ".m3u8");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            using var response = await Download(source, transport, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var block = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(block, timeout.Token)) > 0)
            {
                if (buffer.Length + read > 16 * 1024 * 1024) throw new InvalidDataException("Размер плейлиста превышает 16 МБ.");
                buffer.Write(block, 0, read);
            }
            buffer.Position = 0;
            using var reader = new StreamReader(buffer);
            var text = await reader.ReadToEndAsync(timeout.Token);
            var channels = Playlist.Parse(text.Split('\n'));
            if (channels.Count == 0) throw new InvalidDataException("Источник не содержит каналов M3U. Возможно, сервер вернул страницу сайта или входа.");
            Directory.CreateDirectory(AppPaths.ResourcesDirectory);
            var temporary = cache + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, text, cancellation);
                File.Move(temporary, cache, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return channels;
        }
        catch when (!cancellation.IsCancellationRequested && File.Exists(cache)) { return Playlist.Read(cache); }
    }

    public static string? Prompt(Window owner, string title)
    {
        var input = new TextBox { Margin = new Thickness(0, 10, 0, 12), MinWidth = 440,
            Background = new SolidColorBrush(Color.FromRgb(21, 25, 31)), Foreground = Brushes.WhiteSmoke,
            CaretBrush = Brushes.WhiteSmoke, Padding = new Thickness(8) };
        var error = new TextBlock { Text = "Введите ссылку HTTP или HTTPS.", Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
        var load = new Button { Content = "Загрузить", IsDefault = true, Padding = new Thickness(14, 7, 14, 7) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 7, 14, 7) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(load); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Ссылка на " + title });
        panel.Children.Add(input); panel.Children.Add(error); panel.Children.Add(buttons);
        var dialog = new Window { Owner = owner, Title = title + " по ссылке", Content = panel,
            Background = owner.Background, Foreground = owner.Foreground, Resources = owner.Resources,
            SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        load.Click += (_, _) => { if (IsUrl(input.Text.Trim())) dialog.DialogResult = true; else error.Visibility = Visibility.Visible; };
        dialog.Loaded += (_, _) => input.Focus();
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }
}
