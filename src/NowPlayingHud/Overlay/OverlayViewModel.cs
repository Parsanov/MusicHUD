using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NowPlayingHud.Media;

namespace NowPlayingHud.Overlay;

public sealed class OverlayViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>
    /// Прогрес оновлюється раз на 250 мс. Оверлей ділить GPU з грою,
    /// частіше — марно витрачені кадри заради секундної стрілки.
    /// </summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly SmtcService _smtc;
    private readonly SourceIconProvider _icons;
    private readonly DispatcherTimer _progressTimer;

    private readonly RelayCommand _playPause;
    private readonly RelayCommand _next;
    private readonly RelayCommand _previous;

    private TrackInfo _track = TrackInfo.Empty;
    private ImageSource? _cover;
    private byte[]? _coverBytes;
    private ImageSource? _sourceIcon;
    private string _iconAppId = string.Empty;
    private double _progress;
    private string _positionText = "0:00";
    private string _durationText = "0:00";

    public OverlayViewModel(SmtcService smtc, SourceIconProvider icons)
    {
        _smtc = smtc;
        _icons = icons;

        _playPause = new RelayCommand(() => _ = _smtc.TogglePlayPauseAsync(), () => HasTrack);
        _next = new RelayCommand(() => _ = _smtc.NextAsync(), () => HasTrack);
        _previous = new RelayCommand(() => _ = _smtc.PreviousAsync(), () => HasTrack);

        _progressTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ProgressInterval };
        _progressTimer.Tick += (_, _) => UpdateProgress();
    }

    public ICommand PlayPauseCommand => _playPause;
    public ICommand NextCommand => _next;
    public ICommand PreviousCommand => _previous;

    /// <summary>Є чим керувати. Поки немає сесії, кнопки треба гасити.</summary>
    public bool HasTrack => !_track.IsEmpty;

    public string Title => _track.IsEmpty ? "Нічого не грає" : _track.Title;

    /// <summary>«Виконавець — Альбом», або лише виконавець, якщо альбому немає.</summary>
    public string Subtitle
    {
        get
        {
            if (_track.IsEmpty)
            {
                // Коротко: довший текст обрізається трьома крапками на 360 px.
                return "Постав щось на відтворення";
            }

            var artist = _track.Artist;
            var album = _track.Album;

            if (string.IsNullOrWhiteSpace(album) || string.Equals(album, artist, StringComparison.Ordinal))
            {
                return artist;
            }

            return string.IsNullOrWhiteSpace(artist) ? album : $"{artist} — {album}";
        }
    }

    public ImageSource? Cover => _cover;

    public bool HasCover => _cover is not null;

    public ImageSource? SourceIcon => _sourceIcon;

    public bool HasSourceIcon => _sourceIcon is not null;

    public bool IsPlaying => _track.IsPlaying;

    public bool HasTimeline => _track.HasTimeline;

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public string PositionText
    {
        get => _positionText;
        private set => Set(ref _positionText, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => Set(ref _durationText, value);
    }

    /// <summary>Викликається лише з UI-потоку (маршалізація — в OverlayWindow).</summary>
    public void Apply(TrackInfo track)
    {
        var coverChanged = !ReferenceEquals(_coverBytes, track.Thumbnail);
        _track = track;

        if (coverChanged)
        {
            _coverBytes = track.Thumbnail;
            _cover = DecodeImage(track.Thumbnail, decodePixelWidth: 128);   // 64 px на екрані * 2
            Raise(nameof(Cover));
            Raise(nameof(HasCover));
        }

        if (!string.Equals(_iconAppId, track.SourceAppId, StringComparison.OrdinalIgnoreCase))
        {
            _iconAppId = track.SourceAppId;
            _ = LoadSourceIconAsync(track.SourceAppId);
        }

        Raise(nameof(Title));
        Raise(nameof(Subtitle));
        Raise(nameof(IsPlaying));
        Raise(nameof(HasTimeline));
        Raise(nameof(HasTrack));

        _playPause.RaiseCanExecuteChanged();
        _next.RaiseCanExecuteChanged();
        _previous.RaiseCanExecuteChanged();

        UpdateProgress();
    }

    /// <summary>
    /// Іконка джерела читається поза UI-потоком: перший раз це звернення
    /// до диска або до реєстру MSIX-пакетів.
    /// </summary>
    private async Task LoadSourceIconAsync(string appId)
    {
        var bytes = await _icons.GetIconAsync(appId).ConfigureAwait(false);

        // Поки читали, трек міг переїхати на джерело з іншим AUMID.
        if (!string.Equals(_iconAppId, appId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var image = DecodeImage(bytes, decodePixelWidth: 32);   // бейдж 14 px * 2

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _sourceIcon = image;
            Raise(nameof(SourceIcon));
            Raise(nameof(HasSourceIcon));
        });
    }

    /// <summary>Таймер прогресу крутиться лише поки HUD видно.</summary>
    public void StartProgressUpdates()
    {
        UpdateProgress();
        _progressTimer.Start();
    }

    public void StopProgressUpdates() => _progressTimer.Stop();

    private void UpdateProgress()
    {
        var position = _track.EstimatePosition(DateTimeOffset.UtcNow);
        var duration = _track.Duration;

        Progress = duration > TimeSpan.Zero
            ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0.0, 1.0)
            : 0.0;

        PositionText = Format(position);
        DurationText = Format(duration);
    }

    private static string Format(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{(int)value.TotalMinutes}:{value.Seconds:00}";

    /// <summary>
    /// BitmapCacheOption.OnLoad обов'язковий: без нього WPF відкладає декодування,
    /// а MemoryStream до того моменту вже закритий.
    /// Freeze() дозволяє віддати картинку в UI-потік з будь-якого іншого.
    /// </summary>
    private static ImageSource? DecodeImage(byte[]? bytes, int decodePixelWidth)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.DecodePixelWidth = decodePixelWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Overlay] картинка не декодувалась: {ex.Message}");
            return null;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        Raise(propertyName);
    }

    public void Dispose() => _progressTimer.Stop();
}

/// <summary>
/// Вбудований BooleanToVisibilityConverter не вміє інвертувати,
/// а заглушка обкладинки і кнопка Play показуються саме за false.
/// </summary>
public sealed class InverseBoolToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is Visibility.Collapsed;
}

/// <summary>Мінімальна команда. Ставити сюди CommunityToolkit заради трьох кнопок — зайве.</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            execute();
        }
    }

    /// <summary>Button сам підхопить це через IsEnabled — додаткових біндингів не треба.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
