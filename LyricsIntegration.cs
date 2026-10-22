using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LiquidSpotify;

internal sealed record NowPlayingSnapshot(string Key, string Title, string Artist, string Album, double Duration,
    double Position, bool Playing, bool Connected, bool CanPlay, bool CanSeek, ImageSource? Artwork);

public partial class MainWindow
{
    private object? playerContent;
    private LyricsExperience? lyricsView;
    private bool lyricsOpen;
    public bool IsFullScreen { get; private set; }
    private Rect playerReturnBounds;
    private Rect smallLyricsBounds;
    private bool returnPinned;
    private string playerReturnMode = "glass";

    private NowPlayingSnapshot Snapshot(double? elapsed = null)
    {
        var value = elapsed ?? CurrentSeconds();
        return new NowPlayingSnapshot(session == null ? "offline" : mediaKey, TrackTitle.Text, Artist.Text, Album.Text,
            duration.TotalSeconds, value, playing, session != null, PlayButton.IsEnabled, Progress.IsEnabled, FullArtwork.ImageSource);
    }
    private double CurrentSeconds()
    {
        var value = (position - start).TotalSeconds;
        if (playing && timelineUpdated != default) value += Math.Max(0, (DateTimeOffset.UtcNow - timelineUpdated).TotalSeconds) * playbackRate;
        return Math.Clamp(value, 0, Math.Max(0, duration.TotalSeconds));
    }
    internal Task TogglePlayback() => Send(s => s.TryTogglePlayPauseAsync());
    internal Task PreviousTrack() => Send(s => s.TrySkipPreviousAsync());
    internal Task NextTrack() => Send(s => s.TrySkipNextAsync());
    internal Task SeekTo(double seconds) => Progress.IsEnabled ? Send(s => s.TryChangePlaybackPositionAsync((start + TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Math.Max(0, duration.TotalSeconds)))).Ticks)) : Task.CompletedTask;
    private void ToggleLyrics(object sender, RoutedEventArgs e) { if (lyricsOpen) CloseLyrics(); else ShowLyrics(false); }
    private void FullscreenLyrics(object sender, RoutedEventArgs e) { if (lyricsOpen) ToggleLyricsFullscreen(); else ShowLyrics(true); }

    public void ShowLyrics(bool fullscreen = false)
    {
        if (lyricsOpen)
        {
            if (fullscreen != IsFullScreen) ToggleLyricsFullscreen();
            return;
        }
        var anchor = motionRunning ? sceneTargetBounds : new Rect(Left, Top, Width, Height);
        var animated = BeginSceneTransition();
        playerContent = scenePresenter!.Content;
        var returnWidth = lastPlayerBounds.Width > 0 ? lastPlayerBounds.Width : Width;
        var returnHeight = lastPlayerBounds.Height > 0 ? lastPlayerBounds.Height : Height;
        playerReturnBounds = new Rect(anchor.Left, anchor.Top, returnWidth, returnHeight);
        playerReturnMode = artworkMode ? "artwork" : compact ? "compact" : "glass";
        returnPinned = Topmost;
        overlayTimer.Stop();
        lyricsView ??= new LyricsExperience(this);
        lyricsView.PrepareOpen();
        lyricsOpen = true; IsFullScreen = fullscreen;
        scenePresenter.Content = lyricsView;
        smallLyricsBounds = new Rect(anchor.Left, anchor.Top, 480, 660);
        lyricsView.SetFullscreen(fullscreen);
        lyricsView.Update(Snapshot());
        if (fullscreen) Topmost = true;
        FinishSceneTransition(fullscreen ? NativeGlass.MonitorBounds(this) : smallLyricsBounds, animated);
        Focus();
    }
    public void ToggleLyricsFullscreen()
    {
        if (!lyricsOpen) { ShowLyrics(true); return; }
        var smallBounds = motionRunning ? sceneTargetBounds : new Rect(Left, Top, Width, Height);
        var animated = BeginSceneTransition();
        Rect target;
        if (!IsFullScreen)
        {
            smallLyricsBounds = smallBounds;
            var screen = NativeGlass.MonitorBounds(this);
            IsFullScreen = true; Topmost = true;
            target = screen;
        }
        else
        {
            IsFullScreen = false; Topmost = returnPinned;
            target = smallLyricsBounds;
        }
        lyricsView!.SetFullscreen(IsFullScreen);
        FinishSceneTransition(target, animated);
    }
    public void CloseLyrics()
    {
        if (!lyricsOpen) return;
        var position = IsFullScreen ? smallLyricsBounds.Location : (motionRunning ? sceneTargetBounds.Location : new Point(Left, Top));
        var animated = BeginSceneTransition();
        lyricsView?.CancelLookup();
        lyricsOpen = false; IsFullScreen = false;
        scenePresenter!.Content = playerContent;
        Topmost = returnPinned;
        FinishSceneTransition(new Rect(position, playerReturnBounds.Size), animated);
        RevealArtworkControls();
        Focus();
    }
    internal void DragMiniWindow(MouseButtonEventArgs e)
    {
        if (IsFullScreen || e.LeftButton != MouseButtonState.Pressed) return;
        if (motionRunning) CompleteSceneTransition(motionGeneration);
        e.Handled = true;
        try { DragMove(); }
        catch (InvalidOperationException) { }
        RememberWindowPosition();
    }
    private void RememberWindowPosition()
    {
        if (IsFullScreen) return;
        sceneTargetBounds = new Rect(Left, Top, Width, Height);
        if (lyricsOpen)
        {
            smallLyricsBounds = sceneTargetBounds;
            playerReturnBounds = new Rect(new Point(Left, Top), playerReturnBounds.Size);
        }
        else lastPlayerBounds = sceneTargetBounds;
        SaveSettings();
    }
    private bool HandleLyricsKey(KeyEventArgs e)
    {
        if (e.Key == Key.F11) { e.Handled = true; ToggleLyricsFullscreen(); return true; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L)
        { e.Handled = true; if (lyricsOpen) CloseLyrics(); else ShowLyrics(false); return true; }
        if (lyricsOpen && e.Key == Key.Escape)
        { e.Handled = true; if (IsFullScreen) ToggleLyricsFullscreen(); else CloseLyrics(); return true; }
        if (lyricsOpen && Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.E or Key.M)
        { e.Handled = true; CloseLyrics(); SetViewMode(e.Key == Key.M ? "compact" : "artwork"); return true; }
        return false;
    }
}
