using System.Diagnostics;
using Microsoft.Win32;
using NowPlayingHud.Interop;

namespace NowPlayingHud.Settings;

/// <summary>
/// Автозапуск через HKCU\...\Run.
///
/// Для MSIX-збірки це не працює — там автозапуск описується в маніфесті
/// (windows.startupTask), і вмикати/вимикати його треба через
/// StartupTask.RequestEnableAsync. Тому в упакованому режимі метод мовчки
/// нічого не робить, а перемикач у налаштуваннях ховається.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NowPlayingHud";

    public static bool IsSupported => !NativeMethods.IsRunningPackaged();

    public static bool IsEnabled()
    {
        if (!IsSupported)
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Startup] не вдалося прочитати ключ: {ex.Message}");
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        if (!IsSupported)
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                // Environment.ProcessPath — шлях до самого exe, а не до dll;
                // Assembly.Location для single-file публікації порожній.
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    return;
                }

                key.SetValue(ValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Startup] не вдалося записати ключ: {ex.Message}");
        }
    }
}
