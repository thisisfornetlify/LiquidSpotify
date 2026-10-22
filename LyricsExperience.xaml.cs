using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LiquidSpotify;

public partial class LyricsExperience : UserControl
{
    private readonly MainWindow player;
    private CancellationTokenSource? lookup;
    private string requestKey = "";
    private IReadOnlyList<LyricLine> lines = [];
    private readonly List<Button> lineButtons = [];
    private bool synced, following = true, seeking, fullscreen;
    private int active = -2;
    private double offset, seconds;
    private NowPlayingSnapshot? current;
    public static readonly DependencyProperty ScrollPositionProperty = DependencyProperty.Register(
        nameof(ScrollPosition), typeof(double), typeof(LyricsExperience),
        new PropertyMetadata(0.0, (d, e) => ((LyricsExperience)d).LyricsScroll.ScrollToVerticalOffset((double)e.NewValue)));
    public double ScrollPosition { get => (double)GetValue(ScrollPositionProperty); set => SetValue(ScrollPositionProperty, value); }

    public LyricsExperience(MainWindow player)
    {
        InitializeComponent(); this.player = player;
        AmbientArt.CacheMode = new BitmapCache(0.5);
        AlbumCard.CacheMode = new BitmapCache();
        SizeChanged += (_, _) => ResizeScene();
    }
    public void PrepareOpen() { if (lines.Count == 0) requestKey = ""; }
    public void SetFullscreen(bool value)
    {
        fullscreen = value;
        AmbientArt.Opacity = value ? 0.32 : 0.18;
        FullPlayer.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        MiniInfo.Visibility = MiniTransport.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        MiniInfoRow.Height = new GridLength(value ? 0 : 70);
        MiniTransportRow.Height = new GridLength(value ? 0 : 90);
        PlayerColumn.Width = value ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        BodyGap.Width = new GridLength(value ? 70 : 0);
        SceneLayout.Margin = value ? new Thickness(52, 26, 52, 30) : new Thickness(24, 16, 24, 18);
        ScreenButton.ToolTip = value ? "Exit fullscreen (F11 / Esc)" : "Fullscreen (F11)";
        ResizeScene();
    }
    private void ResizeScene()
    {
        if (ActualHeight < 1) return;
        var coverSize = fullscreen ? Math.Clamp(ActualHeight - 390, 160, Math.Min(420, Math.Max(180, ActualWidth * 0.32))) : 360;
        FullPlayer.Width = AlbumCard.Width = AlbumCard.Height = coverSize;
        var size = fullscreen ? Math.Clamp(ActualHeight / 22.0, 28, 48) : 28;
        foreach (var button in lineButtons)
            if (button.Content is TextBlock text) { text.FontSize = size; text.LineHeight = size * 1.22; }
        LyricsLines.Margin = new Thickness(0, fullscreen ? Math.Max(80, ActualHeight * 0.22) : 80, 0, fullscreen ? ActualHeight * 0.35 : 170);
        if (following && synced) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { LyricsScroll.UpdateLayout(); ScrollToActive(false); });
    }
    internal void Update(NowPlayingSnapshot state)
    {
        current = state; seconds = state.Position;
        MiniTitle.Text = FullTitle.Text = state.Title;
        MiniArtist.Text = FullArtist.Text = state.Artist;
        if (!ReferenceEquals(AmbientArt.Source, state.Artwork))
        {
            AmbientArt.Source = state.Artwork;
            MiniCover.ImageSource = LargeCover.ImageSource = state.Artwork;
        }
        MiniPlayGlyph.Data = FullPlayGlyph.Data = (Geometry)FindResource(state.Playing ? "PauseGeometry" : "PlayGeometry");
        MiniPlay.IsEnabled = FullPlay.IsEnabled = state.CanPlay;
        MiniSeek.IsEnabled = FullSeek.IsEnabled = state.CanSeek && state.Duration > 0;
        MiniDuration.Text = FullDuration.Text = Format(state.Duration);
        if (!seeking)
        {
            MiniSeek.Maximum = FullSeek.Maximum = Math.Max(1, state.Duration);
            MiniSeek.Value = FullSeek.Value = Math.Clamp(state.Position, 0, Math.Max(0, state.Duration));
            MiniElapsed.Text = FullElapsed.Text = Format(state.Position);
        }
        if (requestKey != state.Key)
        {
            requestKey = state.Key;
            offset = 0; OffsetLabel.Text = "0.0s";
            if (!state.Connected)
            {
                CancelLookup(); lines = []; RenderLines();
                SetEmpty("No music playing", "Open Spotify to view lyrics.");
            }
            else _ = Lookup(state);
        }
        UpdateActive();
    }
    private async Task Lookup(NowPlayingSnapshot state)
    {
        CancelLookup(); lookup = new CancellationTokenSource(); var token = lookup.Token;
        lines = []; synced = false; active = -2; RenderLines();
        SetEmpty("Loading lyrics…", "");
        SourceLabel.Text = "Looking up lyrics on LRCLIB…";
        try
        {
            var result = await LyricsService.Find(state.Title, state.Artist, state.Album, state.Duration, token);
            if (token.IsCancellationRequested || requestKey != state.Key) return;
            ApplyResult(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (requestKey != state.Key || token.IsCancellationRequested) return;
            SetEmpty("Couldn't load lyrics", "Retry or open a local lyrics file from the menu.");
            SourceLabel.Text = "Lyrics connection unavailable";
        }
    }
    private void ApplyResult(LyricResult result)
    {
        lines = result.Lines; synced = result.Synced; following = true; active = -2;
        FollowButton.Content = "Follow lyrics"; FollowButton.Visibility = Visibility.Collapsed;
        FollowButton.IsEnabled = synced;
        SourceLabel.Text = result.Status;
        RenderLines();
        if (lines.Count == 0) SetEmpty(result.Status.StartsWith("Instrumental") ? "Instrumental" : "Lyrics unavailable", result.Status);
        else { EmptyState.Visibility = Visibility.Collapsed; LyricsScroll.Visibility = Visibility.Visible; }
        UpdateActive();
    }
    private void SetEmpty(string title, string description)
    {
        EmptyTitle.Text = title; EmptyDescription.Text = description;
        EmptyState.Visibility = Visibility.Visible;
        LyricsScroll.Visibility = Visibility.Collapsed;
        FollowButton.IsEnabled = false;
    }
    private void RenderLines()
    {
        BeginAnimation(ScrollPositionProperty, null); LyricsScroll.ScrollToTop();
        LyricsLines.Children.Clear(); lineButtons.Clear();
        for (var i = 0; i < lines.Count; i++)
        {
            var index = i;
            var text = new TextBlock { Text = lines[i].Text, TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.Bold, FontSize = fullscreen ? 40 : 28, LineHeight = fullscreen ? 49 : 35,
                Foreground = Brushes.White };
            var button = new Button { Content = text, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 10, 8, 10), Margin = new Thickness(0, 3, 0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Focusable = synced, Cursor = synced ? Cursors.Hand : Cursors.Arrow,
                RenderTransformOrigin = new Point(0, 0.5), RenderTransform = new ScaleTransform(0.98, 0.98) };
            var template = new ControlTemplate(typeof(Button));
            var surface = new FrameworkElementFactory(typeof(Border)); surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            surface.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(PaddingProperty));
            surface.AppendChild(presenter); template.VisualTree = surface; button.Template = template;
            button.ToolTip = synced ? "Jump to this line" : null;
            button.MouseEnter += (_, _) => button.Background = new SolidColorBrush(Color.FromArgb(14, 255, 255, 255));
            button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
            button.Click += async (_, e) => { e.Handled = true; if (synced && current?.CanSeek == true) { following = true; FollowButton.Visibility = Visibility.Collapsed; await player.SeekTo(lines[index].Time - offset); } };
            lineButtons.Add(button); LyricsLines.Children.Add(button);
        }
        ResizeScene();
    }
    private void UpdateActive()
    {
        if (lines.Count == 0) return;
        var index = -1;
        if (synced) for (var i = 0; i < lines.Count && lines[i].Time <= seconds + offset; i++) index = i;
        if (active == index) return;
        active = index;
        for (var i = 0; i < lineButtons.Count; i++)
        {
            var selected = i == active;
            var button = lineButtons[i];
            var opacity = !synced || selected ? 1 : i < active ? 0.32 : 0.36;
            if (Math.Abs(button.Opacity - opacity) > 0.01) button.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(340)));
            if (button.Content is TextBlock text)
            {
                var soften = synced && !selected && Math.Abs(i - active) > 1;
                if (soften && text.Effect == null) text.Effect = new BlurEffect { Radius = fullscreen ? 1.2 : 0.65, RenderingBias = RenderingBias.Performance };
                else if (!soften) text.Effect = null;
            }
            var transform = (ScaleTransform)button.RenderTransform;
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(selected ? 1 : 0.98, TimeSpan.FromMilliseconds(360)));
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(selected ? 1 : 0.98, TimeSpan.FromMilliseconds(360)));
        }
        if (following && synced) Dispatcher.BeginInvoke(() => ScrollToActive(true));
    }
    private void ScrollToActive(bool animate)
    {
        if (active < 0 || active >= lineButtons.Count || LyricsScroll.ViewportHeight <= 0) return;
        var button = lineButtons[active];
        var y = button.TransformToAncestor(LyricsLines).Transform(new Point()).Y + LyricsLines.Margin.Top;
        var target = Math.Clamp(y - LyricsScroll.ViewportHeight * 0.34, 0, LyricsScroll.ScrollableHeight);
        var from = LyricsScroll.VerticalOffset;
        BeginAnimation(ScrollPositionProperty, null); ScrollPosition = target;
        if (animate) BeginAnimation(ScrollPositionProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(650)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.Stop });
    }
    private void ManualScroll(object sender, MouseWheelEventArgs e)
    {
        if (!synced) return;
        var position = LyricsScroll.VerticalOffset;
        BeginAnimation(ScrollPositionProperty, null); ScrollPosition = position;
        following = false; FollowButton.Content = "Follow lyrics"; FollowButton.Visibility = Visibility.Visible;
    }
    private void ResumeFollowing(object sender, RoutedEventArgs e) { following = true; FollowButton.Visibility = Visibility.Collapsed; ScrollToActive(true); }
    private void Earlier(object sender, RoutedEventArgs e) { offset = Math.Clamp(offset + 0.5, -15, 15); OffsetChanged(); }
    private void Later(object sender, RoutedEventArgs e) { offset = Math.Clamp(offset - 0.5, -15, 15); OffsetChanged(); }
    private void ResetOffset(object sender, RoutedEventArgs e) { offset = 0; OffsetChanged(); }
    private void OpenLyricsMenu(object sender, RoutedEventArgs e) { LyricsMore.ContextMenu!.PlacementTarget = LyricsMore; LyricsMore.ContextMenu.IsOpen = true; }
    private void OffsetChanged() { OffsetLabel.Text = offset.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture) + "s"; active = -2; UpdateActive(); }
    private async void RetryLyrics(object sender, RoutedEventArgs e) { if (current?.Connected == true) { LyricsService.ClearCache(); await Lookup(current); } }
    private void LoadLyricsFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Lyrics files|*.lrc;*.txt|All files|*.*", Title = "Load lyrics for this track" };
        if (dialog.ShowDialog(player) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 2 * 1024 * 1024) { SetEmpty("That file is too large.", "Choose a lyrics file smaller than 2 MB."); return; }
            var text = File.ReadAllText(dialog.FileName); var timed = LyricsService.ParseLrc(text);
            CancelLookup(); ApplyResult(timed.Count > 0 ? new LyricResult(timed, true, "Synced lyrics · local file") : new LyricResult(LyricsService.PlainLines(text), false, "Lyrics · local file · timing unavailable"));
        }
        catch { SetEmpty("Couldn't open that file.", "Try another LRC or text file."); }
    }
    private void SeekStarted(object sender, MouseButtonEventArgs e) => seeking = true;
    private async void SeekEnded(object sender, MouseButtonEventArgs e) { if (!seeking) return; seeking = false; if (sender is Slider slider) await player.SeekTo(slider.Value); }
    private async void SeekKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right && Keyboard.Modifiers == ModifierKeys.None && current?.CanSeek == true)
        { e.Handled = true; await player.SeekTo(seconds + (e.Key == Key.Left ? -5 : 5)); }
    }
    private async void PlayPause(object sender, RoutedEventArgs e) => await player.TogglePlayback();
    private async void Previous(object sender, RoutedEventArgs e) => await player.PreviousTrack();
    private async void Next(object sender, RoutedEventArgs e) => await player.NextTrack();
    private void BackToPlayer(object sender, RoutedEventArgs e) => player.CloseLyrics();
    private void SwitchFullscreen(object sender, RoutedEventArgs e) => player.ToggleLyricsFullscreen();
    private void ClosePlayer(object sender, RoutedEventArgs e) => player.Close();
    private void MoveWindow(object sender, MouseButtonEventArgs e)
    {
        if (!CanDragFrom(e.OriginalSource as DependencyObject)) return;
        if (e.LeftButton == MouseButtonState.Pressed) player.DragMiniWindow(e);
    }
    private bool CanDragFrom(DependencyObject? element)
    {
        if (fullscreen) return false;
        while (element != null)
        {
            if (element is Button or Slider or Thumb or ScrollBar) return false;
            element = VisualTreeHelper.GetParent(element);
        }
        return true;
    }
    internal bool DragSurfacesCorrect() => Scene.Background != null && SceneHeader.Background != null
        && CanDragFrom(SceneHeader) && CanDragFrom(MiniInfo) && CanDragFrom(MiniTitle) && CanDragFrom(Scene)
        && !CanDragFrom(ScreenButton) && !CanDragFrom(MiniSeek) && !CanDragFrom(LyricsMore);
    internal async Task<bool> VerifyOptionsMenu(string path)
    {
        OpenLyricsMenu(this, new RoutedEventArgs());
        await Task.Delay(180);
        var menu = LyricsMore.ContextMenu!;
        var valid = MainWindow.SaveMenuPreview(menu, path);
        menu.IsOpen = false;
        return valid;
    }
    public void CancelLookup() { lookup?.Cancel(); lookup?.Dispose(); lookup = null; }
    internal object Diagnostics() => new { fullscreen, synced, following, active, seconds, offset,
        lineCount = lines.Count, activeTime = active >= 0 && active < lines.Count ? lines[active].Time : -1,
        activeOpacity = active >= 0 && active < lineButtons.Count ? lineButtons[active].Opacity : -1,
        scroll = LyricsScroll.VerticalOffset, viewport = LyricsScroll.ViewportHeight,
        activeY = active >= 0 && active < lineButtons.Count ? lineButtons[active].TransformToAncestor(LyricsScroll).Transform(new Point()).Y : -1 };
    private static string Format(double seconds) => seconds <= 0 ? "0:00" : TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
}

