using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IptvPlayer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Task.Run(SourceChecks.Run).GetAwaiter().GetResult();
        Task.Run(AppUpdateChecks.Run).GetAwaiter().GetResult();
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow();
        object Named(string name) => window.FindName(name) ?? throw new Exception("Missing control: " + name);
        T Field<T>(string name) => (T)(typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
        void Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var channels = (ColumnDefinition)Named("ChannelsColumn");
        var guide = (ColumnDefinition)Named("GuideColumn");
        var gap = (ColumnDefinition)Named("GuideGapColumn");
        var controls = (Border)Named("ControlsPanel");
        var normalHost = (ContentControl)Named("WindowControlsHost");
        var fullscreenHost = (ContentControl)Named("FullscreenControlsHost");
        var root = (DockPanel)Named("RootDock");
        try
        {
            var sources = (Button)Named("SourcesButton");
            Call("Sources_Click", sources, new RoutedEventArgs());
            Check(sources.ContextMenu.IsOpen && sources.ContextMenu.Items.Count == 4, "Sources button opens playlist, EPG and app update actions");
            Check(((Button)Named("AppUpdateLink")).Visibility == Visibility.Collapsed && Named("CheckAppUpdatesMenu") is MenuItem,
                "Update link is hidden until a new release is found");
            Check(ReferenceEquals(Named("OpenPlaylist"), sources.ContextMenu.Items[0]) &&
                ReferenceEquals(Named("OpenEpg"), sources.ContextMenu.Items[1]) &&
                ReferenceEquals(Named("RefreshEpg"), sources.ContextMenu.Items[2]), "Existing actions are accessible in the menu");
            sources.ContextMenu.IsOpen = false;
            Check(((MenuItem)Named("OpenPlaylist")).Header.ToString() == "Плейлисты" && ((MenuItem)Named("OpenEpg")).Items.Count == 2,
                "Playlist management replaces the old M3U submenu");
            typeof(MainWindow).GetField("updatingEpg", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            Call("RefreshEpg_Click", null, new RoutedEventArgs());
            Check(((MenuItem)Named("OpenPlaylist")).IsEnabled && ((MenuItem)Named("OpenEpg")).IsEnabled &&
                ((MenuItem)Named("RefreshEpg")).IsEnabled, "An ongoing update leaves source actions accessible");
            Check(((TextBlock)Named("Status")).Text == "Обновление EPG уже выполняется.", "Repeated refresh explains the ongoing update");
            typeof(MainWindow).GetField("updatingEpg", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
            var settings = Field<PlayerSettings>("settings");
            PlaylistChecks.Run(window);
            settings.ChannelName = "Last played";
            settings.ChannelGroup = "Saved group";
            var list = (ListBox)Named("Channels");
            list.ItemsSource = new[] { new Channel { Name = "Only highlighted", Group = "Other group", Stream = new Uri("https://example.invalid/test") } };
            list.SelectedIndex = 0;
            Check(!((Button)Named("ArchiveToggle")).IsEnabled, "Archive disabled for live-only channel");
            list.ItemsSource = new[] { new Channel { Name = "Archive channel", ArchiveDays = 7, Stream = new Uri("https://example.invalid/test") } };
            list.SelectedIndex = 0;
            Check(((Button)Named("ArchiveToggle")).IsEnabled, "Archive enabled for supported channel");
            Call("ArchiveToggle_Click", null, new RoutedEventArgs());
            Check(((ComboBox)Named("ArchiveDate")).Visibility == Visibility.Visible && ((ComboBox)Named("ArchiveDate")).Items.Count == 8, "Archive days fit existing guide");
            Call("ArchiveToggle_Click", null, new RoutedEventArgs());
            Check(((ComboBox)Named("ArchiveDate")).Visibility == Visibility.Collapsed, "Programme view restored");
            Check(settings.ChannelName == "Last played" && settings.ChannelGroup == "Saved group", "Highlighting a channel preserves the last played channel");
            Check(guide.Width.Value == 350, "Guide starts open");
            Call("ToggleGuide_Click", null, new RoutedEventArgs());
            Check(guide.Width.Value == 0 && gap.Width.Value == 0, "Guide collapses including gap");
            Call("ToggleGuide_Click", null, new RoutedEventArgs());
            Check(guide.Width.Value == 350 && gap.Width.Value == 12, "Guide reopens");
            Call("ToggleChannels_Click", null, new RoutedEventArgs());
            var mediaPlayer = Field<object?>("player");
            Check(mediaPlayer is not null, "Native media player initialized");

            foreach (var guideInitiallyOpen in new[] { true, false })
            {
                if (!guideInitiallyOpen) Call("ToggleGuide_Click", null, new RoutedEventArgs());
                if (!guideInitiallyOpen) window.WindowState = WindowState.Maximized;
                var stateBefore = window.WindowState;
                var channelsBefore = channels.Width;
                var guideBefore = guide.Width;
                Call("ToggleFullscreen");
                Check(Field<bool>("fullscreen") && window.WindowStyle == WindowStyle.None, "Borderless fullscreen");
                Check(root.Margin == new Thickness(0) && ((Grid)Named("TopBar")).Visibility == Visibility.Collapsed && ((TextBlock)Named("Status")).Visibility == Visibility.Collapsed, "No window chrome in fullscreen");
                Check(channels.Width.Value == 0 && guide.Width.Value == 0 && gap.Width.Value == 0, "Sidebars do not reserve video space");
                root.Measure(new Size(1280, 720));
                root.Arrange(new Rect(0, 0, 1280, 720));
                root.UpdateLayout();
                var video = (FrameworkElement)Named("Video");
                var origin = video.TranslatePoint(new Point(0, 0), root);
                Check(Math.Abs(video.ActualWidth - 1280) < 1 && Math.Abs(video.ActualHeight - 720) < 1 && origin.X == 0 && origin.Y == 0, "Video fills the entire fullscreen viewport");
                Check(normalHost.Content is null && ReferenceEquals(fullscreenHost.Content, controls), "Same playback controls move over video");
                Check(ReferenceEquals(mediaPlayer, Field<object?>("player")), "Fullscreen keeps same media player");

                Call("ControlsIdle_Tick", null, EventArgs.Empty);
                Check(fullscreenHost.Visibility == Visibility.Collapsed && ((Grid)Named("VideoOverlay")).Cursor == Cursors.None, "Idle hides controls and cursor");
                Call("ShowFullscreenControls");
                Check(fullscreenHost.Visibility == Visibility.Visible, "Controls can reappear after hiding");
                Call("ToggleFullscreen");
                Check(!Field<bool>("fullscreen") && root.Margin == new Thickness(18) && window.WindowState == stateBefore, "Exit restores previous window state");
                Check(channels.Width == channelsBefore && guide.Width == guideBefore, "Exit restores panel visibility");
                Check(ReferenceEquals(normalHost.Content, controls) && fullscreenHost.Content is null, "Controls restored without duplication");
                Check(!Field<System.Windows.Threading.DispatcherTimer>("controlsIdle").IsEnabled, "Exit stops idle timer");
            }
            Console.WriteLine("Проверки сворачивания EPG, полного экрана, скрытия и восстановления панели: OK");
        }
        finally { window.Close(); application.Shutdown(); }
    }
}
