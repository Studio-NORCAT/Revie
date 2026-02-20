using System.Windows.Input;

namespace DoodleCapture.App.Models;

public sealed class HotkeyDefinition
{
    public HotkeyModifiers Modifiers { get; set; }
    public string KeyName { get; set; } = "None";

    public bool TryGetKey(out Key key)
    {
        if (Enum.TryParse(KeyName, true, out key))
        {
            return true;
        }

        if (KeyName.Length == 1 && char.IsLetterOrDigit(KeyName[0]))
        {
            var normalized = char.ToUpperInvariant(KeyName[0]).ToString();
            if (Enum.TryParse(normalized, true, out key))
            {
                return true;
            }
        }

        key = Key.None;
        return false;
    }

    public string ToDisplayString()
    {
        var pieces = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) pieces.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) pieces.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) pieces.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) pieces.Add("Win");
        pieces.Add(KeyName);
        return string.Join("+", pieces);
    }
}
