using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Windows.Media.Control;

namespace LiquidSpotify;

public partial class App : Application
{
    private static System.Threading.Mutex? instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--diagnose"))
        {
            try
            {
                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var sessions = new List<object>();
                foreach (var s in manager.GetSessions())
                {
                    var p = await s.TryGetMediaPropertiesAsync();
                    var info = s.GetPlaybackInfo();
                    var t = s.GetTimelineProperties();
                    sessions.Add(new { app = s.SourceAppUserModelId, title = p.Title, artist = p.Artist,
                        album = p.AlbumTitle, artwork = p.Thumbnail != null, status = info.PlaybackStatus.ToString(),
                        position = t.Position.TotalSeconds, duration = t.EndTime.TotalSeconds,
                        play = info.Controls.IsPlayEnabled, pause = info.Controls.IsPauseEnabled,
                        next = info.Controls.IsNextEnabled, previous = info.Controls.IsPreviousEnabled,
                        seek = info.Controls.IsPlaybackPositionEnabled, shuffle = info.Controls.IsShuffleEnabled,
                        repeat = info.Controls.IsRepeatEnabled });
                }
                var report = ArgValue(e.Args, "--report");
                if (report != null) File.WriteAllText(report, JsonSerializer.Serialize(sessions, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0);
            }
            catch (Exception ex) { var report = ArgValue(e.Args, "--report"); if (report != null) File.WriteAllText(report, ex.ToString()); Shutdown(1); }
            return;
        }
        instance = new System.Threading.Mutex(true, "Local\\LiquidSpotify.MiniPlayer", out var isNew);
        if (!isNew) { Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "LiquidSpotify-error.txt"), args.Exception.ToString()); } catch { }
            args.Handled = true;
        };
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.Contains("--artwork")) window.SetViewMode("artwork", false);
        if (e.Args.Contains("--lyrics")) window.ShowLyrics(false);
        if (e.Args.Contains("--fullscreen")) window.ShowLyrics(true);
        var motionChecks = ArgValue(e.Args, "--verify-motion");
        if (motionChecks != null) await window.VerifyWindowMotion(motionChecks);
        var positionChecks = ArgValue(e.Args, "--verify-position");
        if (positionChecks != null) await window.VerifyWindowPosition(positionChecks);
        var capture = ArgValue(e.Args, "--capture");
        if (capture != null)
        {
            await Task.Delay(5000);
            window.SavePreview(capture);
        }
        var captures = ArgValue(e.Args, "--capture-set");
        if (captures != null)
        {
            Directory.CreateDirectory(captures);
            await Task.Delay(5000);
            foreach (var mode in new[] { "glass", "compact", "artwork" })
            {
                window.SetViewMode(mode);
                await Task.Delay(700);
                window.SavePreview(Path.Combine(captures, mode + ".png"));
            }
            await Task.Delay(3300);
            window.SavePreview(Path.Combine(captures, "artwork-clean.png"));
            window.SaveArtwork(Path.Combine(captures, "cover-source.png"));
        }
        var lyricsCaptures = ArgValue(e.Args, "--capture-lyrics");
        if (lyricsCaptures != null)
        {
            Directory.CreateDirectory(lyricsCaptures);
            window.ShowLyrics(false);
            await Task.Delay(12000);
            window.SavePreview(Path.Combine(lyricsCaptures, "lyrics.png"));
            window.ToggleLyricsFullscreen();
            await Task.Delay(1400);
            window.SavePreview(Path.Combine(lyricsCaptures, "fullscreen-lyrics.png"));
        }
    }
    private static string? ArgValue(string[] args, string key)
    {
        var i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
