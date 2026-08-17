using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Timers;
using NowPlayingHud.Interop;

namespace NowPlayingHud.Settings;

public sealed class JsonSettingsStore : ISettingsStore, IDisposable
{
    private const string FileName = "settings.json";

    /// <summary>Скільки чекати після останньої зміни перед записом.</summary>
    private static readonly TimeSpan WriteDelay = TimeSpan.FromMilliseconds(400);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly System.Timers.Timer _writeTimer;
    private readonly object _sync = new();

    private AppSettings? _pending;
    private bool _disposed;

    public string FilePath { get; }

    /// <summary>
    /// Файлу налаштувань не було на момент Load. Замість майстра першого
    /// запуску застосунок один раз покаже HUD із підказкою про хоткей.
    /// </summary>
    public bool IsFirstRun { get; private set; }

    public JsonSettingsStore()
    {
        FilePath = Path.Combine(ResolveFolder(), FileName);

        _writeTimer = new System.Timers.Timer(WriteDelay.TotalMilliseconds) { AutoReset = false };
        _writeTimer.Elapsed += OnWriteTimerElapsed;
    }

    /// <summary>
    /// MSIX віртуалізує файлову систему, тому шлях беремо у WinRT.
    /// Для звичайного exe Package.Current кине виняток — тому спочатку
    /// перевірка через GetCurrentPackageFullName, а не try/catch.
    /// </summary>
    private static string ResolveFolder()
    {
        string folder;

        if (NativeMethods.IsRunningPackaged())
        {
            folder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        }
        else
        {
            folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NowPlayingHud");
        }

        Directory.CreateDirectory(folder);
        return folder;
    }

    public AppSettings Load()
    {
        IsFirstRun = !File.Exists(FilePath);

        try
        {
            if (!IsFirstRun)
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);

                if (settings is not null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            // Битий файл не має ламати запуск — просто повертаємось до дефолтів.
            Debug.WriteLine($"[Settings] не вдалося прочитати {FilePath}: {ex.Message}");
        }

        var fresh = new AppSettings();
        fresh.Normalize();
        return fresh;
    }

    public void SaveDeferred(AppSettings settings)
    {
        lock (_sync)
        {
            _pending = settings;
            _writeTimer.Stop();
            _writeTimer.Start();
        }
    }

    public void SaveNow(AppSettings settings)
    {
        lock (_sync)
        {
            _writeTimer.Stop();
            _pending = null;
        }

        Write(settings);
    }

    private void OnWriteTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        AppSettings? settings;

        lock (_sync)
        {
            settings = _pending;
            _pending = null;
        }

        if (settings is not null)
        {
            Write(settings);
        }
    }

    private void Write(AppSettings settings)
    {
        try
        {
            settings.Normalize();

            // Пишемо у тимчасовий файл і замінюємо: якщо процес вб'ють посеред
            // запису, користувач не отримає обрізаний settings.json.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] не вдалося записати {FilePath}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _writeTimer.Elapsed -= OnWriteTimerElapsed;
        _writeTimer.Dispose();
    }
}
