using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Revie.App.Infrastructure;
using Revie.App.Models;
using Revie.App.Native;
using Revie.App.Services;

namespace Revie.App.ViewModels;

public sealed class CaptureListItemViewModel : ObservableObject
{
    private static readonly ImageSource EmptyImageSource = new BitmapImage();
    private ImageSource? _thumbnail;
    private ImageSource? _previewImage;
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

        return Record.ImagePath;
    }

    private string GetThumbnailPath()
    {
        if (IsVideo)
        {
            var thumb = (Record.Metadata.ThumbnailPath ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(thumb))
            {
                return thumb;
            }

            var captureDir = Record.CaptureDirectory;
            return Path.Combine(captureDir, "thumbnail.png");
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

        const int maxAttempts = 4;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var baseBitmap = new System.Drawing.Bitmap(path);
                using var renderBitmap = ResizeIfNeeded(baseBitmap, decodeWidth);
                var hBitmap = renderBitmap.GetHbitmap();

                try
                {
                    var source = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally
                {
                    NativeMethods.DeleteObject(hBitmap);
                }
            }
            catch when (attempt < maxAttempts)
            {
                Thread.Sleep(80);
            }
            catch
            {
                return EmptyImageSource;
            }
        }

        return EmptyImageSource;
    }

    private static System.Drawing.Bitmap ResizeIfNeeded(System.Drawing.Bitmap source, int decodeWidth)
    {
        if (decodeWidth <= 0 || source.Width <= decodeWidth)
        {
            return (System.Drawing.Bitmap)source.Clone();
        }

        var targetHeight = Math.Max(1, (int)Math.Round((double)source.Height * decodeWidth / source.Width));
        var resized = new System.Drawing.Bitmap(decodeWidth, targetHeight, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

        using var graphics = System.Drawing.Graphics.FromImage(resized);
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, 0, 0, decodeWidth, targetHeight);
        return resized;
    }
}
