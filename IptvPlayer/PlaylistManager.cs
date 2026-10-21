using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace IptvPlayer;

public static class PlaylistManager
{
    public static Window Create(Window owner, List<SavedPlaylist> playlists,
        Func<SavedPlaylist?, SavedPlaylist?, Task<bool>> change)
    {
        var list = new ListBox { Height = 160, Margin = new Thickness(0, 10, 0, 16), DisplayMemberPath = "Name" };
        TextBox Input() => new() { Background = new SolidColorBrush(Color.FromRgb(21, 25, 31)),
            Foreground = Brushes.WhiteSmoke, CaretBrush = Brushes.WhiteSmoke, BorderThickness = new Thickness(0),
            Padding = new Thickness(9), Margin = new Thickness(0, 6, 0, 12) };
        var name = Input(); var source = Input();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        var actions = new WrapPanel();
        Button Button(string title) { var button = new Button { Content = title, Margin = new Thickness(0, 0, 8, 8) }; actions.Children.Add(button); return button; }
        var add = Button("Новый"); var save = Button("Сохранить"); var remove = Button("Удалить"); var close = Button("Закрыть");
        var browse = new Button { Content = "Выбрать файл…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Плейлисты", FontSize = 22, FontWeight = FontWeights.Bold });
        panel.Children.Add(list);
        panel.Children.Add(new TextBlock { Text = "Название плейлиста" }); panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "Ссылка HTTP/HTTPS или путь к M3U" }); panel.Children.Add(source);
        panel.Children.Add(browse); panel.Children.Add(actions); panel.Children.Add(status);
        var dialog = new Window { Title = "Настройки — Плейлисты", Width = 600,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Content = panel,
            Background = owner.Background, Foreground = owner.Foreground, Resources = owner.Resources,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        void Refresh(SavedPlaylist? selected) { list.ItemsSource = playlists.ToArray(); list.SelectedItem = selected; }
        list.SelectionChanged += (_, _) =>
        {
            var selected = list.SelectedItem as SavedPlaylist;
            name.Text = selected?.Name ?? ""; source.Text = selected?.Source ?? "";
            remove.IsEnabled = selected is not null; status.Text = "";
        };
        browse.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = "Плейлисты|*.m3u;*.m3u8" };
            if (picker.ShowDialog(dialog) == true) source.Text = picker.FileName;
        };
        add.Click += (_, _) => { list.SelectedItem = null; name.Clear(); source.Clear(); name.Focus(); };
        async Task Apply(bool deleting)
        {
            var selected = list.SelectedItem as SavedPlaylist;
            SavedPlaylist? replacement = null;
            if (!deleting)
            {
                var title = name.Text.Trim(); var location = source.Text.Trim();
                if (title.Length == 0 || location.Length == 0) { status.Text = "Укажите название и источник плейлиста."; return; }
                if (!RemoteSource.IsUrl(location) && !File.Exists(location)) { status.Text = "Выберите существующий файл или ссылку HTTP/HTTPS."; return; }
                if (playlists.Any(p => p != selected && string.Equals(p.Name, title, StringComparison.OrdinalIgnoreCase)))
                { status.Text = "Такое название уже есть. Укажите другое."; return; }
                replacement = new SavedPlaylist { Id = selected?.Id ?? Guid.NewGuid().ToString("N"), Name = title, Source = location };
            }
            else if (selected is null) return;
            panel.IsEnabled = false; status.Text = deleting ? "Удаление…" : "Загрузка плейлиста…";
            try
            {
                if (await change(selected, replacement)) { Refresh(replacement); status.Text = deleting ? "Плейлист убран из плеера. Исходный файл сохранён." : "Плейлист сохранён."; }
                else status.Text = "Не удалось загрузить плейлист. Проверьте файл или ссылку.";
            }
            catch (InvalidOperationException error) { status.Text = error.Message; }
            catch { status.Text = "Не удалось сохранить изменения."; }
            finally { panel.IsEnabled = true; }
        }
        save.Click += async (_, _) => await Apply(false);
        remove.Click += async (_, _) => await Apply(true);
        close.Click += (_, _) => dialog.Close();
        Refresh(playlists.FirstOrDefault()); remove.IsEnabled = list.SelectedItem is not null;
        return dialog;
    }
}
