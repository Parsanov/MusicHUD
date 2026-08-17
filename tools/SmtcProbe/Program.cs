using System.Runtime.InteropServices;
using System.Text;
using Windows.Media.Control;

namespace SmtcProbe;

/// <summary>
/// Кроки 1–2 з порядку розробки: перевірка, що SMTC взагалі віддає дані
/// і що глобальний хоткей доходить, поки у фокусі чуже вікно.
///
/// Це має працювати ДО того, як з'явиться хоч один рядок WPF.
/// Якщо не працює — далі йти нема сенсу.
/// </summary>
internal static class Program
{
    // Хоткей за замовчуванням: Alt+Shift+M
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_M = 0x4D;

    private const int WM_HOTKEY = 0x0312;
    private const int WM_QUIT = 0x0012;
    private const int HotkeyId = 1;

    private static GlobalSystemMediaTransportControlsSessionManager? _manager;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("SmtcProbe — Alt+Shift+M друкує поточний трек, Ctrl+C виходить.\n");

        // Ініціалізація синхронно: далі на цьому ж потоці має крутитись
        // цикл повідомлень, тому віддавати його await-у не можна.
        try
        {
            InitializeAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SMTC недоступний: {ex.Message}");
            return 1;
        }

        // hWnd = NULL: WM_HOTKEY піде в чергу повідомлень цього потоку,
        // тож вікно тут не потрібне взагалі.
        if (!RegisterHotKey(IntPtr.Zero, HotkeyId, MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, VK_M))
        {
            Console.WriteLine("Alt+Shift+M зайнято іншою програмою — RegisterHotKey повернув false.");
        }
        else
        {
            Console.WriteLine("Хоткей зареєстровано. Переключись у гру і натисни Alt+Shift+M.\n");
        }

        var threadId = GetCurrentThreadId();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        };

        RunMessageLoop();

        UnregisterHotKey(IntPtr.Zero, HotkeyId);
        return 0;
    }

    private static async Task InitializeAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

        _manager.CurrentSessionChanged += (_, _) =>
        {
            Console.WriteLine("[подія] змінилась активна сесія");
            PrintCurrentTrack();
        };

        PrintSessions();
        PrintCurrentTrack();
    }

    private static void RunMessageLoop()
    {
        // GetMessage повертає 0 на WM_QUIT і -1 на помилці.
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.message == WM_HOTKEY)
            {
                Console.WriteLine($"[хоткей] {DateTime.Now:HH:mm:ss}");
                PrintCurrentTrack();
            }

            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    private static void PrintSessions()
    {
        if (_manager is null)
        {
            return;
        }

        var sessions = _manager.GetSessions();
        Console.WriteLine($"Живих медіасесій: {sessions.Count}");

        foreach (var session in sessions)
        {
            Console.WriteLine($"  - {session.SourceAppUserModelId}");
        }

        Console.WriteLine();
    }

    private static void PrintCurrentTrack()
    {
        var session = _manager?.GetCurrentSession();

        if (session is null)
        {
            Console.WriteLine("  активної сесії немає\n");
            return;
        }

        try
        {
            var properties = session.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            var duration = timeline.EndTime - timeline.StartTime;

            Console.WriteLine($"  джерело : {session.SourceAppUserModelId}");
            Console.WriteLine($"  трек    : {properties.Title}");
            Console.WriteLine($"  артист  : {properties.Artist}");
            Console.WriteLine($"  альбом  : {properties.AlbumTitle}");
            Console.WriteLine($"  статус  : {playback.PlaybackStatus}");
            Console.WriteLine($"  позиція : {timeline.Position:mm\\:ss} / {duration:mm\\:ss}");
            Console.WriteLine($"  обкладинка: {(properties.Thumbnail is null ? "немає" : "є")}\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  не вдалося прочитати: {ex.Message}\n");
        }
    }

    // ------------------------------------------------------------------
    // P/Invoke
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
