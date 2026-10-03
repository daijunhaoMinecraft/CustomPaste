using System.Windows.Input;
using System.Windows.Interop;
using CustomPaste.Models;

namespace CustomPaste.Services;

public readonly record struct Hotkey(uint Modifiers, uint VirtualKey)
{
    public static Hotkey Parse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("快捷键需包含 Ctrl / Alt / Win 和一个按键。");
        uint modifiers = 0;
        foreach (var part in parts[..^1])
        {
            uint flag = part.ToUpperInvariant() switch
            {
                "ALT" => 1,
                "CTRL" or "CONTROL" => 2,
                "SHIFT" => 4,
                "WIN" => 8,
                _ => 0
            };
            if (flag == 0 || (modifiers & flag) != 0) throw new ArgumentException("快捷键修饰键无效或重复。");
            modifiers |= flag;
        }

        var name = parts[^1].ToUpperInvariant();
        uint key = name.Length == 1 && char.IsAsciiLetterOrDigit(name[0])
            ? (uint)name[0]
            : name switch
            {
                "SPACE" => 0x20,
                "INSERT" => 0x2D,
                "HOME" => 0x24,
                "END" => 0x23,
                "PAGEUP" => 0x21,
                "PAGEDOWN" => 0x22,
                "ESC" or "ESCAPE" => 0x1B,
                _ => 0
            };
        if (name.StartsWith('F') && int.TryParse(name.AsSpan(1), out var f) && f is >= 1 and <= 24)
            key = (uint)(0x6F + f);
        if ((modifiers & 11) == 0 || key == 0) throw new ArgumentException("请使用 Ctrl / Alt / Win 加字母、数字、F1–F24 或导航键。");
        if ((modifiers == 2 && key is 0x43 or 0x56 or 0x58) || (modifiers == 4 && key == 0x2D))
            throw new ArgumentException("请勿占用系统复制、剪切或粘贴快捷键。");
        return new Hotkey(modifiers, key);
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((Modifiers & 1) != 0) parts.Add("Alt");
        if ((Modifiers & 4) != 0) parts.Add("Shift");
        if ((Modifiers & 8) != 0) parts.Add("Win");
        var key = VirtualKey switch
        {
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x1B => "Escape",
            _ => KeyInterop.KeyFromVirtualKey((int)VirtualKey).ToString()
        };
        if (VirtualKey is >= 0x30 and <= 0x39) key = ((char)VirtualKey).ToString();
        parts.Add(key);
        return string.Join("+", parts);
    }
}

public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private Dictionary<PasteAction, Hotkey> _current = new();
    private readonly Dictionary<Hotkey, int> _registered = new();
    private int _nextId = 100;
    public event Action<PasteAction>? Pressed;

    public HotkeyService()
    {
        // Message-only window: independent of whether settings is visible or closed.
        _source = new HwndSource(new HwndSourceParameters("CustomPaste.Hotkeys") { ParentWindow = new IntPtr(-3) });
        _source.AddHook(WndProc);
    }

    public void Apply(Dictionary<PasteAction, Hotkey> desired)
    {
        var added = new List<Hotkey>();
        try
        {
            foreach (var key in desired.Values.Distinct().Where(k => !_registered.ContainsKey(k)))
            {
                var id = _nextId++;
                if (!NativeMethods.RegisterHotKey(_source.Handle, id, key.Modifiers | 0x4000, key.VirtualKey))
                    throw new InvalidOperationException($"无法注册 {key}，可能已被其他程序或 Windows 占用。原快捷键保持不变。");
                _registered.Add(key, id);
                added.Add(key);
            }
        }
        catch
        {
            foreach (var key in added)
            {
                NativeMethods.UnregisterHotKey(_source.Handle, _registered[key]);
                _registered.Remove(key);
            }

            throw;
        }

        foreach (var key in _registered.Keys.Except(desired.Values).ToList())
        {
            NativeMethods.UnregisterHotKey(_source.Handle, _registered[key]);
            _registered.Remove(key);
        }

        _current = new Dictionary<PasteAction, Hotkey>(desired);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != 0x0312) return IntPtr.Zero;
        foreach (var pair in _current)
            if (_registered.TryGetValue(pair.Value, out var id) && id == wParam.ToInt32())
            {
                handled = true;
                Pressed?.Invoke(pair.Key);
                break;
            }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _registered.Values) NativeMethods.UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}