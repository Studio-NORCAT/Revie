using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Revie.App.Models;
using Revie.App.Native;

namespace Revie.App.Services;

public sealed class HotkeyService : IDisposable
{
    private const int BaseHotkeyId = 4000;

    private readonly HwndSource _source;
    private readonly Dictionary<int, string> _hotkeyIds = new();

    public event EventHandler<string>? HotkeyPressed;

    public HotkeyService()
    {
        var parameters = new HwndSourceParameters("RevieHotkeys")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(NativeMethods.HWND_MESSAGE)
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public IReadOnlyList<string> RegisterHotkeys(IReadOnlyDictionary<string, HotkeyDefinition> hotkeys)
    {
        UnregisterAll();

        var errors = new List<string>();
        var index = 0;

        foreach (var (action, definition) in hotkeys)
        {
            if (!definition.TryGetKey(out var key) || key == Key.None)
            {
                errors.Add($"{action}: invalid key '{definition.KeyName}'.");
                continue;
            }

            var id = BaseHotkeyId + index;
            var virtualKey = KeyInterop.VirtualKeyFromKey(key);
            var ok = NativeMethods.RegisterHotKey(_source.Handle, id, (uint)definition.Modifiers, (uint)virtualKey);
            if (!ok)
            {
                var errorCode = Marshal.GetLastWin32Error();
                errors.Add($"{action}: failed to register ({errorCode}).");
                continue;
            }

            _hotkeyIds[id] = action;
            index++;
        }

        return errors;
    }

    public void UnregisterAll()
    {
        foreach (var (id, _) in _hotkeyIds)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        }

        _hotkeyIds.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY)
        {
            return IntPtr.Zero;
        }

        var id = wParam.ToInt32();
        if (_hotkeyIds.TryGetValue(id, out var action))
        {
            HotkeyPressed?.Invoke(this, action);
            handled = true;
        }

        return IntPtr.Zero;
    }
}
