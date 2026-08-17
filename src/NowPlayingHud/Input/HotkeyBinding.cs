using System.Text;
using System.Windows.Input;
using NowPlayingHud.Interop;

namespace NowPlayingHud.Input;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8
}

/// <summary>Що робить хоткей. Кожна кнопка HUD має дублікат тут.</summary>
public enum HotkeyAction
{
    /// <summary>Показати/сховати HUD.</summary>
    ToggleHud,
    PlayPause,
    Next,
    Previous
}

/// <summary>
/// Комбінація клавіш. Зберігається в JSON рядком виду "Alt+Shift+M",
/// щоб файл налаштувань можна було правити руками.
/// </summary>
public sealed record HotkeyBinding(HotkeyModifiers Modifiers, Key Key)
{
    public static HotkeyBinding None { get; } = new(HotkeyModifiers.None, Key.None);

    public bool IsEmpty => Key == Key.None;

    /// <summary>Прапорці у форматі RegisterHotKey. MOD_NOREPEAT — щоб утримана клавіша не сипала подіями.</summary>
    public uint ToWin32Modifiers()
    {
        uint flags = WindowStyles.MOD_NOREPEAT;

        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) flags |= WindowStyles.MOD_ALT;
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) flags |= WindowStyles.MOD_CONTROL;
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) flags |= WindowStyles.MOD_SHIFT;
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) flags |= WindowStyles.MOD_WIN;

        return flags;
    }

    public int ToVirtualKey() => KeyInterop.VirtualKeyFromKey(Key);

    public override string ToString()
    {
        if (IsEmpty)
        {
            return "(не задано)";
        }

        var text = new StringBuilder();

        if (Modifiers.HasFlag(HotkeyModifiers.Control)) text.Append("Ctrl+");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) text.Append("Alt+");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) text.Append("Shift+");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) text.Append("Win+");

        return text.Append(Key).ToString();
    }

    public static bool TryParse(string? text, out HotkeyBinding binding)
    {
        binding = None;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = HotkeyModifiers.None;
        var key = Key.None;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= HotkeyModifiers.Control; break;
                case "alt": modifiers |= HotkeyModifiers.Alt; break;
                case "shift": modifiers |= HotkeyModifiers.Shift; break;
                case "win" or "windows": modifiers |= HotkeyModifiers.Win; break;
                default:
                    if (!Enum.TryParse(part, ignoreCase: true, out key))
                    {
                        return false;
                    }
                    break;
            }
        }

        if (key == Key.None)
        {
            return false;
        }

        binding = new HotkeyBinding(modifiers, key);
        return true;
    }

    /// <summary>Збирає біндинг з натискання в UI-контролі захоплення хоткею.</summary>
    public static HotkeyBinding FromKeyEvent(Key key, ModifierKeys modifiers)
    {
        // При Alt+X WPF віддає Key.System, а справжню клавішу — в SystemKey.
        var effective = key == Key.System ? Key.None : key;

        var result = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= HotkeyModifiers.Win;

        return new HotkeyBinding(result, effective);
    }

    /// <summary>Модифікатори самі по собі клавішею хоткею бути не можуть.</summary>
    public static bool IsModifierKey(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin
        or Key.System;
}
