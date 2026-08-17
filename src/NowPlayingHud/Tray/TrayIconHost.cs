using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;

namespace NowPlayingHud.Tray;

/// <summary>
/// Іконка в треї — єдина точка входу в застосунок після запуску:
/// головного вікна в нього немає.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly TaskbarIcon _icon;
    private bool _disposed;

    public event EventHandler? ShowHudRequested;
    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? ExitRequested;

    public TrayIconHost()
    {
        var menu = new ContextMenu();

        menu.Items.Add(CreateItem("Показати HUD", () => ShowHudRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(CreateItem("Налаштування…", () => OpenSettingsRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateItem("Вихід", () => ExitRequested?.Invoke(this, EventArgs.Empty)));

        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")),
            ToolTipText = "NowPlaying HUD",
            ContextMenu = menu
            // MenuActivation не задаємо: за замовчуванням це і є правий клік,
            // а лівий зайнятий показом HUD.
        };

        _icon.TrayLeftMouseUp += (_, _) => ShowHudRequested?.Invoke(this, EventArgs.Empty);

        // Без ForceCreate іконка з'явиться лише після першого показу вікна,
        // а вікна в цього застосунку на старті немає.
        _icon.ForceCreate();
    }

    /// <summary>Підказка при наведенні — назва треку, що зараз грає.</summary>
    public void SetTooltip(string text)
    {
        // Windows обрізає підказку трея на 127 символах.
        _icon.ToolTipText = text.Length <= 120 ? text : text[..120] + "…";
    }

    /// <summary>
    /// Системне сповіщення. Потрібне рівно один раз — при першому запуску,
    /// бо Windows 11 ховає нові іконки в переповнення трею, і застосунок
    /// без головного вікна виглядає як такий, що не стартував.
    /// </summary>
    public void ShowNotification(string title, string message) =>
        _icon.ShowNotification(title, message);

    private static MenuItem CreateItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Без Dispose іконка лишається висіти в треї до наведення мишею.
        _icon.Dispose();
    }
}
