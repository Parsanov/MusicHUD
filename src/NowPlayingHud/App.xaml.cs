using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using NowPlayingHud.Configuration;
using NowPlayingHud.Input;
using NowPlayingHud.Media;
using NowPlayingHud.Overlay;
using NowPlayingHud.Settings;
using NowPlayingHud.Tray;

namespace NowPlayingHud;

/// <summary>
/// Точка входу і композиція сервісів.
///
/// DI-контейнера немає свідомо: об'єктів рівно вісім, усі створюються тут
/// в одному місці і живуть до виходу.
/// </summary>
public partial class App : Application
{
    private const string InstanceMutexName = @"Local\NowPlayingHud.SingleInstance";

    /// <summary>Короткий показ після перемикання треку хоткеєм — щоб бачити, що команда дійшла.</summary>
    private static readonly TimeSpan FlashDuration = TimeSpan.FromSeconds(3);

    private Mutex? _instanceMutex;
    private JsonSettingsStore _store = null!;
    private AppSettings _settings = null!;
    private SmtcService _smtc = null!;
    private SourceIconProvider _icons = null!;
    private HotkeyService _hotkeys = null!;
    private OverlayViewModel _viewModel = null!;
    private OverlayWindow _overlay = null!;
    private TrayIconHost _tray = null!;

    private SettingsWindow? _settingsWindow;
    private IReadOnlyList<HotkeyConflict> _conflicts = Array.Empty<HotkeyConflict>();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Друга копія перехопить ті самі хоткеї і покаже другий HUD.
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Мьютекс не наш — звільняти його в OnExit не можна, інакше
            // ReleaseMutex кине ApplicationException.
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        _store = new JsonSettingsStore();
        _settings = _store.Load();

        _smtc = new SmtcService { PinnedAppId = _settings.PinnedSourceAppId };
        _icons = new SourceIconProvider();

        _viewModel = new OverlayViewModel(_smtc, _icons);
        _overlay = new OverlayWindow(_viewModel, _settings);
        _overlay.WarmUp();

        _hotkeys = new HotkeyService();
        _hotkeys.Pressed += OnHotkeyPressed;
        _hotkeys.HudKeyReleased += (_, _) => _overlay.HideHud();

        _tray = new TrayIconHost();
        _tray.ShowHudRequested += (_, _) =>
            _overlay.ShowHud(interactive: true, autoHideAfter: _settings.PassiveHudDuration);
        _tray.OpenSettingsRequested += (_, _) => OpenSettings();
        _tray.ExitRequested += (_, _) => ExitApplication();

        _smtc.Updated += OnSmtcUpdated;
        _smtc.TrackChanged += OnSmtcTrackChanged;

        _conflicts = _hotkeys.Apply(_settings.BuildBindings());
        StartupRegistration.Set(_settings.RunAtStartup);

        _ = InitializeMediaAsync();

        // Замість майстра першого запуску: один показ HUD із підказкою.
        if (_store.IsFirstRun)
        {
            ShowFirstRunHint();
        }

