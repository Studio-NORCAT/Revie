using System.Windows.Input;
using Revie.App.Models;

namespace Revie.App.Services;

public static class HotkeyParser
{
    public static bool TryParse(string input, out HotkeyDefinition hotkey, out string error)
    {
        hotkey = new HotkeyDefinition();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Hotkey cannot be empty.";
            return false;
        }

        var tokens = input.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            error = "Hotkey format is invalid.";
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            switch (tokens[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "win":
                case "windows":
                    modifiers |= HotkeyModifiers.Win;
                    break;
                default:
                    error = $"Unknown modifier '{tokens[i]}'.";
                    return false;
            }
        }

        var keyToken = tokens[^1];
        if (keyToken.Length == 1)
        {
            keyToken = char.ToUpperInvariant(keyToken[0]).ToString();
        }

        if (!Enum.TryParse<Key>(keyToken, true, out var key) || key == Key.None)
        {
            error = $"Unknown key '{keyToken}'.";
            return false;
        }

        hotkey = new HotkeyDefinition
        {
            Modifiers = modifiers,
            KeyName = key.ToString()
        };
        return true;
    }
}
