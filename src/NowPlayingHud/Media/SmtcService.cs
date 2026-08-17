using System.Diagnostics;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace NowPlayingHud.Media;

/// <summary>
/// Ядро продукту: обгортка над SMTC.
///
/// Свідомо нічого не знає ні про WPF, ні про Dispatcher. Дві причини:
/// 1) його можна перевірити консольним застосунком на 30 рядків, поки оверлея
///    ще не існує (див. tools/SmtcProbe);
/// 2) це шов для майбутнього Linux-порту — там на його місце стане MprisService.
///
/// Події летять з потоків пула. Маршалізація в UI — відповідальність підписника.
/// </summary>
public sealed class SmtcService : IDisposable
{
    /// <summary>
    /// Spotify на один трек шле 3–4 події поспіль. Без склеювання HUD
    /// встигає перемалюватись кілька разів і читає обкладинку щоразу.
    /// </summary>
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(60);

    private readonly object _sync = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private CancellationTokenSource? _coalesceCts;
    private string? _pinnedAppId;
    private bool _disposed;

    /// <summary>Останній відомий стан. Читається з будь-якого потоку.</summary>
    public TrackInfo Current { get; private set; } = TrackInfo.Empty;

    /// <summary>Будь-яка зміна: метадані, статус, таймлайн.</summary>
    public event EventHandler<TrackInfo>? Updated;

    /// <summary>Змінився саме трек — тригер для пасивного HUD.</summary>
    public event EventHandler<TrackInfo>? TrackChanged;

    /// <summary>Змінився склад живих сесій — оновити випадайку джерел.</summary>
    public event EventHandler? SessionsChanged;

    /// <summary>AUMID закріпленого джерела; null — автовибір активної сесії.</summary>
    public string? PinnedAppId
    {
        get => _pinnedAppId;
        set
        {
            if (string.Equals(_pinnedAppId, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _pinnedAppId = string.IsNullOrWhiteSpace(value) ? null : value;
            AttachSession(ResolveSession());
            ScheduleRefresh();
        }
    }

    public async Task InitializeAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        _manager.SessionsChanged += OnManagerSessionsChanged;

        AttachSession(ResolveSession());
        await RefreshAsync().ConfigureAwait(false);
    }

    /// <summary>Живі сесії для випадайки «закріпити застосунок».</summary>
    public IReadOnlyList<SessionDescriptor> GetSessions()
    {
        var manager = _manager;
        if (manager is null)
        {
            return Array.Empty<SessionDescriptor>();
        }

        var result = new List<SessionDescriptor>();

        foreach (var session in manager.GetSessions())
        {
            var appId = session.SourceAppUserModelId;
            if (string.IsNullOrWhiteSpace(appId))
            {
                continue;
            }

            result.Add(new SessionDescriptor(appId, PrettifyAppId(appId)));
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Керування
    // ------------------------------------------------------------------

    public Task<bool> TogglePlayPauseAsync() => InvokeAsync(s => s.TryTogglePlayPauseAsync());

    public Task<bool> NextAsync() => InvokeAsync(s => s.TrySkipNextAsync());

    public Task<bool> PreviousAsync() => InvokeAsync(s => s.TrySkipPreviousAsync());

    private async Task<bool> InvokeAsync(Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> action)
    {
        var session = _session;
        if (session is null)
        {
            return false;
        }

        try
        {
            return await action(session).AsTask().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Сесія могла померти між читанням поля і викликом — це нормальний стан.
            Debug.WriteLine($"[SMTC] команда не пройшла: {ex.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Підписки
    // ------------------------------------------------------------------

    private GlobalSystemMediaTransportControlsSession? ResolveSession()
    {
        var manager = _manager;
        if (manager is null)
        {
            return null;
        }

        var pinned = _pinnedAppId;

        if (pinned is not null)
        {
            foreach (var session in manager.GetSessions())
            {
                if (string.Equals(session.SourceAppUserModelId, pinned, StringComparison.OrdinalIgnoreCase))
                {
                    return session;
                }
            }

            // Закріплений застосунок не запущений — тихо падаємо на активну сесію.
            // Компроміс свідомий: закріплення розводить одночасні джерела
            // (Spotify грає + вкладка на паузі), а не блокує HUD, коли
            // закріпленого немає в системі взагалі.
        }

        return manager.GetCurrentSession();
    }

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_session, session))
            {
                return;
            }

            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            }

            _session = session;

            if (_session is not null)
            {
                _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            }
        }
    }

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        // При закріпленому джерелі зміна активної сесії нас не стосується.
        if (_pinnedAppId is null)
        {
            AttachSession(ResolveSession());
            ScheduleRefresh();
        }
    }

