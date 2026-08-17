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
    private readonly DispatcherTimer _hoverTimer;

    private AppSettings _settings;
    private IntPtr _hwnd;
    private bool _hiding;

    /// <summary>Кліки зараз ловить HUD, а не гра під ним.</summary>
    private bool _interactive;

    /// <summary>Показаний без автоприховування — до явного закриття.</summary>
    private bool _pinnedOpen;

    /// <summary>Курсор зараз над карткою. Тимчасово знімає click-through.</summary>
    private bool _hoverActive;

    /// <summary>Тривалість, з якою треба перезапустити автоприховування після відведення курсора.</summary>
    private TimeSpan? _autoHideDuration;

    public OverlayWindow(OverlayViewModel viewModel, AppSettings settings)
    {
        _viewModel = viewModel;
        _settings = settings;

        InitializeComponent();
        DataContext = viewModel;

        _autoHideTimer = new DispatcherTimer(DispatcherPriority.Normal);
        _autoHideTimer.Tick += (_, _) => HideHud();

        // З увімкненим WS_EX_TRANSPARENT вікно не отримує жодного повідомлення
        // миші — навіть WM_MOUSEMOVE. Тому наведення доводиться саме опитувати.
        // 100 мс достатньо: людина не встигає клікнути швидше, ніж донесе курсор.
        _hoverTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _hoverTimer.Tick += OnHoverTick;

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
        // HUD, який виїхав сам (пасивний показ), хоткеєм не ховається, а стає
        // клікабельним: якщо він уже на екрані і не реагує на мишу, користувач
        // тисне хоткей, щоб ним керувати, а не щоб його прибрати.
        if (IsHudVisible && _interactive)
        {
            HideHud();
            return;
        }

        ShowHud(interactive: true, autoHideAfter: autoHideAfter);
    }

    /// <param name="interactive">true — кнопки клікабельні; false — кліки проходять у гру.</param>
    /// <param name="autoHideAfter">Скільки тримати на екрані; null — до явного приховування.</param>
    public void ShowHud(bool interactive, TimeSpan? autoHideAfter)
    {
        var wasVisible = IsHudVisible;

        // Пасивний показ не має відбирати клікабельність у HUD, який уже висить
        // після ручного виклику. Інакше виходить так: клікаєш "next" мишею,
        // трек змінюється, звідти прилітає пасивний показ з interactive: false,
        // вмикає WS_EX_TRANSPARENT — і наступний клік провалюється крізь оверлей
        // у гру, хоча HUD видно і він виглядає робочим.
        var effectiveInteractive = interactive || (wasVisible && _interactive);

        // Так само пасивний показ не має ставити таймер на HUD, відкритий
        // "до повторного натискання" (hold-to-peek).
        var stayOpen = (wasVisible && _pinnedOpen) || autoHideAfter is null;

        _autoHideTimer.Stop();
        _hiding = false;
        _interactive = effectiveInteractive;
        _pinnedOpen = stayOpen;
        _autoHideDuration = autoHideAfter;

        ApplyClickThrough();

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

        // Пасивний показ лишається наскрізним, але має оживати під курсором:
        // інакше картка з кнопками просто не реагує на клік, і це не відрізнити
        // від зламаного застосунку.
        if (effectiveInteractive)
        {
            _hoverTimer.Stop();
        }
        else
        {
            _hoverTimer.Start();
        }

        // Якщо HUD уже на екрані, повторна анімація виїзду читається як смикання
        // картки на кожній зміні треку.
        if (!wasVisible)
        {
            RunShowAnimation();
        }

        if (!stayOpen && !_hoverActive && autoHideAfter is { } delay)
        {
            _autoHideTimer.Interval = delay;
            _autoHideTimer.Start();
        }
    }

    /// <summary>Кліки ловить HUD, якщо він викликаний вручну або під ним курсор.</summary>
    private void ApplyClickThrough() =>
        NativeMethods.SetClickThrough(_hwnd, clickThrough: !(_interactive || _hoverActive));

    private void OnHoverTick(object? sender, EventArgs e)
    {
        if (!IsHudVisible || _interactive)
        {
            _hoverTimer.Stop();
            return;
        }

        var over = IsCursorOverCard();
        if (over == _hoverActive)
        {
            return;
        }

        _hoverActive = over;
        ApplyClickThrough();

        if (over)
        {
            // Ховати картку з-під курсора, поки людина тягнеться до кнопки, — зле.
            _autoHideTimer.Stop();
        }
        else if (!_pinnedOpen && _autoHideDuration is { } delay)
        {
            _autoHideTimer.Interval = delay;
            _autoHideTimer.Start();
        }
    }

    /// <summary>
    /// Перевіряється саме картка, а не вікно: вікно більше на 20 DIP з кожного
    /// боку — це поле під анімацію виїзду, і воно прозоре.
    /// </summary>
    private bool IsCursorOverCard()
    {
        if (!IsVisible || Hud.ActualWidth <= 0 || !NativeMethods.GetCursorPos(out var cursor))
        {
            return false;
        }

        try
        {
            // PointToScreen віддає фізичні пікселі — саме те, у чому працює GetCursorPos.
            var topLeft = Hud.PointToScreen(new Point(0, 0));
            var bottomRight = Hud.PointToScreen(new Point(Hud.ActualWidth, Hud.ActualHeight));

            return cursor.X >= topLeft.X && cursor.X < bottomRight.X
                && cursor.Y >= topLeft.Y && cursor.Y < bottomRight.Y;
        }
        catch (InvalidOperationException)
        {
            // Вікно могло втратити PresentationSource між перевіркою і викликом.
            return false;
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
        _interactive = false;
        _pinnedOpen = false;
        _hoverActive = false;
        _autoHideDuration = null;

        _hoverTimer.Stop();

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
