using System.Runtime.InteropServices;
using System.Text;

namespace NowPlayingHud.Interop;

internal static class NativeMethods
{
    // ------------------------------------------------------------------
    // Стилі вікна
    // ------------------------------------------------------------------

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>
    /// SetWindowLongPtr фізично існує тільки в 64-бітному user32; у 32-бітному
    /// це макрос над SetWindowLong. Тому розгалуження за розміром вказівника.
    /// </summary>
    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    public static int GetWindowExStyle(IntPtr hWnd) => (int)GetWindowLongPtr(hWnd, WindowStyles.GWL_EXSTYLE);

    public static void SetWindowExStyle(IntPtr hWnd, int exStyle) =>
        SetWindowLongPtr(hWnd, WindowStyles.GWL_EXSTYLE, new IntPtr(exStyle));

    public static void AddWindowExStyle(IntPtr hWnd, int flags) =>
        SetWindowExStyle(hWnd, GetWindowExStyle(hWnd) | flags);

    /// <summary>Вмикає/вимикає WS_EX_TRANSPARENT — «кліки проходять крізь».</summary>
    public static void SetClickThrough(IntPtr hWnd, bool clickThrough)
    {
        var style = GetWindowExStyle(hWnd);
        var updated = clickThrough
            ? style | WindowStyles.WS_EX_TRANSPARENT
            : style & ~WindowStyles.WS_EX_TRANSPARENT;

        if (updated != style)
        {
            SetWindowExStyle(hWnd, updated);
        }
    }

    // ------------------------------------------------------------------
    // Позиція та z-order
    // ------------------------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    /// <summary>
    /// Повторно піднімає вікно нагору. Ігри та інші topmost-застосунки
    /// регулярно перебивають z-order, тому це робиться на кожному показі.
    /// </summary>
    public static void ForceTopMost(IntPtr hWnd) =>
        SetWindowPos(hWnd, WindowStyles.HWND_TOPMOST, 0, 0, 0, 0,
            WindowStyles.SWP_NOMOVE | WindowStyles.SWP_NOSIZE | WindowStyles.SWP_NOACTIVATE);

    /// <summary>Переміщує вікно у фізичних пікселях, не активуючи його.</summary>
    public static void MoveWindowNoActivate(IntPtr hWnd, int x, int y) =>
        SetWindowPos(hWnd, WindowStyles.HWND_TOPMOST, x, y, 0, 0,
            WindowStyles.SWP_NOSIZE | WindowStyles.SWP_NOACTIVATE);

    // ------------------------------------------------------------------
    // Хоткеї
    // ------------------------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>
    /// Старший біт = клавіша натиснута зараз. Потрібно для hold-to-peek:
    /// RegisterHotKey сповіщає лише про натискання, відпускання доводиться опитувати.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    public static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    // ------------------------------------------------------------------
    // Монітори та DPI
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;   // повна область монітора
        public RECT rcWork;      // без панелі задач
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>MDT_EFFECTIVE_DPI = 0 — масштаб, який реально бачить користувач.</summary>
    private const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// DPI конкретного монітора. Береться саме монітора, а не вікна:
    /// вікно ще не переїхало на цільовий екран, коли рахується позиція.
    /// </summary>
    public static double GetMonitorScale(IntPtr hMonitor)
    {
        if (GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
        {
            return dpiX / 96.0;   // 96 DPI = 100% масштабу = 1 DIP до 1 пікселя
        }

        return 1.0;
    }

    // ------------------------------------------------------------------
    // Визначення MSIX-упаковки
    // ------------------------------------------------------------------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    /// <summary>
    /// true, якщо процес запущений з MSIX-пакета. Від цього залежить шлях,
    /// куди пишуться налаштування (файлова система віртуалізується).
    /// </summary>
    public static bool IsRunningPackaged()
    {
        var length = 0;
        return GetCurrentPackageFullName(ref length, null) != WindowStyles.APPMODEL_ERROR_NO_PACKAGE;
    }
}
