using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace IptvPlayer;

public partial class MainWindow : Window
{
    private static readonly string SettingsPath = AppPaths.SettingsPath;
    private PlayerSettings settings = new();
    private bool restoring;
    private bool closed;
    private bool rebuildingFilters;
    private sealed record GroupChoice(string? Value, string Label);
    private readonly CancellationTokenSource lifetime = new();
    private LibVLC? vlc;
    private MediaPlayer? player;
    private List<Channel> channels = [];
    private Epg? epg;
    private Channel? playingChannel;
    private bool fullscreen;
    private WindowState previousWindowState;
    private GridLength previousChannelsWidth;
    private GridLength previousGuideWidth;
    private Point lastVideoPointer;
    private readonly System.Windows.Threading.DispatcherTimer controlsIdle = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(1) };

    public MainWindow()
    {
        InitializeComponent();
        // VideoView рисует содержимое поверх видео в отдельном окне; передаём ему стили.
        VideoOverlay.Resources.MergedDictionaries.Add(Resources);
        controlsIdle.Tick += ControlsIdle_Tick;
        try
        {
            Core.Initialize();
            vlc = new LibVLC("--quiet", "--no-video-title-show");
            player = new MediaPlayer(vlc) { Volume = 70, EnableMouseInput = false, EnableKeyInput = false };
            Video.MediaPlayer = player;
            player.Playing += (_, _) => SetPlaybackState("В эфире");
            player.Paused += (_, _) => SetPlaybackState("Пауза");
            player.Stopped += (_, _) => SetPlaybackState("Остановлено");
            player.EndReached += (_, _) => SetPlaybackState("Завершено");
            player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (closed) return;
                PlaybackState.Text = "Ошибка потока";
                Status.Text = "Ошибка потока: проверьте доступность канала и подключение.";
            });
        }
        catch { Status.Text = "Не удалось загрузить LibVLC. Проверьте комплект файлов приложения (Windows x64)."; }
        timer.Tick += (_, _) => UpdateGuide();
        timer.Start();
        Loaded += RestoreSession;
    }

    private async void RestoreSession(object sender, RoutedEventArgs e)
    {
        restoring = true;
        OpenPlaylist.IsEnabled = OpenEpg.IsEnabled = RefreshEpg.IsEnabled = false;
        var errors = new List<string>();
        try
        {
            try { Directory.CreateDirectory(AppPaths.ResourcesDirectory); settings = PlayerSettings.Read(SettingsPath); }
            catch { errors.Add("Настройки повреждены или недоступны; используются стандартные пути."); }
            Status.Text = "Загрузка сохранённых файлов…";
            if (!string.IsNullOrEmpty(settings.PlaylistPath) && !await LoadPlaylist(settings.PlaylistPath))
                errors.Add("Плейлист недоступен или пуст. Выберите его через «Открыть M3U…».");
            if (!closed && !string.IsNullOrEmpty(settings.EpgPath) && !await LoadEpg(settings.EpgPath))
                errors.Add("EPG недоступен. Нажмите «Обновить EPG» или выберите файл через «Открыть EPG…».");
        }
        finally
        {
            restoring = false;
            if (!closed)
            {
                OpenPlaylist.IsEnabled = OpenEpg.IsEnabled = RefreshEpg.IsEnabled = true;
                Status.Text = errors.Count > 0 ? string.Join(" ", errors) : $"Загружено каналов: {channels.Count}. Для просмотра нажмите «Воспроизвести».";
            }
        }
    }

    private async Task<bool> LoadPlaylist(string path)
    {
        try
        {
            var loaded = await Task.Run(() => Playlist.Read(path));
            if (closed || loaded.Count == 0) return false;
            channels = loaded;
            settings.PlaylistPath = path;
            rebuildingFilters = true;
            try
            {
                foreach (var channel in channels)
                    channel.IsFavorite = settings.Favorites.Contains(new FavoriteChannel(channel.Name, channel.Group));
                Search.Clear();
                var groups = new List<GroupChoice> { new(null, "Все группы") };
                groups.AddRange(channels.Select(c => c.Group).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(g => new GroupChoice(g, g.Length == 0 ? "Без группы" : g)));
                Groups.ItemsSource = groups;
                Groups.SelectedItem = settings.FavoritesOnly ? groups[0] : groups.FirstOrDefault(g => g.Value == settings.SelectedGroup) ?? groups[0];
                Groups.IsEnabled = !settings.FavoritesOnly;
                FavoritesOnly.IsChecked = settings.FavoritesOnly;
            }
            finally { rebuildingFilters = false; }
            settings.SelectedGroup = (Groups.SelectedItem as GroupChoice)?.Value;
            FilterChannels();
            // Старые настройки хранили пустую группу до добавления поддержки EXTGRP.
            Channels.SelectedItem = Channels.Items.Cast<Channel>().FirstOrDefault(c => c.Name == settings.ChannelName &&
                (c.Group == settings.ChannelGroup || string.IsNullOrEmpty(settings.ChannelGroup)));
            if (Channels.SelectedItem is not null) Channels.ScrollIntoView(Channels.SelectedItem);
            else ChannelsColumn.Width = new GridLength(250);
            UpdateGuide();
            return true;
        }
        catch { return false; }
    }

    private async Task<bool> LoadEpg(string path)
    {
        try
        {
            var loaded = await Task.Run(() => Epg.Read(path));
            if (closed) return false;
            epg = loaded;
            settings.EpgPath = path;
            UpdateGuide();
            return true;
        }
        catch { return false; }
    }

    private void SaveSettings()
    {
        if (restoring || closed) return;
        try { settings.Save(SettingsPath); }
        catch { Status.Text += " Не удалось сохранить настройки; выбор может не восстановиться при следующем запуске."; }
    }

    private async void OpenPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Плейлисты|*.m3u;*.m3u8", InitialDirectory = AppPaths.ResourcesDirectory };
        if (dialog.ShowDialog(this) != true) return;
        OpenPlaylist.IsEnabled = RefreshEpg.IsEnabled = false;
        try
        {
            if (!await LoadPlaylist(dialog.FileName)) { Status.Text = "Не удалось прочитать плейлист: файл недоступен, повреждён или не содержит каналов."; return; }
            Status.Text = $"Загружено каналов: {channels.Count}. Выберите канал и нажмите «Воспроизвести».";
            SaveSettings();
        }
        catch { Status.Text = "Не удалось прочитать плейлист. Проверьте файл и права доступа."; }
        finally { if (!closed) OpenPlaylist.IsEnabled = RefreshEpg.IsEnabled = true; }
    }

    private async void OpenEpg_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "XMLTV|*.xml;*.gz", InitialDirectory = AppPaths.ResourcesDirectory };
        if (dialog.ShowDialog(this) != true) return;
        OpenEpg.IsEnabled = RefreshEpg.IsEnabled = false;
        Status.Text = "Чтение программы передач…";
        try
        {
            if (!await LoadEpg(dialog.FileName)) { Status.Text = "Не удалось прочитать EPG. Проверьте файл и права доступа."; return; }
            Status.Text = $"EPG загружен. Сопоставлено каналов: {channels.Count(c => epg?.Match(c) is not null)} из {channels.Count}.";
            SaveSettings();
        }
        catch { Status.Text = "Не удалось прочитать EPG. Проверьте формат XMLTV и целостность архива."; }
        finally { if (!closed) OpenEpg.IsEnabled = RefreshEpg.IsEnabled = true; }
    }

    private async void RefreshEpg_Click(object sender, RoutedEventArgs e)
    {
        OpenPlaylist.IsEnabled = OpenEpg.IsEnabled = RefreshEpg.IsEnabled = false;
        var progress = new Progress<string>(message => { if (!closed) Status.Text = message; });
        try
        {
            var result = await EpgUpdater.UpdateAsync(EpgUpdater.TargetPath, progress, lifetime.Token);
            if (closed) return;
            epg = result.Guide;
            settings.EpgPath = EpgUpdater.TargetPath;
            UpdateGuide();
            Status.Text = $"EPG обновлён. Передач: {result.Count:N0}. Период: 3 дня назад и 3 дня вперёд.";
            SaveSettings();
        }
        catch (OperationCanceledException)
        {
            if (!closed) Status.Text = "Обновление прервано или сервер не ответил вовремя. Предыдущая программа сохранена.";
        }
        catch
        {
            if (!closed) Status.Text = "Не удалось обновить EPG. Проверьте интернет и повторите попытку. Предыдущая программа сохранена.";
        }
        finally
        {
            if (!closed) OpenPlaylist.IsEnabled = OpenEpg.IsEnabled = RefreshEpg.IsEnabled = true;
        }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) { if (Channels is not null && !rebuildingFilters) FilterChannels(); }
    private void Filters_Changed(object sender, RoutedEventArgs e)
    {
        if (Channels is null || rebuildingFilters) return;
        rebuildingFilters = true;
        try
        {
            if (FavoritesOnly.IsChecked == true) Groups.SelectedIndex = 0;
            Groups.IsEnabled = FavoritesOnly.IsChecked != true;
            settings.SelectedGroup = (Groups.SelectedItem as GroupChoice)?.Value;
            settings.FavoritesOnly = FavoritesOnly.IsChecked == true;
        }
        finally { rebuildingFilters = false; }
        FilterChannels();
        SaveSettings();
    }
    private void FilterChannels()
    {
        var selected = Channels.SelectedItem as Channel;
        var visible = Playlist.Filter(channels, Search.Text, (Groups.SelectedItem as GroupChoice)?.Value, FavoritesOnly.IsChecked == true);
        Channels.ItemsSource = visible;
        if (selected is not null && visible.Contains(selected)) Channels.SelectedItem = selected;
        ChannelCount.Text = $"Показано: {visible.Count} из {channels.Count}";
        ChannelsEmpty.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Channel channel) return;
        var key = new FavoriteChannel(channel.Name, channel.Group);
        bool favorite = !channel.IsFavorite;
        if (favorite) settings.Favorites.Add(key); else settings.Favorites.Remove(key);
        foreach (var item in channels.Where(c => c.Name == channel.Name && c.Group == channel.Group)) item.IsFavorite = favorite;
        FilterChannels();
        SaveSettings();
        e.Handled = true;
    }
    private void Channel_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is Visual)
        {
            if (source is Button) return;
            source = VisualTreeHelper.GetParent(source);
        }
        Play_Click(sender, e);
    }
    private void Channel_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateGuide();
        if (Channels.SelectedItem is not Channel channel) return;
        settings.ChannelName = channel.Name;
        settings.ChannelGroup = channel.Group;
        SaveSettings();
    }
    private void UpdateGuide()
    {
        UpdateNow();
        if (Channels.SelectedItem is not Channel channel)
        {
            Guide.ItemsSource = null;
            GuideTitle.Text = "Выберите канал";
            GuideEmpty.Text = "Откройте список кнопкой «Каналы».";
            GuideEmpty.Visibility = Visibility.Visible;
            return;
        }
        GuideTitle.Text = channel.Name;
        var id = epg?.Match(channel);
        var programmes = id is null ? [] : epg!.Upcoming(id).ToList();
        Guide.ItemsSource = programmes;
        GuideEmpty.Text = epg is null ? "Загрузите EPG или нажмите «Обновить EPG»."
            : id is null ? "Для этого канала совпадение EPG не найдено."
            : programmes.Count == 0 ? "В загруженном EPG нет текущих и будущих передач. Обновите EPG." : "";
        GuideEmpty.Visibility = GuideEmpty.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateNow()
    {
        var channel = playingChannel ?? Channels.SelectedItem as Channel;
        CurrentChannel.Text = channel?.Name ?? "Выберите канал";
        var id = channel is null ? null : epg?.Match(channel);
        var programmes = id is null ? [] : epg!.Upcoming(id).ToList();
        var current = programmes.FirstOrDefault(p => p.IsCurrent);
        var next = programmes.FirstOrDefault(p => p.Start > DateTimeOffset.UtcNow);
        NowTitle.Text = current?.Title ?? (channel is null ? "Откройте список каналов" : "Нет текущей программы");
        NextTitle.Text = next is null ? "" : $"Далее: {next.Start.ToLocalTime():HH:mm} · {next.Title}";
        ProgrammeStart.Text = current is null ? "—" : current.Start.ToLocalTime().ToString("HH:mm");
        ProgrammeEnd.Text = current is null ? "—" : current.Stop.ToLocalTime().ToString("HH:mm");
        ProgrammeProgress.Value = current?.Progress ?? 0;
    }

    private void SetPlaybackState(string text) => Dispatcher.BeginInvoke(() => { if (!closed) PlaybackState.Text = text; });
    private void ToggleChannels_Click(object sender, RoutedEventArgs e)
    {
        ChannelsColumn.Width = new GridLength(ChannelsColumn.Width.Value == 0 ? 250 : 0);
        if (ChannelsColumn.Width.Value > 0) Search.Focus();
    }
    private void ToggleGuide_Click(object sender, RoutedEventArgs e)
    {
        GuideColumn.Width = new GridLength(GuideColumn.Width.Value == 0 ? 350 : 0);
        GuideGapColumn.Width = new GridLength(GuideColumn.Width.Value == 0 ? 0 : 12);
    }
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void ToggleFullscreen()
    {
        if (!fullscreen)
        {
            previousWindowState = WindowState;
            previousChannelsWidth = ChannelsColumn.Width;
            previousGuideWidth = GuideColumn.Width;
            fullscreen = true;
            RootDock.Margin = new Thickness(0);
            TopBar.Visibility = Status.Visibility = Visibility.Collapsed;
            ChannelsColumn.Width = GuideColumn.Width = GuideGapColumn.Width = new GridLength(0);
            PlayerBorder.CornerRadius = new CornerRadius(0);
            WindowControlsHost.Content = null;
            FullscreenControlsHost.Content = ControlsPanel;
            FullscreenButton.Content = "⤡  Выйти";
            FullscreenButton.ToolTip = "Выйти из полного экрана (Esc)";
            System.Windows.Automation.AutomationProperties.SetName(FullscreenButton, "Выйти из полного экрана");
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            VideoOverlay.Focus();
            lastVideoPointer = Mouse.GetPosition(VideoOverlay);
            ShowFullscreenControls();
        }
        else
        {
            fullscreen = false;
            controlsIdle.Stop();
            FullscreenControlsHost.Content = null;
            FullscreenControlsHost.Visibility = Visibility.Collapsed;
            WindowControlsHost.Content = ControlsPanel;
            VideoOverlay.Cursor = Cursors.Arrow;
            RootDock.Margin = new Thickness(18);
            TopBar.Visibility = Status.Visibility = Visibility.Visible;
            ChannelsColumn.Width = previousChannelsWidth;
            GuideColumn.Width = previousGuideWidth;
            GuideGapColumn.Width = new GridLength(previousGuideWidth.Value == 0 ? 0 : 12);
            PlayerBorder.CornerRadius = new CornerRadius(6);
            FullscreenButton.Content = "⛶";
            FullscreenButton.ToolTip = "Полный экран (F11), выход — Esc";
            System.Windows.Automation.AutomationProperties.SetName(FullscreenButton, "Полный экран");
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = previousWindowState;
            Focus();
        }
    }
    private void ShowFullscreenControls()
    {
        if (!fullscreen) return;
        if (FullscreenControlsHost.Visibility != Visibility.Visible)
        {
            FullscreenControlsHost.Visibility = Visibility.Visible;
            ((TranslateTransform)FullscreenControlsHost.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(90, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase() });
        }
        VideoOverlay.Cursor = Cursors.Arrow;
        controlsIdle.Stop();
        controlsIdle.Start();
    }
    private void ControlsIdle_Tick(object? sender, EventArgs e)
    {
        if (!fullscreen || ControlsPanel.IsMouseOver || ControlsPanel.IsKeyboardFocusWithin || Mouse.Captured is not null) return;
        controlsIdle.Stop();
        FullscreenControlsHost.Visibility = Visibility.Collapsed;
        VideoOverlay.Cursor = Cursors.None;
    }
    private void Video_MouseMove(object sender, MouseEventArgs e)
    {
        if (!fullscreen) return;
        var position = e.GetPosition(VideoOverlay);
        if (position == lastVideoPointer) return;
        lastVideoPointer = position;
        if (!ControlsPanel.IsMouseOver && ControlsPanel.IsKeyboardFocusWithin) VideoOverlay.Focus();
        ShowFullscreenControls();
    }
    private void Video_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ControlsPanel.IsMouseOver) return;
        VideoOverlay.Focus();
        ShowFullscreenControls();
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (fullscreen) ShowFullscreenControls();
        if (e.Key == Key.F11 || e.Key == Key.Escape && fullscreen) { ToggleFullscreen(); e.Handled = true; }
        else if (e.Key == Key.Escape) { ChannelsColumn.Width = new GridLength(0); e.Handled = true; }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        var selected = Channels.SelectedItem as Channel;
        var channel = fullscreen ? playingChannel ?? selected : selected;
        if (channel is null || player is null || vlc is null) return;
        try
        {
            using var media = new Media(vlc, channel.Stream);
            if (player.Play(media))
            {
                playingChannel = channel;
                PlaybackState.Text = "Подключение…";
                Status.Text = $"Воспроизведение: {channel.Name}";
                ChannelsColumn.Width = new GridLength(0);
                UpdateNow();
            }
            else Status.Text = "Не удалось начать воспроизведение.";
        }
        catch { Status.Text = "Не удалось открыть канал."; }
    }
    private void Pause_Click(object sender, RoutedEventArgs e) => player?.Pause();
    private void Stop_Click(object sender, RoutedEventArgs e) { player?.Stop(); Status.Text = "Воспроизведение остановлено."; }
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (player is not null) player.Volume = (int)e.NewValue; }
    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        lifetime.Cancel();
        timer.Stop();
        controlsIdle.Stop();
        Video.MediaPlayer = null;
        player?.Stop();
        Video.Dispose();
        player?.Dispose();
        vlc?.Dispose();
        base.OnClosed(e);
    }
}
