namespace NowPlayingHud.Media;

/// <summary>
/// Знімок стану медіасесії. Immutable — щоб його можна було безпечно
/// перекинути з потоку пула в UI-потік без блокувань.
/// </summary>
/// <param name="Title">Назва треку.</param>
/// <param name="Artist">Виконавець.</param>
/// <param name="Album">Альбом. Часто порожній — YouTube у браузері його не дає.</param>
/// <param name="SourceAppId">AUMID джерела: "Spotify.exe", "msedge.exe", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic".</param>
/// <param name="Status">Стан відтворення на момент знімка.</param>
/// <param name="Position">Позиція, як її повідомила сесія (не тікає сама).</param>
/// <param name="Duration">Тривалість треку. Zero, якщо джерело не повідомляє таймлайн.</param>
/// <param name="PositionUpdatedAt">Коли сесія востаннє оновила Position — база для екстраполяції.</param>
/// <param name="Thumbnail">Байти обкладинки (JPEG/PNG). null, якщо джерело її не віддало.</param>
public sealed record TrackInfo(
    string Title,
    string Artist,
    string Album,
    string SourceAppId,
    PlaybackStatus Status,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset PositionUpdatedAt,
    byte[]? Thumbnail)
{
    public static TrackInfo Empty { get; } = new(
        Title: string.Empty,
        Artist: string.Empty,
        Album: string.Empty,
        SourceAppId: string.Empty,
        Status: PlaybackStatus.Unknown,
        Position: TimeSpan.Zero,
        Duration: TimeSpan.Zero,
        PositionUpdatedAt: DateTimeOffset.MinValue,
        Thumbnail: null);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Artist);

    public bool IsPlaying => Status == PlaybackStatus.Playing;

    public bool HasTimeline => Duration > TimeSpan.Zero;

    /// <summary>
    /// Ідентичність саме треку, а не стану. Використовується, щоб відрізнити
    /// «змінився трек» (тригер пасивного HUD) від «натиснули паузу».
    /// </summary>
    public bool IsSameTrackAs(TrackInfo other) =>
        string.Equals(Title, other.Title, StringComparison.Ordinal) &&
        string.Equals(Artist, other.Artist, StringComparison.Ordinal);

    /// <summary>
    /// Позиція «зараз». Сесія оновлює Position подіями, а не щосекунди,
    /// тому під час відтворення до неї додається час, що минув від оновлення.
    /// </summary>
    public TimeSpan EstimatePosition(DateTimeOffset now)
    {
        if (!IsPlaying || PositionUpdatedAt == DateTimeOffset.MinValue)
        {
            return Position;
        }

        var elapsed = now - PositionUpdatedAt;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var estimated = Position + elapsed;
        return HasTimeline && estimated > Duration ? Duration : estimated;
    }
}

/// <summary>Живий елемент списку джерел для випадайки в налаштуваннях.</summary>
/// <param name="AppId">AUMID — те, що зберігається в налаштуваннях.</param>
/// <param name="DisplayName">Людська назва для UI.</param>
public sealed record SessionDescriptor(string AppId, string DisplayName)
{
    public override string ToString() => DisplayName;
}
