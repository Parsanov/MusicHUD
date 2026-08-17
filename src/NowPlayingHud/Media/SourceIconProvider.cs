using System.Collections.Concurrent;
using System.Diagnostics;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace NowPlayingHud.Media;

/// <summary>
/// Іконка застосунку-джерела за його AUMID.
///
/// Про WPF, як і SmtcService, не знає: віддає байти, декодує їх ViewModel.
///
/// Кеш вічний і за AUMID: іконка Spotify між треками не змінюється, а кожне
/// читання — це або звернення до диска, або запит до реєстру MSIX-пакетів.
/// </summary>
public sealed class SourceIconProvider
{
    /// <summary>
    /// 32 px під бейдж 14–16 px на екрані — запас на масштаб 200%.
    /// Просити менше сенсу немає: Windows усе одно віддає найближчий
    /// доступний розмір із ресурсів застосунку.
    /// </summary>
    private const uint RequestedSizePx = 32;

    private readonly ConcurrentDictionary<string, byte[]?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<byte[]?> GetIconAsync(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }

        if (_cache.TryGetValue(appId, out var cached))
        {
            return cached;
        }

        var bytes = await LoadAsync(appId).ConfigureAwait(false);

        // null кешується так само: якщо джерело іконки не віддало, немає сенсу
        // гатити диск на кожній зміні треку.
        _cache[appId] = bytes;
        return bytes;
    }

    private static async Task<byte[]?> LoadAsync(string appId)
    {
        try
        {
            // Наявність '!' відрізняє справжній AUMID MSIX-пакета
            // ("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic")
            // від того, що SMTC віддає для Win32-застосунків ("Spotify.exe").
            return appId.Contains('!', StringComparison.Ordinal)
                ? await LoadPackagedLogoAsync(appId).ConfigureAwait(false)
                : await LoadExecutableIconAsync(appId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SourceIcon] {appId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// MSIX-застосунки: логотип беремо з манифеста пакета.
    /// AppInfo.GetFromAppUserModelId доступний з Windows 10 1809 — це рівно
    /// той самий мінімум, що стоїть у SupportedOSPlatformVersion.
    /// </summary>
    private static async Task<byte[]?> LoadPackagedLogoAsync(string appId)
    {
        var info = AppInfo.GetFromAppUserModelId(appId);
        var logo = info.DisplayInfo.GetLogo(new Windows.Foundation.Size(RequestedSizePx, RequestedSizePx));

        return await ReadStreamAsync(logo).ConfigureAwait(false);
    }

    /// <summary>
    /// Win32-застосунки: SMTC віддає лише ім'я процесу, тому шлях до exe
    /// доводиться шукати серед живих процесів. Іконка читається через
    /// StorageFile.GetThumbnailAsync — це дозволяє не тягти System.Drawing.
    /// </summary>
    private static async Task<byte[]?> LoadExecutableIconAsync(string exeName)
    {
        var path = ResolveExecutablePath(exeName);
        if (path is null)
        {
            return null;
        }

        var file = await StorageFile.GetFileFromPathAsync(path).AsTask().ConfigureAwait(false);

        using var thumbnail = await file
            .GetThumbnailAsync(ThumbnailMode.SingleItem, RequestedSizePx, ThumbnailOptions.ResizeThumbnail)
            .AsTask()
            .ConfigureAwait(false);

        if (thumbnail is null || thumbnail.Size == 0)
        {
            return null;
        }

        return await ReadStreamAsync(thumbnail).ConfigureAwait(false);
    }

    private static string? ResolveExecutablePath(string exeName)
    {
        // Process.GetProcessesByName хоче ім'я без розширення.
        var name = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? exeName[..^4]
            : exeName;

        var processes = Process.GetProcessesByName(name);
        string? path = null;

        try
        {
            foreach (var process in processes)
            {
                if (path is not null)
                {
                    continue;
                }

                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception ex)
                {
                    // MainModule недоступний, якщо процес запущений від адміна
                    // або від іншого користувача. Це не помилка — просто
                    // залишаємось без іконки.
                    Debug.WriteLine($"[SourceIcon] MainModule {name}: {ex.Message}");
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return path;
    }

    /// <summary>Той самий підхід, що й для обкладинки в SmtcService.</summary>
    private static async Task<byte[]?> ReadStreamAsync(IRandomAccessStreamReference reference)
    {
        using var stream = await reference.OpenReadAsync().AsTask().ConfigureAwait(false);

        if (stream.Size == 0 || stream.Size > 1024 * 1024)   // 1 МБ — іконка стільки не важить
        {
            return null;
        }

        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size).AsTask().ConfigureAwait(false);

        var bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static async Task<byte[]?> ReadStreamAsync(IRandomAccessStream stream)
    {
        if (stream.Size == 0 || stream.Size > 1024 * 1024)
        {
            return null;
        }

        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size).AsTask().ConfigureAwait(false);

        var bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