    private void OnManagerSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        // Закріплений застосунок міг щойно запуститись або закритись.
        if (_pinnedAppId is not null)
        {
            AttachSession(ResolveSession());
            ScheduleRefresh();
        }

        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => ScheduleRefresh();

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => ScheduleRefresh();

    private void OnTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => ScheduleRefresh();

    // ------------------------------------------------------------------
    // Оновлення стану
    // ------------------------------------------------------------------

    private void ScheduleRefresh()
    {
        if (_disposed)
        {
            return;
        }

        CancellationTokenSource cts;

        lock (_sync)
        {
            _coalesceCts?.Cancel();
            _coalesceCts?.Dispose();
            _coalesceCts = cts = new CancellationTokenSource();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(CoalesceWindow, cts.Token).ConfigureAwait(false);
                await RefreshAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Прийшла свіжіша подія — цей прохід більше не потрібен.
            }
        });
    }

    public async Task RefreshAsync()
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);

        try
        {
            var session = _session;
            var previous = Current;

            var next = session is null
                ? TrackInfo.Empty
                : await BuildTrackInfoAsync(session, previous).ConfigureAwait(false);

            Current = next;

            if (!next.IsSameTrackAs(previous))
            {
                TrackChanged?.Invoke(this, next);
            }

            Updated?.Invoke(this, next);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SMTC] не вдалося оновити стан: {ex.Message}");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static async Task<TrackInfo> BuildTrackInfoAsync(
        GlobalSystemMediaTransportControlsSession session, TrackInfo previous)
    {
        var properties = await session.TryGetMediaPropertiesAsync().AsTask().ConfigureAwait(false);
        var playback = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();

        var title = properties?.Title ?? string.Empty;
        var artist = properties?.Artist ?? string.Empty;

        // Обкладинка читається тільки коли трек справді змінився:
        // на pause/play перечитувати той самий потік нема сенсу.
        var sameTrack =
            string.Equals(title, previous.Title, StringComparison.Ordinal) &&
            string.Equals(artist, previous.Artist, StringComparison.Ordinal);

        var thumbnail = sameTrack && previous.Thumbnail is not null
            ? previous.Thumbnail
            : await ReadThumbnailAsync(properties?.Thumbnail).ConfigureAwait(false);

        return new TrackInfo(
            Title: title,
            Artist: artist,
            Album: properties?.AlbumTitle ?? string.Empty,
            SourceAppId: session.SourceAppUserModelId ?? string.Empty,
            Status: MapStatus(playback?.PlaybackStatus),
            Position: timeline.Position,
            Duration: timeline.EndTime - timeline.StartTime,
            PositionUpdatedAt: timeline.LastUpdatedTime,
            Thumbnail: thumbnail);
    }

    /// <summary>
    /// Обкладинка приходить як IRandomAccessStreamReference. Читається одразу
    /// в масив байтів: тримати відкритий WinRT-потік до моменту, коли WPF
    /// збереться його декодувати, не можна — його закриють раніше.
    /// Роздільна здатність у SMTC низька, але для 64 px цього рівно достатньо.
    /// </summary>
    private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null)
        {
            return null;
        }

        try
        {
            using var stream = await reference.OpenReadAsync().AsTask().ConfigureAwait(false);

            if (stream.Size == 0 || stream.Size > 8 * 1024 * 1024)   // 8 МБ — захист від сміття в потоці
            {
                return null;
            }

            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size).AsTask().ConfigureAwait(false);

            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            // Браузери регулярно віддають биті або вже закриті посилання.
            Debug.WriteLine($"[SMTC] обкладинка не прочиталась: {ex.Message}");
            return null;
        }
    }

    private static PlaybackStatus MapStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus? status) =>
        status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => PlaybackStatus.Closed,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => PlaybackStatus.Opened,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => PlaybackStatus.Changing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => PlaybackStatus.Stopped,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackStatus.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackStatus.Paused,
            _ => PlaybackStatus.Unknown
        };

    /// <summary>
    /// AUMID у людський вигляд:
    ///   "Spotify.exe"                                        -> "Spotify"
    ///   "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" -> "ZuneMusic"
    /// </summary>
    public static string PrettifyAppId(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return "(невідоме джерело)";
        }

        var value = appId;

        var bang = value.IndexOf('!');
        if (bang >= 0)
        {
            var afterBang = value[(bang + 1)..];
            var dot = afterBang.LastIndexOf('.');
            return dot >= 0 && dot < afterBang.Length - 1 ? afterBang[(dot + 1)..] : afterBang;
        }

        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        return value.Length == 0 ? appId : char.ToUpperInvariant(value[0]) + value[1..];
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        AttachSession(null);

        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnManagerSessionsChanged;
            _manager = null;
        }

        lock (_sync)
        {
            _coalesceCts?.Cancel();
            _coalesceCts?.Dispose();
            _coalesceCts = null;
        }

        _refreshGate.Dispose();
    }
}
