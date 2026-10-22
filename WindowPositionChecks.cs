using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LiquidSpotify;

public partial class MainWindow
{
    public async Task VerifyWindowPosition(string directory)
    {
        Directory.CreateDirectory(directory);
        var original = new Point(Left, Top);
        var checks = new List<object>();
        bool At(Point point) => Math.Abs(Left - point.X) < 0.01 && Math.Abs(Top - point.Y) < 0.01;
        await Task.Delay(2500);
        try
        {
            var origins = new[] { new Point(80, 65), new Point(SystemParameters.PrimaryScreenWidth - 130, SystemParameters.PrimaryScreenHeight - 100), new Point(-35, -25) };
            for (var i = 0; i < origins.Length; i++)
            {
                if (lyricsOpen) { CloseLyrics(); await Task.Delay(550); }
                SetViewMode("glass", false); Left = origins[i].X; Top = origins[i].Y; RememberWindowPosition();
                foreach (var mode in new[] { "compact", "artwork", "glass", "lyrics" })
                {
                    if (mode == "lyrics") ShowLyrics(); else SetViewMode(mode);
                    await Task.Delay(120);
                    var inFlight = At(origins[i]);
                    await Task.Delay(500);
                    checks.Add(new { test = "fixed-position", origin = i, mode, passed = inFlight && At(origins[i]), Left, Top });
                }
                checks.Add(new { test = "lyrics-drag-hit-areas", origin = i, passed = lyricsView!.DragSurfacesCorrect() });
                CloseLyrics(); await Task.Delay(550);
                checks.Add(new { test = "close-lyrics-same-position", origin = i, passed = At(origins[i]), Left, Top });
            }
            Left = 125; Top = 95; RememberWindowPosition();
            ShowLyrics(); await Task.Delay(550);
            var moved = new Point(238, 154);
            Left = moved.X; Top = moved.Y; RememberWindowPosition();
            CloseLyrics(); await Task.Delay(550);
            checks.Add(new { test = "keep-latest-dragged-position", passed = At(moved), Left, Top });
            ShowLyrics(); await Task.Delay(550);
            ToggleLyricsFullscreen(); await Task.Delay(650);
            ToggleLyricsFullscreen(); await Task.Delay(650);
            checks.Add(new { test = "fullscreen-restores-latest-position", passed = At(moved), Left, Top });
            CloseLyrics(); await Task.Delay(550);
            checks.Add(new { test = "player-return-after-fullscreen", passed = At(moved), Left, Top });
        }
        finally
        {
            if (lyricsOpen) { CloseLyrics(); await Task.Delay(550); }
            SetViewMode("glass", false); Left = original.X; Top = original.Y; RememberWindowPosition();
        }
        SavePreview(Path.Combine(directory, "player.png"));
        OpenPlayerMenu(this, new RoutedEventArgs());
        await Task.Delay(180);
        var playerMenu = PlayerMore.ContextMenu!;
        checks.Add(new { test = "player-options-menu", passed = SaveMenuPreview(playerMenu, Path.Combine(directory, "player-menu.png"))
            && playerMenu.Items.OfType<MenuItem>().First(m => Equals(m.Header, "Keep on top")).IsChecked == Topmost });
        playerMenu.IsOpen = false;
        ShowLyrics(); await Task.Delay(1600);
        SavePreview(Path.Combine(directory, "lyrics.png"));
        checks.Add(new { test = "lyrics-options-menu", passed = await lyricsView!.VerifyOptionsMenu(Path.Combine(directory, "lyrics-menu.png")) });
        File.WriteAllText(Path.Combine(directory, "position-checks.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }
    internal static bool SaveMenuPreview(ContextMenu menu, string path)
    {
        menu.UpdateLayout();
        if (!menu.IsOpen || menu.ActualWidth < 1 || menu.ActualHeight < 1) return false;
        var image = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth * 2), (int)Math.Ceiling(menu.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        image.Render(menu);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); png.Save(file); return true;
    }
}
