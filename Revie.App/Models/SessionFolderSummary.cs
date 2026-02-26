using System.ComponentModel;

namespace Revie.App.Models;

public sealed class SessionFolderSummary : INotifyPropertyChanged
{
    private int _unviewedCaptureCount;
    private string _reviewState = Revie.App.Models.ReviewState.None;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SessionName { get; init; } = string.Empty;
    public string Project { get; init; } = string.Empty;
    public List<string> Users { get; init; } = new();
    public DateTime StartLocal { get; init; }
    public DateTime EndLocal { get; init; }
    public string FolderPath { get; init; } = string.Empty;
    public int CaptureCount { get; init; }
    public string PreviewImagePath { get; init; } = string.Empty;
    public bool HasRevisedUploads { get; init; }
    public bool HasPreview => !string.IsNullOrWhiteSpace(PreviewImagePath);
    public string ReviewState
    {
        get => _reviewState;
        set
        {
            var normalized = Revie.App.Models.ReviewState.Normalize(value);
            if (string.Equals(_reviewState, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _reviewState = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReviewState)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReviewStateDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInReview)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCompleted)));
        }
    }
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
    public string ReviewStateDisplay => Revie.App.Models.ReviewState.ToDisplay(_reviewState);
    public bool IsInReview => string.Equals(_reviewState, Revie.App.Models.ReviewState.InReview, StringComparison.OrdinalIgnoreCase);
    public bool IsCompleted => string.Equals(_reviewState, Revie.App.Models.ReviewState.Completed, StringComparison.OrdinalIgnoreCase);

    public string TimeLabel => StartLocal == default
        ? "(Unknown time)"
        : StartLocal.ToString("yyyy-MM-dd HH:mm");

    public string ProjectDisplay => string.IsNullOrWhiteSpace(Project) ? "(No project)" : Project;
    public string UserDisplay => Users.Count == 0 ? "(No user)" : string.Join(", ", Users);
}
