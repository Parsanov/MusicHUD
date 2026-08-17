namespace NowPlayingHud.Settings;

/// <summary>
/// Єдина виправдана абстракція в проєкті.
///
/// Причина конкретна, не «щоб було чисто»: шлях зберігання реально
/// відрізняється між звичайним exe (%LOCALAPPDATA%) і MSIX-збіркою
/// (ApplicationData.Current.LocalFolder, файлова система віртуалізується).
/// </summary>
public interface ISettingsStore
{
    AppSettings Load();

    /// <summary>Запис із затримкою: повзунок масштабу інакше пише файл на кожен піксель.</summary>
    void SaveDeferred(AppSettings settings);

    /// <summary>Негайний запис. Викликається при виході.</summary>
    void SaveNow(AppSettings settings);
}
