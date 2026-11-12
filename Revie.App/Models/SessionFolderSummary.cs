using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Revie.App.Infrastructure;

namespace Revie.App.Models;

public sealed class SessionFolderSummary : INotifyPropertyChanged
{
    private static readonly SemaphoreSlim PreviewSlots = new(4, 4);
    private int _unviewedCaptureCount;
    private List<string> _pendingRevisionReviewUsers = new();
    private string _activeUser = string.Empty;
    private bool _hasRevisedUploads;
    private string _reviewState = Revie.App.Models.ReviewState.None;
    private volatile ImageSource? _previewImage;
    private int _previewLoadStarted;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SessionName { get; init; } = string.Empty;
    public string Project { get; init; } = string.Empty;
    public List<string> Users { get; init; } = new();
    public DateTime StartLocal { get; init; }
    public DateTime EndLocal { get; init; }
    public string FolderPath { get; init; } = string.Empty;
    public int CaptureCount { get; init; }
    public string PreviewImagePath { get; init; } = string.Empty;
    public string FallbackPreviewImagePath { get; init; } = string.Empty;
    public bool HasRevisedUploads
    {
        get => _hasRevisedUploads;
        set
        {
            if (_hasRevisedUploads == value) return;
            _hasRevisedUploads = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasRevisedUploads)));
        }
    }
    public List<string> PendingRevisionReviewUsers
    {
        get => _pendingRevisionReviewUsers;
        set
        {
            _pendingRevisionReviewUsers = value ?? new List<string>();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPendingRevisionReviewForActiveUser)));
        }
    }
    [JsonIgnore]
    public string ActiveUser
    {
        get => _activeUser;
        set
        {
            _activeUser = value ?? string.Empty;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPendingRevisionReviewForActiveUser)));
        }
    }
    [JsonIgnore]
    public bool HasPendingRevisionReviewForActiveUser =>
        !string.IsNullOrWhiteSpace(_activeUser) &&
        _pendingRevisionReviewUsers.Contains(_activeUser, StringComparer.OrdinalIgnoreCase);
    public bool HasPreview => !string.IsNullOrWhiteSpace(PreviewImagePath) || !string.IsNullOrWhiteSpace(FallbackPreviewImagePath);
    [JsonIgnore]
    public ImageSource? PreviewImage
    {
        get
        {
            if (HasPreview && Interlocked.Exchange(ref _previewLoadStarted, 1) == 0)
            {
                _ = Task.Run(async () =>
                {
                    await PreviewSlots.WaitAsync();
                    try
                    {
                        var converter = new SafeImagePathConverter();
                        var source = converter.Convert(
                            PreviewImagePath, typeof(ImageSource), 320, CultureInfo.InvariantCulture) as ImageSource;
                        if (!SafeImagePathConverter.IsUsableImage(source) &&
                            !string.IsNullOrWhiteSpace(FallbackPreviewImagePath) &&
                            !string.Equals(PreviewImagePath, FallbackPreviewImagePath, StringComparison.OrdinalIgnoreCase))
                        {
                            source = converter.Convert(
                                FallbackPreviewImagePath, typeof(ImageSource), 320, CultureInfo.InvariantCulture) as ImageSource;
                        }
                        var dispatcher = System.Windows.Application.Current?.Dispatcher;
                        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
                        _ = dispatcher.BeginInvoke(() =>
                        {
                            _previewImage = source;
                            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewImage)));
                        });
                    }
                    catch
                    {
                        // A missing preview must not block the session list.
                    }
                    finally
                    {
                        PreviewSlots.Release();
                    }
                });
            }
            return _previewImage;
        }
    }
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
