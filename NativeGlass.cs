using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace LiquidSpotify;

internal static class NativeGlass
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    public static Rect MonitorBounds(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        var transform = HwndSource.FromHwnd(hwnd)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return new Rect(transform.Transform(new Point(info.Monitor.Left, info.Monitor.Top)), transform.Transform(new Point(info.Monitor.Right, info.Monitor.Bottom)));
    }

    public static void Apply(Window window)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        if (source?.CompositionTarget != null) source.CompositionTarget.BackgroundColor = Colors.Transparent;
        // AllowsTransparency creates a layered HWND. Keep its per-pixel alpha intact:
        // full-client DWM glass/accent backdrops can paint behind the rounded cutout.
        // The shared WPF surface supplies the tint, artwork glow, and rounded outline.
    }

    public static bool UsesDesktopAlpha(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        return hwnd != IntPtr.Zero && window.AllowsTransparency
            && HwndSource.FromHwnd(hwnd)?.UsesPerPixelOpacity == true
            && (GetWindowLongPtr(hwnd, -20).ToInt64() & 0x00080000) != 0; // WS_EX_LAYERED
    }
}