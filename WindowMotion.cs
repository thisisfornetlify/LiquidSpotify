using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.IO;
using System.Text.Json;

namespace LiquidSpotify;

public partial class MainWindow
{
    private const double WindowRadius = 32;
    private Grid? windowSurface;
    private ContentPresenter? scenePresenter;
    private Image? outgoingScene;
    private Border? windowEdge;
    private readonly RectangleGeometry cornerGeometry = new();
    private DrawingBrush? cornerMask;
    private int motionGeneration;
    private bool motionRunning;
    private Rect lastPlayerBounds;
    private Rect sceneTargetBounds;
    private static readonly TimeSpan MotionDuration = TimeSpan.FromMilliseconds(420);
    private static readonly IEasingFunction MotionEase = new QuinticEase { EasingMode = EasingMode.EaseOut };

    public static readonly DependencyProperty SurfaceRadiusProperty = DependencyProperty.Register(
        nameof(SurfaceRadius), typeof(double), typeof(MainWindow),
        new PropertyMetadata(WindowRadius, (d, _) => ((MainWindow)d).UpdateWindowShape()));
    public double SurfaceRadius { get => (double)GetValue(SurfaceRadiusProperty); set => SetValue(SurfaceRadiusProperty, value); }
    private static readonly DependencyProperty BoundsFrameProperty = DependencyProperty.Register(
        "BoundsFrame", typeof(Rect), typeof(MainWindow),
        new PropertyMetadata(Rect.Empty, (d, e) => ((MainWindow)d).ApplyBoundsFrame((Rect)e.NewValue)));
    private void ApplyBoundsFrame(Rect bounds)
    {
        if (bounds.IsEmpty) return;
        Left = bounds.Left; Top = bounds.Top; Width = bounds.Width; Height = bounds.Height;
    }

