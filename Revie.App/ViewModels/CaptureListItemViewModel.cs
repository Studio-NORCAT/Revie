using System.Windows.Media;
using System.Windows.Media.Imaging;
using Revie.App.Infrastructure;
using Revie.App.Models;
using Revie.App.Services;

namespace Revie.App.ViewModels;

public sealed class CaptureListItemViewModel : ObservableObject
{
    private static readonly ImageSource EmptyImageSource = new BitmapImage();
    private ImageSource? _thumbnail;
    private ImageSource? _previewImage;
    private string _previewOverridePath = string.Empty;
    private string _note;
    private string _project;
    private string _reviewState;
    private List<string> _assignedUsers;
    private string _userTagsText;

    public CaptureListItemViewModel(CaptureRecord record)
    {
        Record = record;
        _note = record.Metadata.Note ?? string.Empty;
        _project = record.Metadata.Project ?? string.Empty;
        _reviewState = Revie.App.Models.ReviewState.Normalize(record.Metadata.ReviewState);
        Record.Metadata.ReviewState = _reviewState;
        var normalizedUsers = CaptureService.NormalizeUsers(record.Metadata.AssignedUsers);
        _assignedUsers = normalizedUsers.Count > 0
            ? normalizedUsers.ToList()
            : CaptureService.NormalizeUsers(new[] { record.Metadata.AssignedUser }).ToList();
        Record.Metadata.AssignedUsers = _assignedUsers.ToList();
        Record.Metadata.AssignedUser = _assignedUsers.FirstOrDefault() ?? string.Empty;
        _userTagsText = string.Join(", ", _assignedUsers);
    }

    public CaptureRecord Record { get; }
    public DateTime LocalTimestamp => Record.LocalTimestamp;
    public string DayGroup => LocalTimestamp.ToString("yyyy-MM-dd ddd");
    public string TimeLabel => LocalTimestamp.ToString("HH:mm:ss.fff");
    public string FullTimestampLabel => LocalTimestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
    public string ActiveWindowTitle => string.IsNullOrWhiteSpace(Record.Metadata.ActiveWindowTitle) ? "(No active window title)" : Record.Metadata.ActiveWindowTitle;
    public string ActiveProcessName => string.IsNullOrWhiteSpace(Record.Metadata.ActiveProcessName) ? "(Unknown process)" : Record.Metadata.ActiveProcessName;
    public bool IsVideo => string.Equals(Record.Metadata.CaptureType, "video", StringComparison.OrdinalIgnoreCase);
    public string CaptureTypeDisplay => IsVideo ? "Video" : "Image";
    public string CaptureTypeGlyph => IsVideo ? "\uE714" : "\uE91B";
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
    public string Note
    {
        get => _note;
        set
        {
            var normalized = value ?? string.Empty;
            if (!SetProperty(ref _note, normalized))
            {
                return;
            }

            Record.Metadata.Note = normalized;
            OnPropertyChanged(nameof(SearchBlob));
            OnPropertyChanged(nameof(HasNote));
        }
    }

    public string Project
    {
        get => _project;
        set
        {
            var normalized = value ?? string.Empty;
            if (!SetProperty(ref _project, normalized))
            {
                return;
            }

            Record.Metadata.Project = normalized;
            OnPropertyChanged(nameof(SearchBlob));
            OnPropertyChanged(nameof(ProjectDisplay));
        }
    }

    public string ProjectDisplay => string.IsNullOrWhiteSpace(Project) ? "(No project)" : Project;
    public string ReviewState
    {
        get => _reviewState;
        set
        {
            var normalized = Revie.App.Models.ReviewState.Normalize(value);
            if (!SetProperty(ref _reviewState, normalized))
            {
                return;
            }

            Record.Metadata.ReviewState = normalized;
            OnPropertyChanged(nameof(ReviewStateDisplay));
            OnPropertyChanged(nameof(IsInReview));
            OnPropertyChanged(nameof(IsCompleted));
        }
    }

    public string ReviewStateDisplay => Revie.App.Models.ReviewState.ToDisplay(_reviewState);
    public bool IsInReview => string.Equals(_reviewState, Revie.App.Models.ReviewState.InReview, StringComparison.OrdinalIgnoreCase);
    public bool IsCompleted => string.Equals(_reviewState, Revie.App.Models.ReviewState.Completed, StringComparison.OrdinalIgnoreCase);

    public string AssignedUser
    {
        get => _assignedUsers.FirstOrDefault() ?? string.Empty;
        set
        {
            var normalized = CaptureService.NormalizeUser(value);
            var newUsers = string.IsNullOrWhiteSpace(normalized) ? new List<string>() : new List<string> { normalized };
            if (SequenceEqualUsers(_assignedUsers, newUsers))
            {
                return;
            }

            _assignedUsers = newUsers;
            _userTagsText = string.Join(", ", _assignedUsers);
            Record.Metadata.AssignedUsers = _assignedUsers.ToList();
            Record.Metadata.AssignedUser = _assignedUsers.FirstOrDefault() ?? string.Empty;
            OnPropertyChanged(nameof(SearchBlob));
            OnPropertyChanged(nameof(AssignedUserDisplay));
            OnPropertyChanged(nameof(UserTagsText));
        }
    }

