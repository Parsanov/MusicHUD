using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NowPlayingHud.Input;
using NowPlayingHud.Media;
using NowPlayingHud.Settings;

namespace NowPlayingHud.Configuration;

/// <summary>
/// Що саме змінилось — щоб не смикати зайве через кожен піксель повзунка.
/// Хоткеї сюди не входять: вони перевʼязуються одразу, у момент натискання.
/// </summary>
[Flags]
public enum SettingsChange
{
    None = 0,
    Hotkeys = 1,
    Appearance = 2,
    Source = 4,
    Startup = 8
}

public partial class SettingsWindow : Window
{
    private const string AutoSourceLabel = "Автоматично";

    private readonly AppSettings _settings;
    private readonly SmtcService _smtc;
    private readonly string _settingsPath;

    /// <summary>
    /// Перевʼязування хоткея. Вікно не знає про HotkeyService — воно лише
    /// питає дозволу і за false відкочує поле назад.
    /// </summary>
    private readonly Func<HotkeyAction, HotkeyBinding, bool> _tryRebind;

    private bool _loading = true;

    /// <summary>Усі зміни застосовуються на льоту — перезапуск не потрібен.</summary>
    public event EventHandler<SettingsChange>? Changed;

    public SettingsWindow(
        AppSettings settings,
        SmtcService smtc,
        string settingsPath,
        Func<HotkeyAction, HotkeyBinding, bool> tryRebind)
    {
        _settings = settings;
        _smtc = smtc;
        _settingsPath = settingsPath;
        _tryRebind = tryRebind;

        InitializeComponent();
        LoadFromSettings();

        // Список джерел має жити: користувач відкриє налаштування, потім
        // запустить Spotify і чекатиме, що той сам з'явиться у випадайці.
        _smtc.SessionsChanged += OnSessionsChanged;
        Closed += (_, _) => _smtc.SessionsChanged -= OnSessionsChanged;

        _loading = false;
    }

    private void LoadFromSettings()
    {
        _loading = true;

        ToggleHotkeyBox.Text = _settings.GetBinding(HotkeyAction.ToggleHud).ToString();
        PlayPauseHotkeyBox.Text = _settings.GetBinding(HotkeyAction.PlayPause).ToString();
        NextHotkeyBox.Text = _settings.GetBinding(HotkeyAction.Next).ToString();
        PreviousHotkeyBox.Text = _settings.GetBinding(HotkeyAction.Previous).ToString();

        ToggleModeRadio.IsChecked = _settings.Mode == HudMode.Toggle;
        HoldModeRadio.IsChecked = _settings.Mode == HudMode.HoldToPeek;

        CornerTopLeft.IsChecked = _settings.Corner == OverlayCorner.TopLeft;
        CornerTopRight.IsChecked = _settings.Corner == OverlayCorner.TopRight;
        CornerBottomLeft.IsChecked = _settings.Corner == OverlayCorner.BottomLeft;
        CornerBottomRight.IsChecked = _settings.Corner == OverlayCorner.BottomRight;

        MarginSlider.Value = _settings.MarginDip;
        MarginValueText.Text = $"{_settings.MarginDip} px";

        PassiveCheck.IsChecked = _settings.PassiveHudEnabled;
        PassiveSecondsSlider.Value = _settings.PassiveHudSeconds;
        PassiveSecondsText.Text = $"{_settings.PassiveHudSeconds:0.#} с";

        ScaleSlider.Value = _settings.Scale;
        ScaleValueText.Text = $"{_settings.Scale * 100:0} %";

        StartupCheck.IsChecked = _settings.RunAtStartup;
        StartupCheck.IsEnabled = StartupRegistration.IsSupported;
        if (!StartupRegistration.IsSupported)
        {
            StartupCheck.Content = "Запускати разом з Windows (у MSIX керується системою)";
        }

        PopulateSources();

        SettingsPathText.Text = _settingsPath;
        SettingsPathText.ToolTip = _settingsPath;

        _loading = false;
    }

    // ------------------------------------------------------------------
    // Джерела
    // ------------------------------------------------------------------

    private void OnSessionsChanged(object? sender, EventArgs e) =>
        // SessionsChanged приходить з пула потоків.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => PopulateSources());

