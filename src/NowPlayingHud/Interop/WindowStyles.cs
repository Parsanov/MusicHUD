namespace NowPlayingHud.Interop;

/// <summary>
/// Константи user32, які потрібні оверлею. Винесені окремо, щоб
/// NativeMethods лишався тільки списком P/Invoke.
/// </summary>
internal static class WindowStyles
{
    // --- індекси для GetWindowLongPtr / SetWindowLongPtr ---
    public const int GWL_EXSTYLE = -20;

    // --- розширені стилі вікна (WS_EX_*) ---

    /// <summary>Вікно не з'являється в Alt+Tab і на панелі задач.</summary>
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>Вікно не забирає фокус при показі — гра лишається активною.</summary>
    public const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>Кліки проходять крізь вікно. Перемикається динамічно.</summary>
    public const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>Потрібен разом з AllowsTransparency для коректної альфи.</summary>
    public const int WS_EX_LAYERED = 0x00080000;

    // --- SetWindowPos ---
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    // --- повідомлення ---
    public const int WM_HOTKEY = 0x0312;

    // --- модифікатори для RegisterHotKey ---
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    /// <summary>Без цього утримана клавіша шле WM_HOTKEY десятки разів на секунду.</summary>
    public const uint MOD_NOREPEAT = 0x4000;

    /// <summary>Спеціальний батько для message-only вікна.</summary>
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    /// <summary>GetCurrentPackageFullName повертає це, якщо процес не в MSIX-пакеті.</summary>
    public const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    /// <summary>MONITOR_DEFAULTTONEAREST — якщо вікна/точки немає на жодному моніторі.</summary>
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
}