        if (_conflicts.Count > 0)
        {
            // Хоткеї не працюють — це той рідкісний випадок, коли налаштування
            // варто відкрити самому, не чекаючи, поки користувач здогадається.
            OpenSettings();
        }
    }

    private async Task InitializeMediaAsync()
    {
        try
        {
            await _smtc.InitializeAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] SMTC не піднявся: {ex.Message}");

            MessageBox.Show(
                "Не вдалося підключитися до системних медіаконтролів Windows.\n" +
                "HUD запущений, але даних про трек не буде.",
                "NowPlaying HUD",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------
    // SMTC -> UI
    // ------------------------------------------------------------------

    private void OnSmtcUpdated(object? sender, TrackInfo track)
    {
        _overlay.UpdateTrack(track);

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _tray.SetTooltip(track.IsEmpty
                ? "NowPlaying HUD"
                : $"{track.Title}\n{track.Artist}");
        });
    }

    private void OnSmtcTrackChanged(object? sender, TrackInfo track)
    {
        if (!_settings.PassiveHudEnabled || track.IsEmpty)
        {
            return;
        }

        // Пасивний показ: кліки мають проходити крізь HUD у гру,
        // тому interactive: false.
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            _overlay.ShowHud(interactive: false, autoHideAfter: _settings.PassiveHudDuration));
    }

    // ------------------------------------------------------------------
    // Хоткеї
    // ------------------------------------------------------------------

    private void OnHotkeyPressed(object? sender, HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleHud:
                if (_settings.Mode == HudMode.HoldToPeek)
                {
                    _overlay.ShowHud(interactive: true, autoHideAfter: null);
                    _hotkeys.BeginHoldWatch();
                }
                else
                {
                    _overlay.ToggleHud(_settings.PassiveHudDuration);
                }
                break;

            case HotkeyAction.PlayPause:
                _ = _smtc.TogglePlayPauseAsync();
                FlashHud();
                break;

            case HotkeyAction.Next:
                _ = _smtc.NextAsync();
                FlashHud();
                break;

            case HotkeyAction.Previous:
                _ = _smtc.PreviousAsync();
                FlashHud();
                break;
        }
    }

    /// <summary>
    /// Після перемикання треку хоткеєм HUD показується коротко сам:
    /// інакше незрозуміло, чи команда взагалі дійшла. Тривалість своя,
    /// не з налаштувань — 15 секунд тут були б перебором.
    /// </summary>
    private void FlashHud()
    {
        if (_overlay.IsHudVisible)
        {
            return;
        }

        _overlay.ShowHud(interactive: false, autoHideAfter: FlashDuration);
    }

    /// <summary>
    /// Перевʼязування хоткея на вимогу вікна налаштувань.
    /// false = комбінація зайнята, стара при цьому вже відновлена.
    /// </summary>
    private bool TryRebindHotkey(HotkeyAction action, HotkeyBinding binding)
    {
        var ok = _hotkeys.TryRebind(action, binding);

        if (ok)
        {
            // Список стартових конфліктів більше не актуальний для цієї дії.
            _conflicts = _conflicts.Where(conflict => conflict.Action != action).ToList();
        }

        return ok;
    }

    // ------------------------------------------------------------------
    // Налаштування
    // ------------------------------------------------------------------

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings, _smtc, _store.FilePath, TryRebindHotkey);
        _settingsWindow.Changed += OnSettingsChanged;
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            _store.SaveNow(_settings);
        };

        _settingsWindow.Show();

        if (_conflicts.Count > 0)
        {
            var list = string.Join(", ", _conflicts.Select(conflict => conflict.Binding.ToString()));
            _settingsWindow.ShowConflict($"Ці комбінації зайняті іншою програмою і не працюють: {list}");
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChange change)
    {
        // SettingsChange.Hotkeys сюди більше не тягне перереєстрацію:
        // перевʼязування вже сталося в TryRebindHotkey, а режим показу
        // (Toggle / HoldToPeek) читається в момент натискання.

        if (change.HasFlag(SettingsChange.Appearance))
        {
            _overlay.ApplySettings(_settings);
            _overlay.PreviewCorner();   // одразу показати, куди переїхав HUD
        }

        if (change.HasFlag(SettingsChange.Source))
        {
            _smtc.PinnedAppId = _settings.PinnedSourceAppId;
        }

        if (change.HasFlag(SettingsChange.Startup))
        {
            StartupRegistration.Set(_settings.RunAtStartup);
        }

        _store.SaveDeferred(_settings);
    }

    private void ShowFirstRunHint()
    {
        var hotkey = _settings.GetBinding(HotkeyAction.ToggleHud);

        _overlay.ShowHud(interactive: false, autoHideAfter: TimeSpan.FromSeconds(6));
        _tray.SetTooltip($"NowPlaying HUD — показати: {hotkey}");

        _tray.ShowNotification(
            "NowPlaying HUD запущено",
            $"Показати HUD: {hotkey}. Іконку можна перетягнути з переповнення трею на панель задач.");
    }

    // ------------------------------------------------------------------
    // Вихід
    // ------------------------------------------------------------------

    private void ExitApplication()
    {
        _store.SaveNow(_settings);
        _overlay.ApplicationIsShuttingDown = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _smtc?.Dispose();
        _viewModel?.Dispose();
        _tray?.Dispose();
        _store?.Dispose();

        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();

        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Debug.WriteLine($"[App] необроблений виняток: {e.Exception}");

        MessageBox.Show(
            $"Сталася помилка:\n{e.Exception.Message}",
            "NowPlaying HUD",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Фонова утиліта не має падати через одну невдалу операцію.
        e.Handled = true;
    }
}
