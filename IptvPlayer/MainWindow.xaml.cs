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
    private int playlistLoadVersion;
    private int epgLoadVersion;
    private bool epgChosenDuringRestore;
    private bool rebuildingPlaylists;
    private readonly AppUpdater appUpdater = new();
    private AppRelease? availableAppRelease;
    private DateTimeOffset lastAppUpdateCheck = DateTimeOffset.MinValue;
    private bool checkingAppUpdate;
    private bool installingAppUpdate;
    private bool closed;
    private bool rebuildingFilters;
    private sealed record GroupChoice(string? Value, string Label);
    private readonly CancellationTokenSource lifetime = new();
    private LibVLC? vlc;
    private MediaPlayer? player;
    private List<Channel> channels = [];
    private Epg? epg;
    private Epg? israelEpg;
    private Epg? regionalEpg;
    private Epg? bcuEpg;
    private Epg? iptvxEpg;
    private Epg? tvteamEpg;
    private bool primaryEpgLoaded;
    private bool updatingEpg;
    private CancellationTokenSource? epgUpdateCancellation;
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
        timer.Tick += async (_, _) =>
        {
            UpdateGuide();
            if (AppUpdater.IsDue(DateTimeOffset.UtcNow, lastAppUpdateCheck)) await CheckAppUpdates(false);
        };
        timer.Start();
        Loaded += RestoreSession;
    }

    private async void RestoreSession(object sender, RoutedEventArgs e)
    {
        restoring = true;
        var errors = new List<string>();
        try
        {
            try { Directory.CreateDirectory(AppPaths.ResourcesDirectory); settings = PlayerSettings.Read(SettingsPath); }
            catch { errors.Add("Настройки повреждены или недоступны; используются стандартные пути."); }
            RefreshPlaylistSelector();
            _ = CheckAppUpdates(false);
            Status.Text = "Загрузка сохранённых файлов…";
            bool playlistLoaded = false;
            if (!string.IsNullOrEmpty(settings.PlaylistPath) && !(playlistLoaded = await LoadPlaylist(settings.PlaylistPath)))
                errors.Add("Плейлист недоступен или пуст. Выберите его через «Открыть M3U…».");
            if (playlistLoaded && !closed)
            {
                var lastChannel = channels.FirstOrDefault(c => c.Name == settings.ChannelName &&
                    (c.Group == settings.ChannelGroup || string.IsNullOrEmpty(settings.ChannelGroup)));
                if (lastChannel is not null) PlayChannel(lastChannel);
            }
            if (!closed && !epgChosenDuringRestore && !string.IsNullOrEmpty(settings.EpgPath) && !await LoadEpg(settings.EpgPath))
                errors.Add("EPG недоступен. Нажмите «Обновить EPG» или выберите файл через «Открыть EPG…».");
            if (!closed) await LoadIsraelEpg();
            if (!closed) await LoadRegionalEpg();
            if (!closed) await LoadBcuEpg();
            if (!closed) await LoadExtraEpg(iptvx: true);
            if (!closed) await LoadExtraEpg(iptvx: false);
        }
        finally
        {
            restoring = false;
            SaveSettings();
            if (!closed)
            {
                OpenPlaylist.IsEnabled = OpenEpg.IsEnabled = RefreshEpg.IsEnabled = true;
                Status.Text = errors.Count > 0 ? string.Join(" ", errors) : playingChannel is not null ? $"Воспроизводится: {playingChannel.Name}" : $"Загружено каналов: {channels.Count}. Для просмотра нажмите «Воспроизвести».";
            }
        }
        if (!closed)
        {
            await UpdateEpg(staleOnly: true);
        }
    }

    private async Task<bool> LoadIsraelEpg()
    {
        var path = File.Exists(EpgUpdater.IsraelPath) ? EpgUpdater.IsraelPath
            : Path.Combine(AppPaths.ResourcesDirectory, "epg-il.xml");
        try
        {
            var loaded = await Task.Run(() => Epg.Read(path));
            if (closed) return false;
            israelEpg = loaded;
            AttachSupplementalEpg();
            UpdateGuide();
            return true;
        }
        catch { return false; }
    }

    private async Task LoadRegionalEpg()
    {
        var path = File.Exists(EpgUpdater.RegionalPath) ? EpgUpdater.RegionalPath
            : Path.Combine(AppPaths.ResourcesDirectory, "epg-zahaujlugl.xml");
        try
        {
            var loaded = await Task.Run(() => Epg.Read(path));
            if (closed) return;
            regionalEpg = loaded;
            AttachSupplementalEpg();
            UpdateGuide();
        }
        catch { }
    }

    private async Task LoadBcuEpg()
    {
        var path = File.Exists(EpgUpdater.BcuPath) ? EpgUpdater.BcuPath
            : Path.Combine(AppPaths.ResourcesDirectory, "epg (1).xml.gz");
        try
        {
            var loaded = await Task.Run(() => Epg.Read(path));
            if (closed) return;
            bcuEpg = loaded;
            AttachSupplementalEpg();
            UpdateGuide();
        }
        catch { }
    }

    private async Task LoadExtraEpg(bool iptvx)
    {
        var cache = iptvx ? EpgUpdater.IptvxPath : EpgUpdater.TvteamPath;
        var original = iptvx ? Path.Combine(AppPaths.ResourcesDirectory, "epg (2).xml.gz")
            : Path.Combine(AppPaths.ResourcesDirectory, "7.2.xml");
        if (iptvx && !File.Exists(original)) original = Path.Combine(AppPaths.ResourcesDirectory, "epg (2).xml", "epg.xml");
        try
        {
            var loaded = await Task.Run(() => Epg.Read(File.Exists(cache) ? cache : original));
            if (closed) return;
            if (iptvx) iptvxEpg = loaded; else tvteamEpg = loaded;
            AttachSupplementalEpg();
            UpdateGuide();
        }
        catch { }
    }

    private void AttachSupplementalEpg()
    {
        if (israelEpg is null && regionalEpg is null && bcuEpg is null && iptvxEpg is null && tvteamEpg is null) return;
        epg ??= new Epg();
        if (israelEpg is not null) epg.AddIsrael(israelEpg, channels);
        if (regionalEpg is not null) epg.AddRegional(regionalEpg, channels);
        if (bcuEpg is not null) epg.AddBcu(bcuEpg, channels);
        if (iptvxEpg is not null) epg.AddRegional(iptvxEpg, channels, "iptvx");
        if (tvteamEpg is not null) epg.AddRegional(tvteamEpg, channels, "tvteam");
    }

    private async Task<bool> LoadPlaylist(string path, List<Channel>? prepared = null)
    {
        var version = ++playlistLoadVersion;
        try
        {
            var loaded = prepared ?? await RemoteSource.ReadPlaylist(path, lifetime.Token);
            if (closed || version != playlistLoadVersion || loaded.Count == 0) return false;
            channels = loaded;
            AttachSupplementalEpg();
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
        var version = ++epgLoadVersion;
        try
        {
            Epg loaded;
            if (RemoteSource.IsUrl(path))
            {
                var cache = RemoteSource.CachePath(path, ".xml.gz");
                try { loaded = (await EpgUpdater.UpdateAsync(cache, cancellation: lifetime.Token, sourceUrl: path)).Guide; }
                catch when (!lifetime.IsCancellationRequested && File.Exists(cache)) { loaded = await Task.Run(() => Epg.Read(cache)); }
            }
            else loaded = await Task.Run(() => Epg.Read(path));
            if (closed || version != epgLoadVersion) return false;
            epg = loaded;
            primaryEpgLoaded = true;
            AttachSupplementalEpg();
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

    private void RefreshPlaylistSelector()
    {
        rebuildingPlaylists = true;
        try
        {
            PlaylistSelector.ItemsSource = settings.Playlists.ToArray();
            PlaylistSelector.SelectedItem = settings.Playlists.FirstOrDefault(p => p.Id == settings.ActivePlaylistId);
        }
        finally { rebuildingPlaylists = false; }
    }

    private void ClearPlaylistChannels()
    {
        ++playlistLoadVersion;
        player?.Stop();
        playingChannel = null;
        settings.ChannelName = settings.ChannelGroup = settings.SelectedGroup = null;
        channels = [];
        rebuildingFilters = true;
        try { Groups.ItemsSource = null; Search.Clear(); }
        finally { rebuildingFilters = false; }
        FilterChannels();
        UpdateGuide();
    }

    private async void PlaylistSelector_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (rebuildingPlaylists || PlaylistSelector.SelectedItem is not SavedPlaylist selected) return;
        settings.ActivePlaylistId = selected.Id;
        settings.PlaylistPath = selected.Source;
        ClearPlaylistChannels();
        Status.Text = "Загрузка плейлиста: " + selected.Name;
        var loaded = await LoadPlaylist(selected.Source);
        if (settings.ActivePlaylistId != selected.Id || closed) return;
        Status.Text = loaded ? $"{selected.Name}: {channels.Count} каналов." : "Не удалось загрузить выбранный плейлист.";
        SaveSettings();
    }

    private void Playlists_Click(object sender, RoutedEventArgs e)
    {
        var dialog = PlaylistManager.Create(this, settings.Playlists, ChangePlaylist);
        dialog.Owner = this;
        dialog.ShowDialog();
    }

    private async Task<bool> ChangePlaylist(SavedPlaylist? previous, SavedPlaylist? replacement)
    {
        List<Channel>? prepared = null;
        if (replacement is not null)
        {
            try { prepared = await RemoteSource.ReadPlaylist(replacement.Source, lifetime.Token); }
            catch (System.Net.Http.HttpRequestException error)
            {
                throw new InvalidOperationException(error.StatusCode is { } code
                    ? $"Сервер плейлиста вернул HTTP {(int)code}. Проверьте доступ к подписке."
                    : error.HttpRequestError switch
                    {
                        System.Net.Http.HttpRequestError.NameResolutionError => "Не удалось определить адрес сервера (DNS).",
                        System.Net.Http.HttpRequestError.SecureConnectionError => "Не удалось установить защищённое соединение с сервером (TLS).",
                        System.Net.Http.HttpRequestError.ProxyTunnelError => "Ошибка подключения через системный прокси.",
                        _ => $"Ошибка подключения к серверу: {error.HttpRequestError}" +
                            (error.InnerException is System.Net.Sockets.SocketException socket ? $" ({socket.SocketErrorCode})." : ".")
                    });
            }
            catch (OperationCanceledException) { throw new InvalidOperationException("Сервер плейлиста не ответил вовремя. Попробуйте ещё раз."); }
            catch (InvalidDataException error) { throw new InvalidOperationException(error.Message); }
            catch { throw new InvalidOperationException("Не удалось прочитать плейлист. Проверьте файл или ссылку."); }
            if (prepared.Count == 0 || closed) return false;
        }
        var wasActive = previous?.Id == settings.ActivePlaylistId;
        if (previous is not null) settings.Playlists.Remove(previous);
        if (replacement is not null) settings.Playlists.Add(replacement);
        if (replacement is not null || wasActive)
        {
            var selected = replacement ?? settings.Playlists.FirstOrDefault();
            settings.ActivePlaylistId = selected?.Id;
            settings.PlaylistPath = selected?.Source;
            ClearPlaylistChannels();
            RefreshPlaylistSelector();
            if (selected is not null) await LoadPlaylist(selected.Source, prepared);
            ChannelsColumn.Width = new GridLength(250);
        }
        else RefreshPlaylistSelector();
        SaveSettings();
        return true;
    }

    private async void OpenEpg_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "XMLTV|*.xml;*.gz", InitialDirectory = AppPaths.ResourcesDirectory };
        if (dialog.ShowDialog(this) != true) return;
        await OpenEpgSource(dialog.FileName);
    }

    private async void OpenEpgUrl_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteSource.Prompt(this, "EPG") is { } url) await OpenEpgSource(url);
    }

    private async Task OpenEpgSource(string source)
    {
        if (restoring) epgChosenDuringRestore = true;
        epgUpdateCancellation?.Cancel();
        OpenEpg.IsEnabled = RefreshEpg.IsEnabled = false;
        Status.Text = "Чтение программы передач…";
        try
        {
            if (!await LoadEpg(source)) { Status.Text = "Не удалось загрузить EPG. Проверьте файл или ссылку на XMLTV (XML или GZ)."; return; }
            Status.Text = $"EPG загружен. Сопоставлено каналов: {channels.Count(c => epg?.Match(c) is not null)} из {channels.Count}.";
            SaveSettings();
        }
        catch { Status.Text = "Не удалось прочитать EPG. Проверьте формат XMLTV и целостность архива."; }
        finally { if (!closed) OpenEpg.IsEnabled = RefreshEpg.IsEnabled = true; }
    }

    private async void RefreshEpg_Click(object sender, RoutedEventArgs e) => await UpdateEpg(staleOnly: false);

    private async Task UpdateEpg(bool staleOnly)
    {
        if (closed) return;
        if (updatingEpg)
        {
            Status.Text = "Обновление EPG уже выполняется.";
            return;
        }
        updatingEpg = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        epgUpdateCancellation = cancellation;
        var messages = new List<string>();
        IProgress<string> progress = new Progress<string>(message => { if (!closed) Status.Text = message; });
        try
        {
            foreach (var source in new[] { "main", "israel", "regional", "bcu", "iptvx", "tvteam" })
            {
                if (closed || cancellation.IsCancellationRequested) return;
                var mainUrl = RemoteSource.IsUrl(settings.EpgPath ?? "") ? settings.EpgPath! : "http://epg.one/epg2.xml.gz";
                var mainPath = RemoteSource.IsUrl(settings.EpgPath ?? "")
                    ? RemoteSource.CachePath(mainUrl, ".xml.gz") : EpgUpdater.TargetPath;
                var (path, url, name, cachedPath, loaded) = source switch
                {
                    "israel" => (EpgUpdater.IsraelPath, EpgUpdater.IsraelUrl, "Израиль",
                        File.Exists(EpgUpdater.IsraelPath) ? EpgUpdater.IsraelPath : Path.Combine(AppPaths.ResourcesDirectory, "epg-il.xml"), israelEpg is not null),
                    "regional" => (EpgUpdater.RegionalPath, EpgUpdater.RegionalUrl, "Дополнительный EPG",
                        File.Exists(EpgUpdater.RegionalPath) ? EpgUpdater.RegionalPath : Path.Combine(AppPaths.ResourcesDirectory, "epg-zahaujlugl.xml"), regionalEpg is not null),
                    "bcu" => (EpgUpdater.BcuPath, EpgUpdater.BcuUrl, "BCU Media",
                        File.Exists(EpgUpdater.BcuPath) ? EpgUpdater.BcuPath : Path.Combine(AppPaths.ResourcesDirectory, "epg (1).xml.gz"), bcuEpg is not null),
                    "iptvx" => (EpgUpdater.IptvxPath, EpgUpdater.IptvxUrl, "IPTVX.one",
                        File.Exists(EpgUpdater.IptvxPath) ? EpgUpdater.IptvxPath : Path.Combine(AppPaths.ResourcesDirectory, "epg (2).xml.gz"), iptvxEpg is not null),
                    "tvteam" => (EpgUpdater.TvteamPath, EpgUpdater.TvteamUrl, "TVTeam",
                        File.Exists(EpgUpdater.TvteamPath) ? EpgUpdater.TvteamPath : Path.Combine(AppPaths.ResourcesDirectory, "7.2.xml"), tvteamEpg is not null),
                    _ => (mainPath, mainUrl, "Основной EPG", RemoteSource.IsUrl(settings.EpgPath ?? "") ? mainPath : settings.EpgPath ?? mainPath, primaryEpgLoaded)
                };
                if (staleOnly && loaded && !EpgUpdater.NeedsUpdate(cachedPath, DateTimeOffset.UtcNow)) continue;
                try
                {
                    progress.Report($"Обновление: {name}…");
                    var result = await EpgUpdater.UpdateAsync(path, progress, cancellation.Token, url);
                    if (closed || cancellation.IsCancellationRequested) return;
                    if (source == "iptvx") iptvxEpg = result.Guide;
                    else if (source == "tvteam") tvteamEpg = result.Guide;
                    else if (source == "bcu") bcuEpg = result.Guide;
                    else if (source == "regional") regionalEpg = result.Guide;
                    else if (source == "israel") israelEpg = result.Guide;
                    else { epg = result.Guide; primaryEpgLoaded = true; if (!RemoteSource.IsUrl(settings.EpgPath ?? "")) settings.EpgPath = path; }
                    AttachSupplementalEpg();
                    UpdateGuide();
                    messages.Add($"{name}: обновлено ({result.Count:N0} передач).");
                    SaveSettings();
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
                catch { messages.Add($"{name}: обновить не удалось; используется сохранённая программа, если она доступна."); }
            }
        }
        finally
        {
            updatingEpg = false;
            epgUpdateCancellation = null;
            if (!closed)
            {
                OpenPlaylist.IsEnabled = OpenEpg.IsEnabled = RefreshEpg.IsEnabled = true;
                if (!cancellation.IsCancellationRequested && messages.Count > 0) Status.Text = string.Join(" ", messages);
            }
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
    private async void CheckAppUpdates_Click(object sender, RoutedEventArgs e) => await CheckAppUpdates(true);

    private async Task CheckAppUpdates(bool manual)
    {
        if (closed || checkingAppUpdate || installingAppUpdate) return;
        checkingAppUpdate = true;
        lastAppUpdateCheck = DateTimeOffset.UtcNow;
        CheckAppUpdatesMenu.IsEnabled = false;
        CheckAppUpdatesMenu.Header = "Проверка обновлений…";
        try
        {
            var current = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
            var release = await appUpdater.CheckAsync(current, lifetime.Token);
            if (closed) return;
            availableAppRelease = release;
            AppUpdateLink.Visibility = release is null ? Visibility.Collapsed : Visibility.Visible;
            if (release is not null) AppUpdateLabel.Text = $"Обновить до {release.Version.ToString(3)}";
            if (manual) Status.Text = release is null ? $"Установлена актуальная версия {current.ToString(3)}."
                : $"Доступна версия {release.Version.ToString(3)}. Нажмите ссылку рядом с «Каналы» для установки.";
            CheckAppUpdatesMenu.Header = "Проверить обновления плеера";
            CheckAppUpdatesMenu.ToolTip = release is null ? $"Текущая версия: {current.ToString(3)}" : $"Доступна версия {release.Version.ToString(3)}";
        }
        catch (OperationCanceledException) when (closed) { }
        catch (InvalidDataException error)
        {
            if (!closed)
            {
                CheckAppUpdatesMenu.Header = "Источник обновлений недоступен — повторить";
                if (manual) Status.Text = error.Message;
            }
        }
        catch
        {
            if (!closed)
            {
                CheckAppUpdatesMenu.Header = "Повторить проверку обновлений";
                if (manual) Status.Text = "Не удалось проверить обновления плеера. Проверьте подключение к интернету и доступность релизов GitHub.";
            }
        }
        finally
        {
            checkingAppUpdate = false;
            if (!closed) CheckAppUpdatesMenu.IsEnabled = true;
        }
    }

    private async void InstallAppUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (availableAppRelease is not { } release || installingAppUpdate || closed) return;
        installingAppUpdate = true;
        AppUpdateLink.IsEnabled = false;
        try
        {
            Status.Text = $"Скачивание версии {release.Version.ToString(3)}…";
            var directory = Path.Combine(AppPaths.DataDirectory, "updates", release.Version.ToString(3));
            var installer = await appUpdater.DownloadAsync(release, directory, lifetime.Token);
            if (closed) return;
            // Keep a settings snapshot as well as the unchanged user data directory.
            settings.Save(SettingsPath);
            File.Copy(SettingsPath, Path.Combine(directory, "player-settings.backup.json"), overwrite: true);
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installer)
            {
                UseShellExecute = true,
                Arguments = $"/DIR=\"{AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)}\" /CLOSEAPPLICATIONS"
            });
            if (process is null) throw new InvalidOperationException();
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException) when (closed) { }
        catch (InvalidDataException error) { if (!closed) Status.Text = error.Message; }
        catch { if (!closed) Status.Text = "Не удалось установить обновление. Текущая версия и данные сохранены; попробуйте ещё раз."; }
        finally
        {
            installingAppUpdate = false;
            if (!closed) AppUpdateLink.IsEnabled = true;
        }
    }

    private void SourceMenu_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is MenuItem { HasItems: true, IsEnabled: true } item) item.IsSubmenuOpen = true;
    }

    private void Sources_Click(object sender, RoutedEventArgs e)
    {
        SourcesButton.ContextMenu.PlacementTarget = SourcesButton;
        SourcesButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        SourcesButton.ContextMenu.IsOpen = true;
    }

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
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        var selected = Channels.SelectedItem as Channel;
        var channel = fullscreen ? playingChannel ?? selected : selected;
        if (channel is not null) PlayChannel(channel);
    }
    private void PlayChannel(Channel channel)
    {
        if (player is null || vlc is null) return;
        try
        {
            using var media = new Media(vlc, channel.Stream);
            if (player.Play(media))
            {
                playingChannel = channel;
                settings.ChannelName = channel.Name;
                settings.ChannelGroup = channel.Group;
                SaveSettings();
                PlaybackState.Text = "Подключение…";
                Status.Text = $"Воспроизведение: {channel.Name}";
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