    private void PopulateSources()
    {
        // Перезаповнення списку смикає SelectionChanged; без цієї заглушки
        // воно виглядає як вибір користувача і пише налаштування на кожну
        // появу нової медіасесії в системі.
        var wasLoading = _loading;
        _loading = true;

        try
        {
            var options = new List<SourceOption> { new(null, AutoSourceLabel) };

            foreach (var session in _smtc.GetSessions())
            {
                options.Add(new SourceOption(session.AppId, session.DisplayName));
            }

            // Закріплене джерело зараз не запущене — все одно показуємо його,
            // інакше вибір мовчки злетить на «Автоматично».
            if (_settings.PinnedSourceAppId is { } pinned &&
                options.All(option => !string.Equals(option.AppId, pinned, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new SourceOption(pinned, $"{SmtcService.PrettifyAppId(pinned)} (не запущено)"));
            }

            SourceCombo.ItemsSource = options;
            SourceCombo.SelectedItem = options.FirstOrDefault(option =>
                string.Equals(option.AppId, _settings.PinnedSourceAppId, StringComparison.OrdinalIgnoreCase))
                ?? options[0];
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SourceCombo.SelectedItem is not SourceOption option)
        {
            return;
        }

        _settings.PinnedSourceAppId = option.AppId;
        Changed?.Invoke(this, SettingsChange.Source);
    }

    // ------------------------------------------------------------------
    // Хоткеї
    // ------------------------------------------------------------------

    private void OnHotkeyBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        e.Handled = true;

        var action = ResolveAction(box);
        var previous = _settings.GetBinding(action);

        if (e.Key == Key.Escape)
        {
            _tryRebind(action, HotkeyBinding.None);
            _settings.SetBinding(action, HotkeyBinding.None);
            box.Text = HotkeyBinding.None.ToString();
            ClearConflict();
            Changed?.Invoke(this, SettingsChange.Hotkeys);
            return;
        }

        // При Alt+X WPF кладе справжню клавішу в SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (HotkeyBinding.IsModifierKey(key))
        {
            return;   // чекаємо на «справжню» клавішу
        }

        var binding = HotkeyBinding.FromKeyEvent(key, Keyboard.Modifiers);

        if (binding.Modifiers == HotkeyModifiers.None)
        {
            ShowConflict("Комбінація без модифікатора перехопить клавішу в усій системі. Додай Ctrl, Alt, Shift або Win.");
            return;
        }

        // Реєструємо одразу: якщо комбінацію хтось тримає, налаштування
        // не мають про це знати — інакше в конфігу лишиться хоткей,
        // якого насправді немає.
        if (!_tryRebind(action, binding))
        {
            ShowConflict($"{binding} уже зайнята іншою програмою. Залишено {previous}.");
            box.Text = previous.ToString();
            return;
        }

        _settings.SetBinding(action, binding);
        box.Text = binding.ToString();
        ClearConflict();
        Changed?.Invoke(this, SettingsChange.Hotkeys);
    }

    private HotkeyAction ResolveAction(TextBox box)
    {
        if (ReferenceEquals(box, ToggleHotkeyBox)) return HotkeyAction.ToggleHud;
        if (ReferenceEquals(box, PlayPauseHotkeyBox)) return HotkeyAction.PlayPause;
        if (ReferenceEquals(box, NextHotkeyBox)) return HotkeyAction.Next;
        return HotkeyAction.Previous;
    }

    public void ShowConflict(string message)
    {
        ConflictText.Text = message;
        ConflictText.Visibility = Visibility.Visible;
    }

    public void ClearConflict() => ConflictText.Visibility = Visibility.Collapsed;

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.Mode = HoldModeRadio.IsChecked == true ? HudMode.HoldToPeek : HudMode.Toggle;
        Changed?.Invoke(this, SettingsChange.Hotkeys);
    }

    // ------------------------------------------------------------------
    // Вигляд
    // ------------------------------------------------------------------

    private void OnCornerChecked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        if (Enum.TryParse<OverlayCorner>(tag, out var corner))
        {
            _settings.Corner = corner;
            Changed?.Invoke(this, SettingsChange.Appearance);
        }
    }

    private void OnMarginChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _settings.MarginDip = (int)Math.Round(e.NewValue);
        MarginValueText.Text = $"{_settings.MarginDip} px";
        Changed?.Invoke(this, SettingsChange.Appearance);
    }

    private void OnScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _settings.Scale = Math.Round(e.NewValue, 2);
        ScaleValueText.Text = $"{_settings.Scale * 100:0} %";
        Changed?.Invoke(this, SettingsChange.Appearance);
    }

    private void OnPassiveChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.PassiveHudEnabled = PassiveCheck.IsChecked == true;
        Changed?.Invoke(this, SettingsChange.Appearance);
    }

    private void OnPassiveSecondsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _settings.PassiveHudSeconds = Math.Round(e.NewValue, 1);
        PassiveSecondsText.Text = $"{_settings.PassiveHudSeconds:0.#} с";
        Changed?.Invoke(this, SettingsChange.Appearance);
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.RunAtStartup = StartupCheck.IsChecked == true;
        Changed?.Invoke(this, SettingsChange.Startup);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private sealed record SourceOption(string? AppId, string Label)
    {
        public override string ToString() => Label;
    }
}
