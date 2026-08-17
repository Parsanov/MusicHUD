namespace NowPlayingHud.Media;

/// <summary>
/// Дзеркало GlobalSystemMediaTransportControlsSessionPlaybackStatus.
/// Свій enum потрібен, щоб решта коду не тягла WinRT-типи —
/// на Linux це місце займе стан з MPRIS.
/// </summary>
public enum PlaybackStatus
{
    Unknown = 0,
    Closed = 1,
    Opened = 2,
    Changing = 3,
    Stopped = 4,
    Playing = 5,
    Paused = 6
}
