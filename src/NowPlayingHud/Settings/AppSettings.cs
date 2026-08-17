using System.Text.Json.Serialization;
using NowPlayingHud.Input;

namespace NowPlayingHud.Settings;

public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public enum HudMode
{
    /// <summary>Натиснув — показався на задану тривалість або до повторного натискання.</summary>
    Toggle,

    /// <summary>Тримаєш комбінацію — видно, відпустив — зникло.</summary>
    HoldToPeek
}

/// <summary>
/// Рівно шість пунктів налаштувань. Список закритий — див. SCOPE.md.
/// Хоткеї зберігаються рядками ("Alt+Shift+M"), щоб файл можна було правити руками.
/// </summary>
public sealed class AppSettings
{
    // Межі в одному місці: на них спираються і Normalize, і повзунки у вікні налаштувань.
    public const double MinPassiveSeconds = 1.0;
    public const double MaxPassiveSeconds = 15.0;
    public const double MinScale = 0.8;    // менше — текст назви треку нечитабельний
    public const double MaxScale = 1.6;    // більше — картка починає заважати грі

    // 1. Прив'язка хоткеїв
    public string HotkeyToggleHud { get; set; } = "Alt+Shift+M";
    public string HotkeyPlayPause { get; set; } = "Alt+Shift+Space";
    public string HotkeyNext { get; set; } = "Alt+Shift+Right";
    public string HotkeyPrevious { get; set; } = "Alt+Shift+Left";

    /// <summary>Поведінка хоткея показу. Технічно частина п.1.</summary>
    public HudMode Mode { get; set; } = HudMode.Toggle;

    // 2. Кут + відступ
    public OverlayCorner Corner { get; set; } = OverlayCorner.BottomRight;

    /// <summary>Відступ від краю робочої області, у логічних пікселях (DIP).</summary>
    public int MarginDip { get; set; } = 24;

    // 3. Пасивний HUD
    public bool PassiveHudEnabled { get; set; } = true;
    public double PassiveHudSeconds { get; set; } = 15.0;

    // 4. Масштаб
    public double Scale { get; set; } = 1.1;   // 110%: на 1080p стандартні 100% дрібнуваті

    // 5. Автозапуск з Windows
    public bool RunAtStartup { get; set; }

    // 6. Джерело за замовчуванням. null = активна системна сесія.
    public string? PinnedSourceAppId { get; set; }

    [JsonIgnore]
    public TimeSpan PassiveHudDuration =>
        TimeSpan.FromSeconds(Math.Clamp(PassiveHudSeconds, MinPassiveSeconds, MaxPassiveSeconds));

    /// <summary>Розібрані хоткеї у вигляді, придатному для HotkeyService.</summary>
    public Dictionary<HotkeyAction, HotkeyBinding> BuildBindings()
    {
        var result = new Dictionary<HotkeyAction, HotkeyBinding>();

        Add(HotkeyAction.ToggleHud, HotkeyToggleHud);
        Add(HotkeyAction.PlayPause, HotkeyPlayPause);
        Add(HotkeyAction.Next, HotkeyNext);
        Add(HotkeyAction.Previous, HotkeyPrevious);

        return result;

        void Add(HotkeyAction action, string text)
        {
            if (HotkeyBinding.TryParse(text, out var binding))
            {
                result[action] = binding;
            }
        }
    }

    public void SetBinding(HotkeyAction action, HotkeyBinding binding)
    {
        var text = binding.IsEmpty ? string.Empty : binding.ToString();

        switch (action)
        {
            case HotkeyAction.ToggleHud: HotkeyToggleHud = text; break;
            case HotkeyAction.PlayPause: HotkeyPlayPause = text; break;
            case HotkeyAction.Next: HotkeyNext = text; break;
            case HotkeyAction.Previous: HotkeyPrevious = text; break;
        }
    }

    public HotkeyBinding GetBinding(HotkeyAction action)
    {
        var text = action switch
        {
            HotkeyAction.ToggleHud => HotkeyToggleHud,
            HotkeyAction.PlayPause => HotkeyPlayPause,
            HotkeyAction.Next => HotkeyNext,
            HotkeyAction.Previous => HotkeyPrevious,
            _ => string.Empty
        };

        return HotkeyBinding.TryParse(text, out var binding) ? binding : HotkeyBinding.None;
    }

    /// <summary>Прибирає значення, які могли приїхати з битого або підправленого руками файлу.</summary>
    public void Normalize()
    {
        Scale = Math.Clamp(Scale, MinScale, MaxScale);
        MarginDip = Math.Clamp(MarginDip, 0, 200);
        PassiveHudSeconds = Math.Clamp(PassiveHudSeconds, MinPassiveSeconds, MaxPassiveSeconds);

        if (string.IsNullOrWhiteSpace(PinnedSourceAppId))
        {
            PinnedSourceAppId = null;
        }
    }
}
