using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace LiquidSpotify;

public partial class MainWindow : Window
{
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    private GlobalSystemMediaTransportControlsSession? session;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer overlayTimer = new() { Interval = TimeSpan.FromSeconds(2.8) };
    private bool refreshing, seeking, playing, compact, artworkMode, closed, commandBusy;
    private int ticks;
    private Point lastMousePosition = new(-100, -100);
    private string mediaKey = "";
    private TimeSpan duration, position, start;
    private DateTimeOffset timelineUpdated;
    private double playbackRate = 1;
    private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiquidSpotify", "settings.json");
    private sealed record Settings(double Left, double Top, bool Pinned, bool Compact, bool Artwork = false);

    public MainWindow()
    {
        InitializeComponent();
        InitializeWindowSurface();
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 28; Top = area.Bottom - Height - 28;
        LoadSettings();
        SetControls(null);
        Activated += (_, _) => { if (IsFullScreen) Topmost = true; };
        Deactivated += (_, _) => { if (IsFullScreen) Topmost = false; };
        MouseEnter += (_, _) => RevealArtworkControls();
        MouseMove += (_, e) => { var point = e.GetPosition(this); if (artworkMode && (point - lastMousePosition).Length > 1) RevealArtworkControls(); lastMousePosition = point; };
        MouseLeave += (_, _) => { if (artworkMode) { overlayTimer.Stop(); overlayTimer.Start(); } };
        overlayTimer.Tick += (_, _) =>
        {
            overlayTimer.Stop();
            if (artworkMode && !lyricsOpen && !seeking)
            {
                Header.IsHitTestVisible = TrackPanel.IsHitTestVisible = ExpandedControls.IsHitTestVisible = false;
                FadeOverlay(Header, 0, 500); FadeOverlay(TrackPanel, 0, 500);
                FadeOverlay(ExpandedControls, 0, 500); FadeOverlay(CoverShade, 0.08, 650);
            }
        };
        SourceInitialized += (_, _) => NativeGlass.Apply(this);
        Loaded += async (_, _) => { NativeGlass.Apply(this); await Connect(); timer.Start(); };
        timer.Tick += async (_, _) => { UpdateProgress(); if (++ticks % 10 == 0) await Refresh(); };
        Closed += (_, _) => { closed = true; timer.Stop(); overlayTimer.Stop(); lyricsView?.CancelLookup(); Unsubscribe(); if (manager != null) manager.SessionsChanged -= SessionsChanged; SaveSettings(); };
        PreviewKeyDown += async (_, e) =>
        {
            if (HandleLyricsKey(e)) return;
            RevealArtworkControls();
            if (e.Key == Key.Space && e.OriginalSource is not Slider) { e.Handled = true; await Send(s => s.TryTogglePlayPauseAsync()); }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Right) { e.Handled = true; await Send(s => s.TrySkipNextAsync()); }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Left) { e.Handled = true; await Send(s => s.TrySkipPreviousAsync()); }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.M) { e.Handled = true; ToggleCompact(this, new RoutedEventArgs()); }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.E) { e.Handled = true; ToggleArtwork(this, new RoutedEventArgs()); }
            else if (e.Key == Key.Escape && artworkMode) { e.Handled = true; SetViewMode("glass"); }
        };
    }

    private async Task Connect()
    {
        try
        {
            manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            manager.SessionsChanged -= SessionsChanged; manager.SessionsChanged += SessionsChanged;
            await Refresh();
        }
        catch { Status.Text = "Windows media session unavailable · retrying"; }
    }
    private void SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => ScheduleRefresh();
    private void MediaChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) { mediaKey = ""; ScheduleRefresh(); }
    private void PlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => ScheduleRefresh();
    private void TimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => ScheduleRefresh();
    private void ScheduleRefresh()
    {
        if (!closed) Dispatcher.BeginInvoke(async () => await Refresh());
    }
    private void Unsubscribe()
    {
        if (session == null) return;
        session.MediaPropertiesChanged -= MediaChanged;
        session.PlaybackInfoChanged -= PlaybackChanged;
        session.TimelinePropertiesChanged -= TimelineChanged;
    }

    private async Task Refresh()
    {
        if (refreshing || closed) return;
        if (manager == null) { await Connect(); return; }
        refreshing = true;
        try
        {
            var sessions = manager.GetSessions().Where(s => s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase)).ToList();
            var selected = sessions.FirstOrDefault(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) ?? sessions.FirstOrDefault();
            if (selected == null) { Disconnected(); return; }
            if (session == null || session.SourceAppUserModelId != selected.SourceAppUserModelId || !session.Equals(selected))
            {
                Unsubscribe(); session = selected; mediaKey = "";
                session.MediaPropertiesChanged += MediaChanged;
                session.PlaybackInfoChanged += PlaybackChanged;
                session.TimelinePropertiesChanged += TimelineChanged;
            }
            var current = session;
            var p = await current.TryGetMediaPropertiesAsync();
            if (closed || session != current) return;
            var key = p.Title + "\n" + p.Artist + "\n" + p.AlbumTitle;
            if (mediaKey != key)
            {
                mediaKey = key;
                TrackTitle.Text = string.IsNullOrWhiteSpace(p.Title) ? "Spotify" : p.Title;
                Artist.Text = string.IsNullOrWhiteSpace(p.Artist) ? "Spotify desktop" : p.Artist;
                Album.Text = p.AlbumTitle;
                TrackTitle.ToolTip = p.Title; Artist.ToolTip = p.Artist; Album.ToolTip = p.AlbumTitle;
                Artwork.ImageSource = null; Placeholder.Visibility = Visibility.Visible;
                if (p.Thumbnail != null)
                {
                    try
                    {
                        using var stream = await p.Thumbnail.OpenReadAsync();
                        if (stream.Size > 0 && stream.Size <= 20 * 1024 * 1024)
                        {
                            using var reader = new DataReader(stream.GetInputStreamAt(0));
                            await reader.LoadAsync((uint)stream.Size);
                            var bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
                            using var memory = new MemoryStream(bytes);
                            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 800; bitmap.StreamSource = memory; bitmap.EndInit(); bitmap.Freeze();
                            if (session == current && mediaKey == key && !closed)
                            {
                                var cover = ExtractAlbumCover(bitmap);
                                Artwork.ImageSource = FullArtwork.ImageSource = cover;
                                Placeholder.Visibility = Visibility.Collapsed; Tint(cover);
                                CoverCanvas.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)));
                            }
                        }
                    }
                    catch { /* Artwork failure must not break playback controls. */ }
                }
            }
            var info = current.GetPlaybackInfo();
            playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            playbackRate = info.PlaybackRate ?? 1;
            PlayGlyph.Data = CompactPlayGlyph.Data = (Geometry)FindResource(playing ? "PauseGeometry" : "PlayGeometry");
            PlayButton.ToolTip = CompactPlay.ToolTip = playing ? "Pause (Space)" : "Play (Space)";
            System.Windows.Automation.AutomationProperties.SetName(PlayButton, playing ? "Pause" : "Play");
            SetControls(info);
            var timeline = current.GetTimelineProperties();
            start = timeline.StartTime;
            duration = timeline.EndTime - start;
            position = timeline.Position;
            timelineUpdated = timeline.LastUpdatedTime;
            if (timelineUpdated > DateTimeOffset.UtcNow) timelineUpdated = DateTimeOffset.UtcNow;
            Progress.IsEnabled = info.Controls.IsPlaybackPositionEnabled && duration.TotalSeconds > 0;
            Duration.Text = duration.TotalSeconds > 0 ? Format(duration) : "–:––";
            Status.Text = playing ? "Playing" : "Paused";
            Status.ToolTip = "Connected locally through Windows media controls. No Spotify API.";
            StatusDot.Fill = Brush("#9CF5C4");
            UpdateProgress();
        }
        catch { Disconnected(); Status.Text = "Reconnecting to Spotify…"; }
        finally { refreshing = false; }
    }

    private void SetControls(GlobalSystemMediaTransportControlsSessionPlaybackInfo? info)
    {
        var c = info?.Controls;
        PlayButton.IsEnabled = CompactPlay.IsEnabled = c != null && (playing ? c.IsPauseEnabled : c.IsPlayEnabled);
        NextButton.IsEnabled = c?.IsNextEnabled == true;
        PreviousButton.IsEnabled = c?.IsPreviousEnabled == true;
        ShuffleButton.IsEnabled = c?.IsShuffleEnabled == true;
        RepeatButton.IsEnabled = c?.IsRepeatEnabled == true;
        ShuffleButton.Foreground = Brush(info?.IsShuffleActive == true ? "#9CF5C4" : "#AAB6CF");
        RepeatButton.Foreground = Brush(info?.AutoRepeatMode is MediaPlaybackAutoRepeatMode.List or MediaPlaybackAutoRepeatMode.Track ? "#9CF5C4" : "#AAB6CF");
        RepeatOne.Visibility = info?.AutoRepeatMode == MediaPlaybackAutoRepeatMode.Track ? Visibility.Visible : Visibility.Collapsed;
        ShuffleButton.ToolTip = ShuffleButton.IsEnabled ? "Shuffle" : "Spotify does not expose shuffle to Windows";
        RepeatButton.ToolTip = RepeatButton.IsEnabled ? "Repeat" : "Spotify does not expose repeat to Windows";
    }
    private void Disconnected()
    {
        Unsubscribe(); session = null; mediaKey = ""; playing = false; duration = TimeSpan.Zero;
        SetControls(null); Progress.IsEnabled = false; Progress.Value = 0;
        Elapsed.Text = "0:00"; Duration.Text = "–:––";
        TrackTitle.Text = "No music playing"; Artist.Text = "Open Spotify to get started."; Album.Text = "";
        TrackTitle.ToolTip = null; Artist.ToolTip = null; Album.ToolTip = null;
        Artwork.ImageSource = FullArtwork.ImageSource = null; Placeholder.Visibility = Visibility.Visible;
        GlowColor.Color = (Color)ColorConverter.ConvertFromString("#605F82B7");
        PlayGlyph.Data = CompactPlayGlyph.Data = (Geometry)FindResource("PlayGeometry");
        Status.Text = "Waiting for Spotify desktop"; StatusDot.Fill = Brush("#8691A9");
        if (lyricsOpen) lyricsView?.Update(Snapshot());
    }
    private void UpdateProgress()
    {
        if (seeking || session == null) return;
        var elapsed = position - start;
        if (playing && timelineUpdated != default) elapsed += TimeSpan.FromSeconds(Math.Max(0, (DateTimeOffset.UtcNow - timelineUpdated).TotalSeconds) * playbackRate);
        elapsed = TimeSpan.FromSeconds(Math.Clamp(elapsed.TotalSeconds, 0, Math.Max(0, duration.TotalSeconds)));
        Progress.Maximum = Math.Max(1, duration.TotalSeconds); Progress.Value = elapsed.TotalSeconds;
        Elapsed.Text = Format(elapsed);
        if (lyricsOpen) lyricsView?.Update(Snapshot(elapsed.TotalSeconds));
    }
    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static BitmapSource ExtractAlbumCover(BitmapSource bitmap)
    {
        // Recognize the white Spotify wordmark in its Windows media-card layout.
        // Matching the brand mask avoids trimming intentional borders in album art.
        var w = bitmap.PixelWidth; var h = bitmap.PixelHeight;
        if (w < 80 || Math.Abs(w - h) > w * 0.02) return bitmap;
        var pixels = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[w * h * 4]; pixels.CopyPixels(bytes, w * 4, 0);
        var signature = Convert.FromBase64String("AAAAAAAA4AcAAAAA+B8AAAAA+B8MAMgADD4fAOsA/DPz+/85HD7+f2s9/Dt5TmsH/D7/e28G/B/+e28C+B9gAAAD8A8AAAAA");
        var matches = 0;
        for (var y = 0; y < 12; y++)
        for (var x = 0; x < 48; x++)
        {
            var px = (int)Math.Round(w * (0.15 + (x + 0.5) / 48 * 0.7));
            var py = (int)Math.Round(h * (0.81 + (y + 0.5) / 12 * 0.18));
            var index = (py * w + px) * 4;
            var white = bytes[index] > 205 && bytes[index + 1] > 205 && bytes[index + 2] > 205;
            var bit = y * 48 + x;
            var expectedWhite = (signature[bit / 8] & (1 << (bit % 8))) != 0;
            if (white == expectedWhite) matches++;
        }
        if (matches < 576 * 0.91) return bitmap;
        var side = (int)Math.Round(w * 0.778);
        var cover = new CroppedBitmap(bitmap, new Int32Rect((w - side) / 2, 0, side, side));
        cover.Freeze(); return cover;
    }
    private void Tint(BitmapSource bitmap)
    {
        var small = new TransformedBitmap(bitmap, new ScaleTransform(12.0 / bitmap.PixelWidth, 12.0 / bitmap.PixelHeight));
        var converted = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        if (pixels.Length == 0) return;
        long r = 0, g = 0, b = 0;
        for (var i = 0; i < pixels.Length; i += 4) { b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2]; }
        var n = pixels.Length / 4;
        GlowColor.Color = Color.FromArgb(58, (byte)Math.Clamp(r / n + 25, 0, 255), (byte)Math.Clamp(g / n + 25, 0, 255), (byte)Math.Clamp(b / n + 25, 0, 255));
    }
    private async Task Send(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> command)
    {
        if (session == null || commandBusy) return;
        commandBusy = true;
        try { if (!await command(session)) Status.Text = "Spotify could not complete that action"; }
        catch { Status.Text = "Spotify disconnected · reconnecting"; }
        finally { commandBusy = false; }
        await Task.Delay(160); await Refresh();
    }
    private async void PlayPause(object sender, RoutedEventArgs e) => await Send(s => s.TryTogglePlayPauseAsync());
    private async void Previous(object sender, RoutedEventArgs e) => await Send(s => s.TrySkipPreviousAsync());
    private async void Next(object sender, RoutedEventArgs e) => await Send(s => s.TrySkipNextAsync());
    private async void Shuffle(object sender, RoutedEventArgs e) => await Send(s => s.TryChangeShuffleActiveAsync(!(s.GetPlaybackInfo().IsShuffleActive ?? false)));
    private async void Repeat(object sender, RoutedEventArgs e)
    {
        var current = session?.GetPlaybackInfo().AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None;
        var next = current == MediaPlaybackAutoRepeatMode.None ? MediaPlaybackAutoRepeatMode.List : current == MediaPlaybackAutoRepeatMode.List ? MediaPlaybackAutoRepeatMode.Track : MediaPlaybackAutoRepeatMode.None;
        await Send(s => s.TryChangeAutoRepeatModeAsync(next));
    }
    private void SeekStarted(object sender, MouseButtonEventArgs e) { if (Progress.IsEnabled) seeking = true; }
    private async void SeekEnded(object sender, MouseButtonEventArgs e)
    {
        if (!seeking) return;
        var target = start + TimeSpan.FromSeconds(Progress.Value); seeking = false;
        await Send(s => s.TryChangePlaybackPositionAsync(target.Ticks));
    }
    private async void SeekKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right && Progress.IsEnabled && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            var value = Math.Clamp(Progress.Value + (e.Key == Key.Left ? -5 : 5), 0, duration.TotalSeconds);
            await Send(s => s.TryChangePlaybackPositionAsync((start + TimeSpan.FromSeconds(value)).Ticks));
        }
    }
    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element != null)
        {
            if (element is Button or Slider || element == Thumbnail) return;
            element = VisualTreeHelper.GetParent(element);
        }
        if (e.ClickCount == 2) { ToggleArtwork(sender, e); return; }
        DragMiniWindow(e);
    }
    private void ToggleCompact(object sender, RoutedEventArgs e) => SetViewMode(compact ? "glass" : "compact");
    private void ToggleArtwork(object sender, RoutedEventArgs e) => SetViewMode(artworkMode ? "glass" : "artwork");
    private void ThumbnailClick(object sender, MouseButtonEventArgs e) { e.Handled = true; SetViewMode("artwork"); }
    public void SetViewMode(string mode, bool animate = true)
    {
        if (lyricsOpen) CloseLyrics();
        var anchor = motionRunning ? sceneTargetBounds : new Rect(Left, Top, Width, Height);
        var animated = BeginSceneTransition(animate);
        artworkMode = mode == "artwork"; compact = mode == "compact";
        var targetWidth = artworkMode ? 388.0 : 408.0;
        var targetHeight = artworkMode ? 388.0 : compact ? 124.0 : 268.0;
        var targetLeft = anchor.Left;
        var targetTop = anchor.Top;
        CoverCanvas.Visibility = CoverShade.Visibility = artworkMode ? Visibility.Visible : Visibility.Collapsed;
        Thumbnail.Visibility = artworkMode ? Visibility.Collapsed : Visibility.Visible;
        ThumbnailColumn.Width = new GridLength(artworkMode ? 0 : compact ? 70 : 90);
        Thumbnail.Width = Thumbnail.Height = compact ? 58 : 78;
        Album.Visibility = compact || artworkMode ? Visibility.Collapsed : Visibility.Visible;
        CoverEyebrow.Visibility = artworkMode ? Visibility.Visible : Visibility.Collapsed;
        TrackTitle.FontSize = artworkMode ? 23 : compact ? 16 : 20;
        Artist.FontSize = artworkMode ? 13 : 12;
        Artist.Foreground = Brush(artworkMode ? "#DEE5EF" : "#B7C0CC");
        TrackRow.Height = new GridLength(artworkMode ? 90 : compact ? 70 : 96);
        SpacerRow.Height = artworkMode ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ControlsRow.Height = artworkMode ? new GridLength(95) : compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ExpandedControls.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactPlay.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        CompactControlColumn.Width = new GridLength(compact ? 44 : 0);
        Footer.Visibility = artworkMode ? Visibility.Collapsed : Visibility.Visible;
        FooterRow.Height = artworkMode ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        LayoutRoot.Margin = artworkMode ? new Thickness(24, 14, 24, 18) : new Thickness(22, 12, 22, compact ? 10 : 14);
        ArtworkButton.Background = artworkMode ? Brush("#30FFFFFF") : Brushes.Transparent;
        ArtworkButton.BorderBrush = artworkMode ? Brush("#35FFFFFF") : Brushes.Transparent;
        ArtworkButton.ToolTip = artworkMode ? "Return to glass player (Esc)" : "Artwork view (Ctrl+E)";
        CompactButton.ToolTip = compact ? "Return to glass player (Ctrl+M)" : "Compact mode (Ctrl+M)";
        System.Windows.Automation.AutomationProperties.SetName(ArtworkButton, artworkMode ? "Exit artwork view" : "Enter artwork view");
        lastPlayerBounds = new Rect(targetLeft, targetTop, targetWidth, targetHeight);
        FinishSceneTransition(lastPlayerBounds, animated);
        RevealArtworkControls();
    }
    private static void FadeOverlay(UIElement element, double opacity, int milliseconds)
    {
        if (Math.Abs(element.Opacity - opacity) < 0.001) return;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(element.Opacity, opacity, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
    private void RevealArtworkControls()
    {
        overlayTimer.Stop();
        Header.IsHitTestVisible = TrackPanel.IsHitTestVisible = ExpandedControls.IsHitTestVisible = true;
        FadeOverlay(Header, 1, 160); FadeOverlay(TrackPanel, 1, 160);
        FadeOverlay(ExpandedControls, 1, 160); FadeOverlay(CoverShade, 1, 180);
        if (artworkMode && !lyricsOpen) overlayTimer.Start();
    }
    private void TogglePin(object sender, RoutedEventArgs e) { Topmost = !Topmost; UpdatePin(); }
    private void UpdatePin() { PinButton.Foreground = Brush(Topmost ? "#A4F5CB" : "#AAB6CF"); PinButton.ToolTip = "Always on top: " + (Topmost ? "on" : "off"); }
    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OpenPlayerMenu(object sender, RoutedEventArgs e) { PlayerMore.ContextMenu!.PlacementTarget = PlayerMore; PlayerMore.ContextMenu.IsOpen = true; }
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    private void OpenSpotify(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true }); }
        catch { Status.Text = "Open the Spotify desktop app to connect"; }
    }
    private void LoadSettings()
    {
        try
        {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath));
            if (s == null || !double.IsFinite(s.Left) || !double.IsFinite(s.Top)) return;
            Topmost = s.Pinned;
            SetViewMode(s.Artwork ? "artwork" : s.Compact ? "compact" : "glass", false);
            Left = s.Left; Top = s.Top; UpdatePin();
        }
        catch { }
    }
    private void SaveSettings()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!); File.WriteAllText(settingsPath, JsonSerializer.Serialize(new Settings(IsFullScreen ? smallLyricsBounds.Left : Left, IsFullScreen ? smallLyricsBounds.Top : Top, lyricsOpen ? returnPinned : Topmost, compact, artworkMode))); } catch { }
    }
    public void SavePreview(string path)
    {
        var image = new RenderTargetBitmap((int)ActualWidth * 2, (int)ActualHeight * 2, 192, 192, PixelFormats.Pbgra32); image.Render(this);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); png.Save(file);
        if (lyricsOpen) File.WriteAllText(path + ".qa.json", JsonSerializer.Serialize(new { Left, Top, ActualWidth, ActualHeight, IsFullScreen, font = FontFamily.Source, lyrics = lyricsView?.Diagnostics() }, new JsonSerializerOptions { WriteIndented = true }));
    }
    public void SaveArtwork(string path)
    {
        if (FullArtwork.ImageSource is not BitmapSource bitmap) return;
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); png.Save(file);
    }
}


