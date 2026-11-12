using System.Windows.Media;
using System.Windows.Media.Imaging;
using Revie.App.Infrastructure;
using Revie.App.Models;
using Revie.App.Services;

namespace Revie.App.ViewModels;

public sealed class CaptureListItemViewModel : ObservableObject
{
    private static readonly ImageSource EmptyImageSource = CreateEmptyImageSource();
    private static readonly SemaphoreSlim PreviewLoadSlots = new(2, 2);
    private ImageSource? _thumbnail;
    private bool _thumbnailLoadStarted;
    private volatile ImageSource? _previewImage;
    private readonly object _previewLoadLock = new();
    private Task<ImageSource>? _previewLoadTask;
    private int _previewVersion;
    private string _previewOverridePath = string.Empty;
    private string _note;
    private string _project;
    private string _reviewState;
    private string _activeUser;
    private List<string> _assignedUsers;
    private string _userTagsText;

    public CaptureListItemViewModel(CaptureRecord record, string activeUser = "")
    {
        Record = record;
        _activeUser = activeUser;
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
    public bool IsDeleted { get; private set; }
    public void MarkDeleted() => IsDeleted = true;
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
    public bool HasRevisedUploads => Record.Metadata.UploadedImagePaths is { Count: > 0 };
    public bool IsRevisionReviewRequestedForLocalUser => RevisionReviewState.IsPendingForUser(Record.Metadata, _activeUser);
    public void RefreshRevisionReviewState()
    {
        OnPropertyChanged(nameof(HasRevisedUploads));
        OnPropertyChanged(nameof(IsRevisionReviewRequestedForLocalUser));
    }
    public void SetActiveUser(string activeUser)
    {
        _activeUser = activeUser ?? string.Empty;
        OnPropertyChanged(nameof(IsRevisionReviewRequestedForLocalUser));
    }
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
    public ImageSource Thumbnail
    {
        get
        {
            if (!_thumbnailLoadStarted)
            {
                _thumbnailLoadStarted = true;
                _ = Task.Run(() =>
                {
                    try
                    {
                        var source = GetThumbnailPath();
                        var thumbnailPath = source.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                            ? source
                            : SidebarThumbnailService.EnsureThumbnail(source, Record.CaptureDirectory);
                        var image = LoadImage(thumbnailPath, 160);
                        if (!SafeImagePathConverter.IsUsableImage(image) &&
                            !IsVideo && File.Exists(Record.ImagePath) &&
                            !string.Equals(thumbnailPath, Record.ImagePath, StringComparison.OrdinalIgnoreCase))
                        {
                            image = LoadImage(Record.ImagePath, 160);
                        }
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            _thumbnail = image;
                            OnPropertyChanged(nameof(Thumbnail));
                        });
                    }
                    catch
                    {
                        // A preview failure should leave the capture navigable.
                    }
                });
            }
            return _thumbnail ?? EmptyImageSource;
        }
    }

    public ImageSource PreviewImage
    {
        get
        {
            if (_previewImage is not null) return _previewImage;
            _ = EnsurePreviewLoadedAsync();
            return EmptyImageSource;
        }
    }

    public async Task EnsurePreviewLoadedAsync()
    {
        Task<ImageSource> loadTask;
        int version;
        lock (_previewLoadLock)
        {
            if (_previewImage is not null) return;
            version = _previewVersion;
            loadTask = _previewLoadTask ??= Task.Run(() =>
            {
                PreviewLoadSlots.Wait();
                try
                {
                    lock (_previewLoadLock)
                    {
                        if (version != _previewVersion) return EmptyImageSource;
                    }
                    return LoadImage(GetPreviewPath(), 0);
                }
                catch
                {
                    return EmptyImageSource;
                }
                finally
                {
                    PreviewLoadSlots.Release();
                }
            });
        }

        var image = await loadTask.ConfigureAwait(false);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                lock (_previewLoadLock)
                {
                    if (version != _previewVersion || _previewImage is not null) return;
                    _previewImage = image;
                }
                OnPropertyChanged(nameof(PreviewImage));
            });
        }
        catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException)
        {
            // The window is closing.
        }
    }

    public void ReleasePreviewImage() => InvalidatePreviewImage();

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
            Record.Metadata.ThumbnailPath = Path.Combine(captureDirectory, SidebarThumbnailService.FileName);
        }

        Record.Metadata.JsonPath = jsonPath;
        _thumbnail = null;
        _thumbnailLoadStarted = false;
        InvalidatePreviewImage();
        OnPropertyChanged(nameof(CaptureDirectory));
        OnPropertyChanged(nameof(ImagePath));
        OnPropertyChanged(nameof(VideoPath));
        OnPropertyChanged(nameof(MediaPath));
        OnPropertyChanged(nameof(Thumbnail));
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
        InvalidatePreviewImage();
        OnPropertyChanged(nameof(PreviewImage));
    }

    private void InvalidatePreviewImage()
    {
        lock (_previewLoadLock)
        {
            _previewVersion++;
            _previewImage = null;
            _previewLoadTask = null;
        }
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

        var imageThumbnail = Record.Metadata.ThumbnailPath;
        return !string.IsNullOrWhiteSpace(imageThumbnail) && File.Exists(imageThumbnail)
            ? imageThumbnail
            : Record.ImagePath;
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

    private static ImageSource CreateEmptyImageSource()
    {
        var image = new BitmapImage();
        image.Freeze();
        return image;
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
