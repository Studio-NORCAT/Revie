namespace DoodleCapture.App.Models;

public sealed class MonitorSnapshot
{
    public string DeviceName { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
