using System.Windows.Interop;
using System.Windows.Threading;
using NowPlayingHud.Interop;

namespace NowPlayingHud.Input;

/// <summary>Що не вдалося зареєструвати і чому.</summary>
/// <param name="Action">Дія, за яку відповідав хоткей.</param>
/// <param name="Binding">Комбінація, яку не взяли.</param>
public sealed record HotkeyConflict(HotkeyAction Action, HotkeyBinding Binding);

/// <summary>
/// Глобальні хоткеї через RegisterHotKey.
///
/// Слухач висить на окремому message-only вікні, а не на оверлеї: оверлей
/// ховається і показується, а хоткеї мають працювати завжди.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    /// <summary>Період опитування відпускання клавіші для hold-to-peek.</summary>
    private static readonly TimeSpan HoldPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly HwndSource _sink;
    private readonly Dictionary<int, HotkeyAction> _byId = new();
    private readonly Dictionary<HotkeyAction, HotkeyBinding> _bindings = new();
    private readonly DispatcherTimer _holdTimer;

    private int _nextId = 1;
    private int _watchedVirtualKey;
    private bool _disposed;

    /// <summary>Комбінацію натиснуто.</summary>
    public event EventHandler<HotkeyAction>? Pressed;

    /// <summary>Клавішу ToggleHud відпущено. Потрібно лише для hold-to-peek.</summary>
    public event EventHandler? HudKeyReleased;

    public HotkeyService()
    {
        var parameters = new HwndSourceParameters("NowPlayingHud.HotkeySink")
        {
            // HWND_MESSAGE: вікно без піксельного представлення, тільки для повідомлень.
            ParentWindow = WindowStyles.HWND_MESSAGE,
            WindowStyle = 0
        };

        _sink = new HwndSource(parameters);
        _sink.AddHook(WndProc);

        _holdTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = HoldPollInterval
        };
        _holdTimer.Tick += OnHoldTick;
    }

    /// <summary>
    /// Реєструє повний набір при старті. Повертає ті, що не взялися:
    /// RegisterHotKey мовчки повертає false, якщо комбінація вже кимось зайнята,
    /// і про це треба сказати користувачу, а не проковтнути.
    /// </summary>
    public IReadOnlyList<HotkeyConflict> Apply(IReadOnlyDictionary<HotkeyAction, HotkeyBinding> bindings)
    {
        UnregisterAll();

        var conflicts = new List<HotkeyConflict>();

        foreach (var (action, binding) in bindings)
        {
            if (binding.IsEmpty)
            {
                continue;
            }

            if (!TryBind(action, binding))
            {
                conflicts.Add(new HotkeyConflict(action, binding));
            }
        }

        return conflicts;
    }

    /// <summary>
    /// Перевʼязує одну дію.
    ///
    /// Стару комбінацію знімаємо ПЕРЕД реєстрацією нової: інакше RegisterHotKey
    /// відмовить сам собі, якщо нова відрізняється від старої лише модифікатором.
    ///
    /// false = нову комбінацію хтось уже тримає. Стара при цьому відновлюється,
    /// щоб дія не залишилась без хоткея взагалі.
    /// </summary>
    public bool TryRebind(HotkeyAction action, HotkeyBinding binding)
    {
        var previous = GetActiveBinding(action);

        Unbind(action);

        // Порожня комбінація — легальний стан: користувач натиснув Escape.
        if (binding.IsEmpty || TryBind(action, binding))
        {
            return true;
        }

        if (!previous.IsEmpty)
        {
            TryBind(action, previous);
        }

        return false;
    }

    /// <summary>Комбінація, яка зараз реально зареєстрована в системі за цією дією.</summary>
    public HotkeyBinding GetActiveBinding(HotkeyAction action) =>
        _bindings.TryGetValue(action, out var binding) ? binding : HotkeyBinding.None;

    private bool TryBind(HotkeyAction action, HotkeyBinding binding)
    {
        var id = _nextId++;

        if (!NativeMethods.RegisterHotKey(_sink.Handle, id, binding.ToWin32Modifiers(), (uint)binding.ToVirtualKey()))
        {
            return false;
        }

        _byId[id] = action;
        _bindings[action] = binding;
        return true;
    }

    private void Unbind(HotkeyAction action)
    {
        // ToList(), бо словник змінюється всередині циклу.
        foreach (var (id, registered) in _byId.ToList())
        {
            if (registered != action)
            {
                continue;
            }

            NativeMethods.UnregisterHotKey(_sink.Handle, id);
            _byId.Remove(id);
        }

        _bindings.Remove(action);
    }

    /// <summary>
    /// Вмикає стеження за відпусканням клавіші ToggleHud.
    /// RegisterHotKey сповіщає тільки про натискання, тому стан клавіші
    /// доводиться опитувати — іншого способу зробити hold-to-peek немає.
    /// </summary>
    public void BeginHoldWatch()
    {
        var binding = GetActiveBinding(HotkeyAction.ToggleHud);
        if (binding.IsEmpty)
        {
            return;
        }

        _watchedVirtualKey = binding.ToVirtualKey();
        _holdTimer.Start();
    }

    public void EndHoldWatch() => _holdTimer.Stop();

    private void OnHoldTick(object? sender, EventArgs e)
    {
        if (_watchedVirtualKey != 0 && NativeMethods.IsKeyDown(_watchedVirtualKey))
        {
            return;
        }

        _holdTimer.Stop();
        HudKeyReleased?.Invoke(this, EventArgs.Empty);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WindowStyles.WM_HOTKEY)
        {
            return IntPtr.Zero;
        }

        if (_byId.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Pressed?.Invoke(this, action);
        }

        return IntPtr.Zero;
    }

    private void UnregisterAll()
    {
        foreach (var id in _byId.Keys)
        {
            NativeMethods.UnregisterHotKey(_sink.Handle, id);
        }

        _byId.Clear();
        _bindings.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _holdTimer.Stop();
        _holdTimer.Tick -= OnHoldTick;

        UnregisterAll();

        _sink.RemoveHook(WndProc);
        _sink.Dispose();
    }
}
