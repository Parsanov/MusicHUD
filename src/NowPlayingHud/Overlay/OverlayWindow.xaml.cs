using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NowPlayingHud.Interop;
using NowPlayingHud.Media;
using NowPlayingHud.Settings;

namespace NowPlayingHud.Overlay;

public partial class OverlayWindow : Window
{
    /// <summary>Наскільки HUD «виїжджає» від краю, у DIP.</summary>
    private const double SlideDistance = 14;

    private static readonly TimeSpan ShowDuration = TimeSpan.FromMilliseconds(170);
    private static readonly TimeSpan HideDuration = TimeSpan.FromMilliseconds(120);

    private readonly OverlayViewModel _viewModel;
    private readonly DispatcherTimer _autoHideTimer;

    private AppSettings _settings;
    private IntPtr _hwnd;
    private bool _hiding;

    public OverlayWindow(OverlayViewModel viewModel, AppSettings settings)
    {
        _viewModel = viewModel;
        _settings = settings;

        InitializeComponent();
        DataContext = viewModel;

        _autoHideTimer = new DispatcherTimer(DispatcherPriority.Normal);
        _autoHideTimer.Tick += (_, _) => HideHud();

        ApplySettings(settings);
    }

    /// <summary>HUD зараз на екрані (або саме зараз виїжджає).</summary>
    public bool IsHudVisible { get; private set; }

    // ------------------------------------------------------------------
    // Створення та прогрів
    // ------------------------------------------------------------------

    /// <summary>
    /// Вікно живе весь час роботи застосунку. Створення вікна WPF і перший
    /// кадр рендера разом коштують 50–100 мс — це весь бюджет появи,
    /// тому платимо за них один раз на старті.
    /// </summary>
    public void WarmUp()
    {
        // EnsureHandle створює HWND без показу вікна — саме тут спрацює
        // OnSourceInitialized і накотяться потрібні стилі.
        _hwnd = new WindowInteropHelper(this).EnsureHandle();

        // Показуємо один раз, щоб WPF змалював вміст і виміряв SizeToContent.
        // Hud.Opacity = 0 у XAML, тому на екрані нічого не з'явиться.
        Show();

        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (!IsHudVisible)
            {
                Hide();
            }
        });
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;

        // З XAML ці стилі не задаються, тільки через P/Invoke:
        // TOOLWINDOW прибирає вікно з Alt+Tab і панелі задач,
        // NOACTIVATE не дає забрати фокус у гри при показі.
        NativeMethods.AddWindowExStyle(_hwnd, WindowStyles.WS_EX_TOOLWINDOW | WindowStyles.WS_EX_NOACTIVATE);

        // Стартуємо як «наскрізне» вікно: у пасивному режимі кліки мають
        // йти в гру, а не в оверлей.
        NativeMethods.SetClickThrough(_hwnd, clickThrough: true);
    }

    // ------------------------------------------------------------------
    // Налаштування
    // ------------------------------------------------------------------

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;

        HudScale.ScaleX = settings.Scale;
        HudScale.ScaleY = settings.Scale;

        // Розмір міг змінитись — перерахувати позицію, поки HUD видно.
        if (IsHudVisible)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Reposition());
        }
    }

    /// <summary>Показ на кілька секунд для попереднього перегляду кута в налаштуваннях.</summary>
    public void PreviewCorner() => ShowHud(interactive: false, autoHideAfter: TimeSpan.FromSeconds(2));

    // ------------------------------------------------------------------
    // Дані
    // ------------------------------------------------------------------

    public void UpdateTrack(TrackInfo track)
    {
        if (Dispatcher.CheckAccess())
        {
            _viewModel.Apply(track);
        }
        else
        {
            // Події SMTC приходять з пула потоків.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _viewModel.Apply(track));
        }
    }

    // ------------------------------------------------------------------
    // Показ і приховування
    // ------------------------------------------------------------------

    /// <param name="autoHideAfter">Скільки тримати на екрані; null — до повторного натискання.</param>
    public void ToggleHud(TimeSpan? autoHideAfter = null)
    {
        if (IsHudVisible)
        {
            HideHud();
        }
        else
        {
            ShowHud(interactive: true, autoHideAfter: autoHideAfter);
        }
    }

    /// <param name="interactive">true — кнопки клікабельні; false — кліки проходять у гру.</param>
    /// <param name="autoHideAfter">Скільки тримати на екрані; null — до явного приховування.</param>
    public void ShowHud(bool interactive, TimeSpan? autoHideAfter)
    {
        _autoHideTimer.Stop();
        _hiding = false;

        NativeMethods.SetClickThrough(_hwnd, clickThrough: !interactive);

        if (!IsVisible)
        {
            Show();
        }

        Reposition();

        // Ігри регулярно перебивають z-order, тому topmost підтверджується
        // на кожному показі, а не один раз при створенні.
        NativeMethods.ForceTopMost(_hwnd);

        IsHudVisible = true;
        _viewModel.StartProgressUpdates();

        RunShowAnimation();

        if (autoHideAfter is { } delay)
        {
            _autoHideTimer.Interval = delay;
            _autoHideTimer.Start();
        }
    }

    public void HideHud()
    {
        _autoHideTimer.Stop();

        if (!IsHudVisible || _hiding)
        {
            return;
        }

        _hiding = true;
        IsHudVisible = false;

        RunHideAnimation();
    }

    private void Reposition()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        // Розміри в DIP; у фізичні пікселі їх переводить позиціонер
        // за DPI саме того монітора, куди вікно поїде.
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;

        OverlayPositioner.Place(_hwnd, width, height, _settings.Corner, _settings.MarginDip);
    }

    // ------------------------------------------------------------------
    // Анімації
    // ------------------------------------------------------------------

    private void RunShowAnimation()
    {
        var (offsetX, offsetY) = OverlayPositioner.SlideOffset(_settings.Corner, SlideDistance);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        Hud.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = 1.0,
            Duration = ShowDuration,
            EasingFunction = ease
        });

        HudSlide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new DoubleAnimation
        {
            From = offsetX,
            To = 0,
            Duration = ShowDuration,
            EasingFunction = ease
        });

        HudSlide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation
        {
            From = offsetY,
            To = 0,
            Duration = ShowDuration,
            EasingFunction = ease
        });
    }

    private void RunHideAnimation()
    {
        var (offsetX, offsetY) = OverlayPositioner.SlideOffset(_settings.Corner, SlideDistance);

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };

        var fade = new DoubleAnimation
        {
            To = 0.0,
            Duration = HideDuration,
            EasingFunction = ease
        };

        fade.Completed += (_, _) =>
        {
            // За час анімації користувач міг натиснути хоткей ще раз.
            if (!IsHudVisible)
            {
                Hide();
                _viewModel.StopProgressUpdates();
            }

            _hiding = false;
        };

        Hud.BeginAnimation(OpacityProperty, fade);

        HudSlide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new DoubleAnimation
        {
            To = offsetX,
            Duration = HideDuration,
            EasingFunction = ease
        });

        HudSlide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation
        {
            To = offsetY,
            Duration = HideDuration,
            EasingFunction = ease
        });
    }

    /// <summary>Закриття вікна = вихід із застосунку, тому Alt+F4 просто ховає HUD.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!ApplicationIsShuttingDown)
        {
            e.Cancel = true;
            HideHud();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>Ставиться в true перед справжнім виходом.</summary>
    public bool ApplicationIsShuttingDown { get; set; }
}