    private void InitializeWindowSurface()
    {
        var initialScene = Content;
        Content = null;
        scenePresenter = new ContentPresenter { Content = initialScene, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1) };
        outgoingScene = new Image { Stretch = Stretch.UniformToFill, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        windowEdge = new Border
        {
            IsHitTestVisible = false, BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255))
        };
        windowSurface = new Grid { ClipToBounds = true };
        windowSurface.Children.Add(scenePresenter);
        windowSurface.Children.Add(outgoingScene);
        windowSurface.Children.Add(windowEdge);
        cornerMask = new DrawingBrush(new GeometryDrawing(Brushes.White, null, cornerGeometry)) { Stretch = Stretch.Fill };
        windowSurface.OpacityMask = cornerMask;
        Shell.BorderThickness = new Thickness(0);
        Content = windowSurface;
        SizeChanged += (_, _) => UpdateWindowShape();
        DpiChanged += (_, _) => UpdateWindowShape();
    }
    private void UpdateWindowShape()
    {
        if (windowSurface == null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var radius = Math.Clamp(SurfaceRadius, 0, Math.Min(ActualWidth, ActualHeight) / 2);
        cornerGeometry.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
        cornerGeometry.RadiusX = cornerGeometry.RadiusY = radius;
        windowSurface.OpacityMask = radius > 0.01 ? cornerMask : null;
        windowEdge!.CornerRadius = new CornerRadius(radius);
        windowEdge.Opacity = Math.Clamp(radius / WindowRadius, 0, 1);
        // The layered HWND presents this alpha mask directly to the desktop.
        // A native region would quantize the antialiased edge to whole pixels.
    }
    private bool BeginSceneTransition(bool animate = true)
    {
        motionGeneration++;
        if (windowSurface == null || scenePresenter == null || outgoingScene == null) return false;
        // Capture the currently composited frame, including an interrupted fade.
        // A bounded bitmap keeps fullscreen transitions inexpensive.
        BitmapSource? snapshot = null;
        if (animate && IsLoaded && ActualWidth > 0 && ActualHeight > 0)
        {
            try
            {
                var scale = Math.Min(1.5, 1100 / Math.Max(ActualWidth, ActualHeight));
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                    drawing.DrawRectangle(new VisualBrush(windowSurface), null, new Rect(0, 0, ActualWidth * scale, ActualHeight * scale));
                var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(ActualWidth * scale)), Math.Max(1, (int)Math.Ceiling(ActualHeight * scale)), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual); bitmap.Freeze(); snapshot = bitmap;
            }
            catch (InvalidOperationException) { }
        }
        FreezeWindowBounds();
        scenePresenter.BeginAnimation(OpacityProperty, null);
        ((ScaleTransform)scenePresenter.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ((ScaleTransform)scenePresenter.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, null);
        outgoingScene.BeginAnimation(OpacityProperty, null);
        motionRunning = snapshot != null;
        scenePresenter.IsHitTestVisible = !motionRunning;
        if (snapshot == null)
        {
            outgoingScene.Visibility = Visibility.Collapsed; outgoingScene.Source = null;
            scenePresenter.Opacity = 1; return false;
        }
        outgoingScene.Source = snapshot; outgoingScene.Visibility = Visibility.Visible; outgoingScene.Opacity = 1;
        scenePresenter.Opacity = 0;
        return true;
    }
    private void FinishSceneTransition(Rect target, bool animate)
    {
        sceneTargetBounds = target;
        var generation = motionGeneration;
        var animated = animate && motionRunning;
        var fromBounds = new Rect(Left, Top, Width, Height);
        SetValue(BoundsFrameProperty, fromBounds);
        BeginAnimation(BoundsFrameProperty, null);
        if (animated)
        {
            var boundsAnimation = new RectAnimation(fromBounds, target, MotionDuration) { EasingFunction = MotionEase };
            boundsAnimation.Completed += (_, _) => CompleteSceneTransition(generation);
            BeginAnimation(BoundsFrameProperty, boundsAnimation);
        }
        else SetValue(BoundsFrameProperty, target);
        AnimateWindowValue(SurfaceRadiusProperty, IsFullScreen ? 0 : WindowRadius, animated);
        UpdateWindowShape();
        if (!animated || scenePresenter == null || outgoingScene == null)
        { CompleteSceneTransition(generation); return; }
        scenePresenter.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(270))
        { BeginTime = TimeSpan.FromMilliseconds(65), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        var scale = (ScaleTransform)scenePresenter.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.995, 1, MotionDuration) { EasingFunction = MotionEase });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.995, 1, MotionDuration) { EasingFunction = MotionEase });
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(105)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        outgoingScene.BeginAnimation(OpacityProperty, fade);
    }
    private void CompleteSceneTransition(int generation)
    {
        if (generation != motionGeneration || scenePresenter == null || outgoingScene == null) return;
        SetValue(BoundsFrameProperty, sceneTargetBounds);
        BeginAnimation(BoundsFrameProperty, null);
        ApplyBoundsFrame(sceneTargetBounds);
        var radius = IsFullScreen ? 0 : WindowRadius;
        SetValue(SurfaceRadiusProperty, radius); BeginAnimation(SurfaceRadiusProperty, null);
        scenePresenter.BeginAnimation(OpacityProperty, null); scenePresenter.Opacity = 1;
        var scale = (ScaleTransform)scenePresenter.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = scale.ScaleY = 1;
        scenePresenter.IsHitTestVisible = true;
        outgoingScene.BeginAnimation(OpacityProperty, null); outgoingScene.Visibility = Visibility.Collapsed; outgoingScene.Source = null;
        motionRunning = false; UpdateWindowShape();
    }
    private void FreezeWindowBounds()
    {
        var frame = motionRunning ? (Rect)GetValue(BoundsFrameProperty) : new Rect(Left, Top, Width, Height);
        if (!frame.IsEmpty) { SetValue(BoundsFrameProperty, frame); BeginAnimation(BoundsFrameProperty, null); ApplyBoundsFrame(frame); }
        var radius = SurfaceRadius; SetValue(SurfaceRadiusProperty, radius); BeginAnimation(SurfaceRadiusProperty, null);
    }
    private void AnimateWindowValue(DependencyProperty property, double target, bool animate)
    {
        var from = (double)GetValue(property); BeginAnimation(property, null); SetValue(property, target);
        if (animate && Math.Abs(from - target) > 0.01)
            BeginAnimation(property, new DoubleAnimation(from, target, MotionDuration) { EasingFunction = MotionEase, FillBehavior = FillBehavior.Stop });
    }
    public async Task VerifyWindowMotion(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var originalLeft = 0.0; var originalTop = 0.0;
        await Task.Delay(3500);
        foreach (var mode in new[] { "glass", "compact", "artwork", "lyrics", "fullscreen" })
        {
            if (mode == "lyrics") ShowLyrics();
            else if (mode == "fullscreen") ToggleLyricsFullscreen();
            else SetViewMode(mode);
            await Task.Delay(120);
            if (mode is "compact" or "fullscreen")
            {
                checks.Add(new { mode = mode + "-in-motion", passed = motionRunning && outgoingScene!.Visibility == Visibility.Visible && scenePresenter!.Opacity > 0 && scenePresenter.Opacity < 1,
                    ActualWidth, ActualHeight, radius = SurfaceRadius, motionRunning, opacity = scenePresenter!.Opacity });
                SavePreview(Path.Combine(directory, mode + "-transition.png"));
            }
            await Task.Delay(650);
            UpdateLayout();
            SavePreview(Path.Combine(directory, mode + ".png"));
            var image = new RenderTargetBitmap(Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(this);
            var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0);
            var corners = new[] { pixels[3], pixels[(image.PixelWidth - 1) * 4 + 3], pixels[(image.PixelHeight - 1) * image.PixelWidth * 4 + 3], pixels[^1] };
            var transparentCorners = corners.All(a => a == 0);
            var expectedWidth = mode switch { "glass" or "compact" => 408, "artwork" => 388, "lyrics" => 480, _ => NativeGlass.MonitorBounds(this).Width };
            var expectedHeight = mode switch { "glass" => 268, "compact" => 124, "artwork" => 388, "lyrics" => 660, _ => NativeGlass.MonitorBounds(this).Height };
            var nativeTransparency = NativeGlass.UsesDesktopAlpha(this);
            var passed = nativeTransparency && !motionRunning && scenePresenter!.IsHitTestVisible && Math.Abs(ActualWidth - expectedWidth) < 1 && Math.Abs(ActualHeight - expectedHeight) < 1 && (mode == "fullscreen" ? corners.All(a => a == 255) && SurfaceRadius < 0.01 : transparentCorners && Math.Abs(SurfaceRadius - WindowRadius) < 0.01);
            if (mode == "glass") { originalLeft = Left; originalTop = Top; }
            checks.Add(new { mode, passed, ActualWidth, ActualHeight, radius = SurfaceRadius, cornerAlpha = corners, nativeTransparency, motionRunning, interactive = scenePresenter!.IsHitTestVisible });
        }
        CloseLyrics();
        await Task.Delay(80); SetViewMode("compact");
        await Task.Delay(70); SetViewMode("artwork");
        await Task.Delay(70); ShowLyrics();
        await Task.Delay(70); ToggleLyricsFullscreen();
        await Task.Delay(70); CloseLyrics();
        await Task.Delay(70); SetViewMode("glass");
        await Task.Delay(700);
        checks.Add(new { mode = "rapid-interruption", passed = !motionRunning && scenePresenter!.IsHitTestVisible && outgoingScene!.Visibility == Visibility.Collapsed && Math.Abs(ActualWidth - 408) < 1 && Math.Abs(ActualHeight - 268) < 1 && Math.Abs(SurfaceRadius - WindowRadius) < 0.01 && Math.Abs(Left - originalLeft) < 1 && Math.Abs(Top - originalTop) < 1,
            ActualWidth, ActualHeight, radius = SurfaceRadius, motionRunning, anchorRestored = Math.Abs(Left - originalLeft) < 1 && Math.Abs(Top - originalTop) < 1 });
        SavePreview(Path.Combine(directory, "after-rapid-switching.png"));
        File.WriteAllText(Path.Combine(directory, "motion-checks.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }
}