    public IReadOnlyList<string> AssignedUsers => _assignedUsers;
    public string AssignedUserDisplay => _assignedUsers.Count == 0 ? "(No user)" : string.Join(", ", _assignedUsers);
    public string UserTagsText
    {
        get => _userTagsText;
        set
        {
            var normalizedText = value ?? string.Empty;
            if (!SetProperty(ref _userTagsText, normalizedText))
            {
                return;
            }

            var parsed = CaptureService.ParseUserTags(normalizedText).ToList();
            if (SequenceEqualUsers(_assignedUsers, parsed))
            {
                return;
            }

            _assignedUsers = parsed;
            Record.Metadata.AssignedUsers = _assignedUsers.ToList();
            Record.Metadata.AssignedUser = _assignedUsers.FirstOrDefault() ?? string.Empty;
            OnPropertyChanged(nameof(AssignedUser));
            OnPropertyChanged(nameof(AssignedUserDisplay));
            OnPropertyChanged(nameof(SearchBlob));
        }
    }

    public string CaptureDirectory => Record.CaptureDirectory;
    public string ImagePath => Record.ImagePath;
    public string VideoPath => Record.VideoPath;
    public string MediaPath => IsVideo ? VideoPath : ImagePath;
    public ImageSource Thumbnail => _thumbnail ??= LoadImage(GetThumbnailPath(), 160);

    public ImageSource PreviewImage
    {
        get
        {
            _previewImage ??= LoadImage(GetPreviewPath(), 0);
            return _previewImage;
        }
    }

    public string SearchBlob => $"{Record.Metadata.Note ?? string.Empty} {Record.Metadata.ActiveWindowTitle ?? string.Empty} {Record.Metadata.ActiveProcessName ?? string.Empty} {Record.Metadata.Project ?? string.Empty} {string.Join(' ', _assignedUsers)} {CaptureTypeDisplay}";

    public void UpdatePaths(string captureDirectory, string imagePath, string jsonPath)
    {
        Record.Metadata.CaptureDirectory = captureDirectory;
        if (IsVideo)
        {
            Record.Metadata.VideoPath = Path.Combine(captureDirectory, "capture.mp4");
            Record.Metadata.ImagePath = string.Empty;
            Record.Metadata.ThumbnailPath = File.Exists(Path.Combine(captureDirectory, "thumbnail.png"))
                ? Path.Combine(captureDirectory, "thumbnail.png")
                : (File.Exists(Path.Combine(captureDirectory, "capture.png")) ? Path.Combine(captureDirectory, "capture.png") : string.Empty);
        }
        else
        {
            Record.Metadata.ImagePath = imagePath;
            Record.Metadata.VideoPath = string.Empty;
            Record.Metadata.ThumbnailPath = Record.Metadata.ImagePath;
        }

        Record.Metadata.JsonPath = jsonPath;
        OnPropertyChanged(nameof(CaptureDirectory));
        OnPropertyChanged(nameof(ImagePath));
        OnPropertyChanged(nameof(VideoPath));
        OnPropertyChanged(nameof(MediaPath));
        OnPropertyChanged(nameof(PreviewImage));
    }

    public void SetPreviewOverridePath(string? path)
    {
        var normalized = (path ?? string.Empty).Trim();
        if (string.Equals(_previewOverridePath, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _previewOverridePath = normalized;
        _previewImage = null;
        OnPropertyChanged(nameof(PreviewImage));
    }

    private string GetPreviewPath()
    {
        if (IsVideo)
        {
            var thumb = GetThumbnailPath();
            if (File.Exists(thumb))
            {
                return thumb;
            }

            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(_previewOverridePath) && File.Exists(_previewOverridePath))
        {
            return _previewOverridePath;
        }

        return Record.ImagePath;
    }

    private string GetThumbnailPath()
    {
        if (IsVideo)
        {
            var thumb = (Record.Metadata.ThumbnailPath ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(thumb) && File.Exists(thumb))
            {
                return thumb;
            }

            var captureDir = Record.CaptureDirectory;
            var fallbackThumbnail = Path.Combine(captureDir, "thumbnail.png");
            if (File.Exists(fallbackThumbnail))
            {
                return fallbackThumbnail;
            }

            var fallbackCaptureImage = Path.Combine(captureDir, "capture.png");
            if (File.Exists(fallbackCaptureImage))
            {
                return fallbackCaptureImage;
            }

            return thumb;
        }

        return Record.ImagePath;
    }

    private static bool SequenceEqualUsers(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static ImageSource LoadImage(string? path, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return EmptyImageSource;
        }

        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var stream = OpenReadStream(path);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodeWidth > 0)
                {
                    bitmap.DecodePixelWidth = decodeWidth;
                }

                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch when (attempt < maxAttempts)
            {
                Thread.Sleep(70 * attempt);
            }
            catch
            {
                return EmptyImageSource;
            }
        }

        return EmptyImageSource;
    }

    private static FileStream OpenReadStream(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (IOException)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
    }
}
