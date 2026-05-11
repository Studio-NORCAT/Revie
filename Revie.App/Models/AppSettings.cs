using System.Text.Json;

namespace Revie.App.Models;

public sealed class AppSettings
{
    public string StorageRoot { get; set; } = string.Empty;
    public bool ClearOverlayAfterCapture { get; set; } = true;
    public PenSizeSettings PenSizes { get; set; } = new();
    public string CustomPenColor { get; set; } = "#FFFFEA00";
    public Dictionary<string, HotkeyDefinition> Hotkeys { get; set; } = CreateDefaultHotkeys();
    public List<string> Projects { get; set; } = new();
    public string ActiveProject { get; set; } = string.Empty;
    public List<string> Users { get; set; } = new();
    public string ActiveUser { get; set; } = string.Empty;
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string MicrophoneDevice { get; set; } = "default";
    public bool EnableDeleteBackups { get; set; } = true;

    public static AppSettings CreateDefault()
    {
        return new AppSettings();
    }

    public AppSettings Clone()
    {
        var json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<AppSettings>(json) ?? CreateDefault();
    }

    public static Dictionary<string, HotkeyDefinition> CreateDefaultHotkeys()
    {
        return new Dictionary<string, HotkeyDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [HotkeyActions.ToggleDrawing] = new HotkeyDefinition { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift, KeyName = "D" },
            [HotkeyActions.ClearOverlay] = new HotkeyDefinition { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift, KeyName = "C" },
            [HotkeyActions.CaptureScreen] = new HotkeyDefinition { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift, KeyName = "S" },
            [HotkeyActions.OpenMainWindow] = new HotkeyDefinition { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift, KeyName = "O" },
            [HotkeyActions.StartVideoCapture] = new HotkeyDefinition { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift, KeyName = "V" },
            [HotkeyActions.StopVideoCapture] = new HotkeyDefinition { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift, KeyName = "B" }
        };
    }
}
