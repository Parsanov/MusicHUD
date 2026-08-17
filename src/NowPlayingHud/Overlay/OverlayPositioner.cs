using NowPlayingHud.Interop;
using NowPlayingHud.Settings;

namespace NowPlayingHud.Overlay;

/// <summary>
/// Рахує, куди поставити вікно оверлея, у фізичних пікселях.
///
/// Через SetWindowPos, а не через Window.Left/Top: WPF мислить у DIP-ах
/// «свого» монітора, а нам треба покласти вікно на монітор, де зараз гра,
/// у якого масштаб може бути іншим.
/// </summary>
internal static class OverlayPositioner
{
    /// <param name="hwnd">Вікно оверлея.</param>
    /// <param name="widthDip">Ширина вікна в логічних пікселях (ActualWidth).</param>
    /// <param name="heightDip">Висота вікна в логічних пікселях (ActualHeight).</param>
    /// <param name="corner">Кут, обраний у налаштуваннях.</param>
    /// <param name="marginDip">Відступ від краю робочої області, у DIP.</param>
    public static void Place(IntPtr hwnd, double widthDip, double heightDip, OverlayCorner corner, int marginDip)
    {
        var monitor = ResolveTargetMonitor(hwnd);

        var info = new NativeMethods.MONITORINFO();
        info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>();

        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var scale = NativeMethods.GetMonitorScale(monitor);

        // rcWork, а не rcMonitor: на робочому столі HUD не має лізти під панель задач.
        // У повноекранній грі ці прямокутники все одно збігаються.
        var area = info.rcWork;

        var width = (int)Math.Round(widthDip * scale);
        var height = (int)Math.Round(heightDip * scale);
        var margin = (int)Math.Round(marginDip * scale);

        var x = corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft
            ? area.Left + margin
            : area.Right - width - margin;

        var y = corner is OverlayCorner.TopLeft or OverlayCorner.TopRight
            ? area.Top + margin
            : area.Bottom - height - margin;

        NativeMethods.MoveWindowNoActivate(hwnd, x, y);
    }

    /// <summary>
    /// Монітор, на якому зараз активне вікно: HUD має з'явитись там, де гра.
    /// Якщо активного вікна немає (робочий стіл) — беремо монітор під курсором.
    /// </summary>
    private static IntPtr ResolveTargetMonitor(IntPtr overlayHwnd)
    {
        var foreground = NativeMethods.GetForegroundWindow();

        if (foreground != IntPtr.Zero && foreground != overlayHwnd)
        {
            return NativeMethods.MonitorFromWindow(foreground, WindowStyles.MONITOR_DEFAULTTONEAREST);
        }

        if (NativeMethods.GetCursorPos(out var cursor))
        {
            return NativeMethods.MonitorFromPoint(cursor, WindowStyles.MONITOR_DEFAULTTONEAREST);
        }

        return NativeMethods.MonitorFromWindow(overlayHwnd, WindowStyles.MONITOR_DEFAULTTONEAREST);
    }

    /// <summary>Напрямок, з якого HUD виїжджає — від найближчого краю екрана.</summary>
    public static (double OffsetX, double OffsetY) SlideOffset(OverlayCorner corner, double distance) => corner switch
    {
        OverlayCorner.TopLeft => (0, -distance),
        OverlayCorner.TopRight => (0, -distance),
        OverlayCorner.BottomLeft => (0, distance),
        _ => (0, distance)
    };
}
