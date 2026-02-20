using System.ComponentModel;

namespace Revie.App.Models;

public sealed class SessionFolderSummary : INotifyPropertyChanged
{
    private int _unviewedCaptureCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SessionName { get; init; } = string.Empty;
    public string Project { get; init; } = string.Empty;
    public List<string> Users { get; init; } = new();
    public DateTime StartLocal { get; init; }
    public DateTime EndLocal { get; init; }
    public string FolderPath { get; init; } = string.Empty;
    public int CaptureCount { get; init; }
    public string PreviewImagePath { get; init; } = string.Empty;
    public bool HasPreview => !string.IsNullOrWhiteSpace(PreviewImagePath) && File.Exists(PreviewImagePath);
    public int UnviewedCaptureCount
    {
        get => _unviewedCaptureCount;
        set
        {
            var normalized = Math.Max(0, value);
            if (_unviewedCaptureCount == normalized)
            {
                return;
            }

            _unviewedCaptureCount = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnviewedCaptureCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasUnviewedCaptures)));
        }
    }
    public bool HasUnviewedCaptures => _unviewedCaptureCount > 0;

    public string TimeLabel => StartLocal == default
        ? "(Unknown time)"
        : StartLocal.ToString("yyyy-MM-dd HH:mm");

    public string ProjectDisplay => string.IsNullOrWhiteSpace(Project) ? "(No project)" : Project;
    public string UserDisplay => Users.Count == 0 ? "(No user)" : string.Join(", ", Users);
}
