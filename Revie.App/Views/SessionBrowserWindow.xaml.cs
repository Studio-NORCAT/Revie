using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Revie.App.Models;
using Revie.App.Services;
using Revie.App.ViewModels;

namespace Revie.App.Views;

public partial class SessionBrowserWindow : Window, INotifyPropertyChanged
{
    private const string AllProjectsLabel = "(All Projects)";
    private const string NoProjectLabel = "(No Project)";
    private const string AllUsersLabel = "(All Users)";
    private const string NoUserLabel = "(No User)";
    private const string UploadsFolderName = "user-uploads";
    private static readonly HashSet<string> SupportedUploadImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp",
        ".webp"
    };
    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _storageRoot;
    private readonly SessionBrowseService _sessionBrowseService = new();
    private readonly SessionNotificationService _sessionNotificationService = new();
    private readonly List<string> _configuredProjects;
    private readonly List<string> _configuredUsers;
    private readonly List<SessionFolderSummary> _allSessions = new();
    private SessionNotificationState _sessionNotificationState = new();
    private readonly DispatcherTimer _sessionNoteSavedFeedbackTimer;
    private readonly DispatcherTimer _sessionVideoPositionTimer;
    private readonly DispatcherTimer _sessionVideoToolbarFadeTimer;
    private int _sessionRefreshRequestId;
    private CancellationTokenSource? _sessionPreviewLoadCts;
    private int _sessionPreviewRequestId;
    private string _selectedSessionProjectFilter = AllProjectsLabel;
    private string _selectedSessionUserFilter = AllUsersLabel;
    private string _selectedCaptureUserChoice = NoUserLabel;
    private int _selectedCaptureCompareTabIndex;
    private CapturePreviewVariant? _selectedCapturePreviewVariant;
    private CapturePreviewVariant? _selectedUploadedCapturePreviewVariant;
    private string _selectedCapturePreviewPath = string.Empty;
    private SessionFolderSummary? _selectedSession;
    private CaptureListItemViewModel? _selectedCapture;
    private bool _isInCaptureMode;
    private bool _isLoadingSessionPreview;
    private bool _isSessionNoteSavedVisible;
    private bool _isCaptureDetailsVisible = true;
    private string _statusText = "Ready.";
    private double _previewScale = 1.0;
    private bool _isPanning;
    private bool _isSessionVideoSeekDragging;
    private bool _isUpdatingSessionVideoSeekSlider;
    private bool _isSessionVideoPlaying;
    private bool _resumeSessionVideoAfterSeek;
    private bool _isSessionVideoToolbarHovered;
    private bool _isSessionPreviewFullscreen;
    private bool _isBusy;
    private readonly GridLength _defaultSessionTopRowHeight;
    private readonly GridLength _defaultSessionBottomRowHeight;
    private readonly GridLength _defaultSessionLeftColumnWidth;
    private readonly GridLength _defaultSessionSpacerColumnWidth;
    private readonly Thickness _defaultSessionRootMargin;
    private const string DeletePendingMarkerFileName = ".revie-delete-pending";
    private System.Windows.Point _panStartPoint;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;

    public SessionBrowserWindow(string storageRoot, IEnumerable<string>? configuredProjects = null, IEnumerable<string>? configuredUsers = null)
    {
        _storageRoot = storageRoot;
        _configuredProjects = (configuredProjects ?? Array.Empty<string>())
            .Where(project => !string.IsNullOrWhiteSpace(project))
            .Select(project => project.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(project => project, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _configuredUsers = (configuredUsers ?? Array.Empty<string>())
            .Where(user => !string.IsNullOrWhiteSpace(user))
            .Select(user => user.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(user => user, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _sessionNotificationState = _sessionNotificationService.Load();

        Sessions = new ObservableCollection<SessionFolderSummary>();
        SessionProjectFilterChoices = new ObservableCollection<string>();
        SessionUserFilterChoices = new ObservableCollection<string>();
        SessionUserChoices = new ObservableCollection<string> { NoUserLabel };
        SessionContextProjectChoices = new ObservableCollection<string> { NoProjectLabel };
        SessionContextUserChoices = new ObservableCollection<string> { NoUserLabel };
        SessionCaptures = new ObservableCollection<CaptureListItemViewModel>();
        SessionPreviewCaptures = new ObservableCollection<CaptureListItemViewModel>();
        CapturePreviewVariants = new ObservableCollection<CapturePreviewVariant>();
        UploadedCapturePreviewVariants = new ObservableCollection<CapturePreviewVariant>();

        SessionCaptures.CollectionChanged += OnSessionCapturesCollectionChanged;
        SessionPreviewCaptures.CollectionChanged += OnSessionPreviewCapturesCollectionChanged;
        CapturePreviewVariants.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CaptureUploadHintText));
        UploadedCapturePreviewVariants.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CaptureUploadHintText));
        Sessions.CollectionChanged += OnSessionsCollectionChanged;
        _sessionNoteSavedFeedbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _sessionNoteSavedFeedbackTimer.Tick += (_, _) =>
        {
            _sessionNoteSavedFeedbackTimer.Stop();
            IsSessionNoteSavedVisible = false;
        };
        _sessionVideoPositionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _sessionVideoPositionTimer.Tick += SessionVideoPositionTimerOnTick;
        _sessionVideoToolbarFadeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1400)
        };
        _sessionVideoToolbarFadeTimer.Tick += SessionVideoToolbarFadeTimerOnTick;

        InitializeComponent();
        _defaultSessionTopRowHeight = SessionTopRow.Height;
        _defaultSessionBottomRowHeight = SessionBottomRow.Height;
        _defaultSessionLeftColumnWidth = SessionLeftColumn.Width;
        _defaultSessionSpacerColumnWidth = SessionSpacerColumn.Width;
        _defaultSessionRootMargin = SessionWindowRootGrid.Margin;
        DataContext = this;
        Loaded += (_, _) => FitPreviewToViewport();
        Closed += OnWindowClosed;
        PreviewKeyDown += SessionBrowserWindow_OnPreviewKeyDown;
        RefreshSessions();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<int>? UnviewedSessionsCountChanged;

    public ObservableCollection<SessionFolderSummary> Sessions { get; }
    public ObservableCollection<string> SessionProjectFilterChoices { get; }
    public ObservableCollection<string> SessionUserFilterChoices { get; }
    public ObservableCollection<string> SessionUserChoices { get; }
    public ObservableCollection<string> SessionContextProjectChoices { get; }
    public ObservableCollection<string> SessionContextUserChoices { get; }
    public ObservableCollection<CaptureListItemViewModel> SessionCaptures { get; }
    public ObservableCollection<CaptureListItemViewModel> SessionPreviewCaptures { get; }
    public ObservableCollection<CapturePreviewVariant> CapturePreviewVariants { get; }
    public ObservableCollection<CapturePreviewVariant> UploadedCapturePreviewVariants { get; }

    public bool IsSessionNoteSavedVisible
    {
        get => _isSessionNoteSavedVisible;
        private set
        {
            if (_isSessionNoteSavedVisible == value)
            {
                return;
            }

            _isSessionNoteSavedVisible = value;
            OnPropertyChanged(nameof(IsSessionNoteSavedVisible));
        }
    }

    public string SelectedSessionProjectFilter
    {
        get => _selectedSessionProjectFilter;
        set
        {
            var normalized = NormalizeSessionProjectFilterChoice(value);
            if (string.Equals(_selectedSessionProjectFilter, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _selectedSessionProjectFilter = normalized;
            OnPropertyChanged(nameof(SelectedSessionProjectFilter));
            ApplySessionFilters();
        }
    }

    public string SelectedSessionUserFilter
    {
        get => _selectedSessionUserFilter;
        set
        {
            var normalized = NormalizeSessionUserFilterChoice(value);
            if (string.Equals(_selectedSessionUserFilter, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _selectedSessionUserFilter = normalized;
            OnPropertyChanged(nameof(SelectedSessionUserFilter));
            ApplySessionFilters();
        }
    }

    public string SelectedCaptureUserChoice
    {
        get => _selectedCaptureUserChoice;
        set
        {
            var normalized = NormalizeCaptureUserChoice(value);
            if (string.Equals(_selectedCaptureUserChoice, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _selectedCaptureUserChoice = normalized;
            OnPropertyChanged(nameof(SelectedCaptureUserChoice));
        }
    }

    public CapturePreviewVariant? SelectedCapturePreviewVariant
    {
        get => _selectedCapturePreviewVariant;
        set
        {
            if (ReferenceEquals(_selectedCapturePreviewVariant, value))
            {
                return;
            }

            _selectedCapturePreviewVariant = value;
            OnPropertyChanged(nameof(SelectedCapturePreviewVariant));
            SetSelectedCapturePreviewPath(value?.ImagePath);
            ApplySelectedCapturePreviewVariant();
            if (value is not null && !value.IsOriginal)
            {
                SetCaptureCompareTabIndex(1, applySelection: false);
            }

            _selectedUploadedCapturePreviewVariant = value is { IsOriginal: false } ? value : null;
            OnPropertyChanged(nameof(SelectedUploadedCapturePreviewVariant));
            OnPropertyChanged(nameof(CaptureUploadHintText));
            if (IsInCaptureMode && SelectedCapture is not null && !IsSelectedCaptureVideo)
            {
                Dispatcher.BeginInvoke(new Action(FitPreviewToViewport), DispatcherPriority.Background);
            }
        }
    }

    public CapturePreviewVariant? SelectedUploadedCapturePreviewVariant
    {
        get => _selectedUploadedCapturePreviewVariant;
        set
        {
            if (ReferenceEquals(_selectedUploadedCapturePreviewVariant, value))
            {
                return;
            }

            _selectedUploadedCapturePreviewVariant = value;
            OnPropertyChanged(nameof(SelectedUploadedCapturePreviewVariant));
            if (value is null)
            {
                return;
            }

            SetCaptureCompareTabIndex(1, applySelection: false);
            if (!ReferenceEquals(SelectedCapturePreviewVariant, value))
            {
                SelectedCapturePreviewVariant = value;
            }
        }
    }

    public bool IsReviewCompareTabActive
    {
        get => _selectedCaptureCompareTabIndex == 0;
        set
        {
            if (!value)
            {
                return;
            }

            SetCaptureCompareTabIndex(0, applySelection: true);
        }
    }

    public bool IsRevisedUploadTabActive
    {
        get => _selectedCaptureCompareTabIndex == 1;
        set
        {
            if (!value)
            {
                return;
            }

            SetCaptureCompareTabIndex(1, applySelection: true);
        }
    }

    public SessionFolderSummary? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (ReferenceEquals(_selectedSession, value))
            {
                return;
            }

            _selectedSession = value;
            OnPropertyChanged(nameof(SelectedSession));
            OnPropertyChanged(nameof(CanOpen));
            OnPropertyChanged(nameof(RightPaneTitle));
            OnPropertyChanged(nameof(RightPaneSubtitle));
            OnPropertyChanged(nameof(SessionPreviewEmptyVisible));
            OnPropertyChanged(nameof(SessionPreviewEmptyMessage));
            OnPropertyChanged(nameof(EmptyRightPaneVisible));
            OnPropertyChanged(nameof(EmptyRightPaneMessage));

            if (!IsInCaptureMode)
            {
                _ = LoadSessionPreviewAsync(_selectedSession);
            }
        }
    }

    public CaptureListItemViewModel? SelectedCapture
    {
        get => _selectedCapture;
        set
        {
            if (ReferenceEquals(_selectedCapture, value))
            {
                return;
            }

            _selectedCapture = value;
            IsSessionNoteSavedVisible = false;
            SelectedCaptureUserChoice = ToCaptureUserChoice(value?.AssignedUser);
            RebuildCapturePreviewVariants(preferredPath: value?.ImagePath);
            SetCaptureCompareTabIndex(0, applySelection: true);
            OnPropertyChanged(nameof(IsSelectedCaptureVideo));
            OnPropertyChanged(nameof(SelectedCapture));
            OnPropertyChanged(nameof(CanOpen));
            OnPropertyChanged(nameof(RightPaneTitle));
            OnPropertyChanged(nameof(RightPaneSubtitle));
            OnPropertyChanged(nameof(EmptyRightPaneVisible));
            OnPropertyChanged(nameof(EmptyRightPaneMessage));
            OnPropertyChanged(nameof(CanManageCaptureUploads));
            OnPropertyChanged(nameof(CaptureUploadHintText));
            OnPropertyChanged(nameof(IsReviewCompareTabActive));
            OnPropertyChanged(nameof(IsRevisedUploadTabActive));
            if (value?.IsVideo == true)
            {
                ConfigureSessionVideoPreview(value.VideoPath);
            }
            else
            {
                ClearSessionVideoPreview();
            }
            FitPreviewToViewport();
        }
    }

    public bool IsInCaptureMode
    {
        get => _isInCaptureMode;
        private set
        {
            if (_isInCaptureMode == value)
            {
                return;
            }

            _isInCaptureMode = value;
            OnPropertyChanged(nameof(IsInCaptureMode));
            OnPropertyChanged(nameof(OpenButtonText));
            OnPropertyChanged(nameof(HeaderTitle));
            OnPropertyChanged(nameof(LeftPaneTitle));
            OnPropertyChanged(nameof(CanOpen));
            OnPropertyChanged(nameof(RightPaneTitle));
            OnPropertyChanged(nameof(RightPaneSubtitle));
            OnPropertyChanged(nameof(EmptyRightPaneVisible));
            OnPropertyChanged(nameof(EmptyRightPaneMessage));
            OnPropertyChanged(nameof(LeftEmptyVisible));
            OnPropertyChanged(nameof(LeftEmptyMessage));
            OnPropertyChanged(nameof(SessionPreviewEmptyVisible));
            OnPropertyChanged(nameof(SessionPreviewEmptyMessage));
            OnPropertyChanged(nameof(CanManageCaptureUploads));
            OnPropertyChanged(nameof(CaptureUploadHintText));
            OnPropertyChanged(nameof(IsReviewCompareTabActive));
            OnPropertyChanged(nameof(IsRevisedUploadTabActive));

            if (!value && _isSessionPreviewFullscreen)
            {
                SetSessionPreviewFullscreen(false);
            }
        }
    }

    public bool IsLoadingSessionPreview
    {
        get => _isLoadingSessionPreview;
        private set
        {
            if (_isLoadingSessionPreview == value)
            {
                return;
            }

            _isLoadingSessionPreview = value;
            OnPropertyChanged(nameof(IsLoadingSessionPreview));
            OnPropertyChanged(nameof(CanOpen));
            OnPropertyChanged(nameof(RightPaneSubtitle));
            OnPropertyChanged(nameof(SessionPreviewEmptyVisible));
            OnPropertyChanged(nameof(SessionPreviewEmptyMessage));
        }
    }

    public string HeaderTitle => IsInCaptureMode && SelectedSession is not null
        ? $"Review Session: {SelectedSession.SessionName}"
        : "Review Sessions";

    public string LeftPaneTitle => IsInCaptureMode ? "Session Captures" : $"Review Sessions ({Sessions.Count})";

    public string OpenButtonText => IsInCaptureMode ? "Open File" : "Open Session";

    public bool CanOpen => !IsBusy && (!IsInCaptureMode ? SelectedSession is not null : SelectedCapture is not null);

    public bool HasSessionCaptures => SessionCaptures.Count > 0;

    public bool HasSessionPreviewCaptures => SessionPreviewCaptures.Count > 0;
    public bool IsSelectedCaptureVideo => SelectedCapture?.IsVideo == true;
    public string SelectedCapturePreviewPath => _selectedCapturePreviewPath;
    public bool CanManageCaptureUploads => IsInCaptureMode && SelectedCapture is not null && !IsSelectedCaptureVideo;
    public string CaptureUploadHintText =>
        IsReviewCompareTabActive
            ? "Review mode: showing original capture."
            : UploadedCapturePreviewVariants.Count == 0
                ? "Drag images here or click Upload to add revised screenshots."
                : SelectedCapturePreviewVariant is null || SelectedCapturePreviewVariant.IsOriginal
                    ? "Select an uploaded revision to compare."
                    : $"Viewing: {SelectedCapturePreviewVariant.Label}";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanOpen));
        }
    }

    public bool SessionPreviewEmptyVisible => !IsInCaptureMode &&
                                              SelectedSession is not null &&
                                              !IsLoadingSessionPreview &&
                                              SessionPreviewCaptures.Count == 0;

    public string SessionPreviewEmptyMessage
    {
        get
        {
            if (SelectedSession is null)
            {
                return "Select a session from the left list.";
            }

            if (IsLoadingSessionPreview)
            {
                return "Loading preview media...";
            }

            return SelectedSession.CaptureCount == 0
                ? "This session has no saved captures."
                : "No valid media files were found in this session.";
        }
    }

    public double PreviewScale
    {
        get => _previewScale;
        private set
        {
            if (Math.Abs(_previewScale - value) < 0.0001)
            {
                return;
            }

            _previewScale = value;
            OnPropertyChanged(nameof(PreviewScale));
        }
    }

    public string RightPaneTitle
    {
        get
        {
            if (!IsInCaptureMode)
            {
                return SelectedSession is null ? "Session Details" : SelectedSession.SessionName;
            }

            return SelectedCapture is null ? "Capture Preview" : SelectedCapture.FullTimestampLabel;
        }
    }

    public string RightPaneSubtitle
    {
        get
        {
            if (!IsInCaptureMode)
            {
                if (SelectedSession is null)
                {
                    return "Select a session from the left list.";
                }

                if (IsLoadingSessionPreview)
                {
                    return $"{SelectedSession.ProjectDisplay} | {SelectedSession.TimeLabel} | Loading previews...";
                }

                if (!HasSessionPreviewCaptures)
                {
                    return $"{SelectedSession.ProjectDisplay} | {SelectedSession.TimeLabel} | No accessible media";
                }

                return $"{SelectedSession.ProjectDisplay} | {SelectedSession.TimeLabel} | {SelectedSession.CaptureCount} capture(s)";
            }

            if (SelectedCapture is null)
            {
                return "Select a capture to preview.";
            }

            return $"{SelectedCapture.ProjectDisplay} | {SelectedCapture.AssignedUserDisplay} | {SelectedCapture.ActiveWindowTitle}";
        }
    }

    public bool EmptyRightPaneVisible => IsInCaptureMode && SelectedCapture is null;

    public string EmptyRightPaneMessage => "Choose a capture from the left list.";

    public bool LeftEmptyVisible => !IsInCaptureMode ? Sessions.Count == 0 : SessionCaptures.Count == 0;

    public string LeftEmptyMessage => !IsInCaptureMode
        ? "No saved sessions found."
        : "This session has no valid captures.";

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool IsCaptureDetailsVisible
    {
        get => _isCaptureDetailsVisible;
        private set
        {
            if (_isCaptureDetailsVisible == value)
            {
                return;
            }

            _isCaptureDetailsVisible = value;
            OnPropertyChanged(nameof(IsCaptureDetailsVisible));
            OnPropertyChanged(nameof(CaptureDetailsToggleText));
        }
    }

    public string CaptureDetailsToggleText => IsCaptureDetailsVisible ? "Hide Details" : "Show Details";

    private void RefreshSessions()
    {
        _ = RefreshSessionsAsync();
    }

    private async Task RefreshSessionsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        var requestId = Interlocked.Increment(ref _sessionRefreshRequestId);
        StatusText = "Loading sessions...";
        try
        {
            var sessions = await Task.Run(() => _sessionBrowseService.GetSessions(_storageRoot));
            if (requestId != _sessionRefreshRequestId)
            {
                return;
            }

            _allSessions.Clear();
            _allSessions.AddRange(sessions);
            ApplySessionUnreadIndicators();

            IsInCaptureMode = false;
            RebuildSessionProjectFilterChoices();
            RebuildSessionUserFilterChoices();
            RebuildSessionContextChoices();
            ApplySessionFilters();

            SessionPreviewCaptures.Clear();
            SelectedCapture = null;
            StatusText = Sessions.Count == 0
                ? "No sessions found. Save a review session from the main window."
                : $"Loaded {Sessions.Count} session(s).";
        }
        catch (Exception ex)
        {
            StatusText = $"Session scan failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RebuildSessionProjectFilterChoices()
    {
        var current = SelectedSessionProjectFilter;
        var choices = new List<string> { AllProjectsLabel, NoProjectLabel };
        choices.AddRange(_configuredProjects);
        choices.AddRange(_allSessions
            .Select(session => CaptureService.NormalizeProject(session.Project))
            .Where(project => !string.IsNullOrWhiteSpace(project)));

        var distinct = choices
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(choice => choice, StringComparer.OrdinalIgnoreCase)
            .ToList();

        distinct.Remove(AllProjectsLabel);
        distinct.Remove(NoProjectLabel);
        distinct.Insert(0, NoProjectLabel);
        distinct.Insert(0, AllProjectsLabel);

        SessionProjectFilterChoices.Clear();
        foreach (var choice in distinct)
        {
            SessionProjectFilterChoices.Add(choice);
        }

        if (!SessionProjectFilterChoices.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            _selectedSessionProjectFilter = AllProjectsLabel;
            OnPropertyChanged(nameof(SelectedSessionProjectFilter));
        }
    }

    private void RebuildSessionUserFilterChoices()
    {
        var current = SelectedSessionUserFilter;
        var choices = new List<string> { AllUsersLabel, NoUserLabel };
        choices.AddRange(_configuredUsers);
        choices.AddRange(_allSessions.SelectMany(session => session.Users ?? new List<string>()));

        var distinct = choices
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(choice => choice, StringComparer.OrdinalIgnoreCase)
            .ToList();

        distinct.Remove(AllUsersLabel);
        distinct.Remove(NoUserLabel);
        distinct.Insert(0, NoUserLabel);
        distinct.Insert(0, AllUsersLabel);

        SessionUserFilterChoices.Clear();
        foreach (var choice in distinct)
        {
            SessionUserFilterChoices.Add(choice);
        }

        if (!SessionUserFilterChoices.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            _selectedSessionUserFilter = AllUsersLabel;
            OnPropertyChanged(nameof(SelectedSessionUserFilter));
        }
    }

    private void RebuildSessionContextChoices()
    {
        var projectChoices = _configuredProjects
            .Concat(_allSessions.Select(session => CaptureService.NormalizeProject(session.Project)))
            .Concat(SessionCaptures.Select(capture => CaptureService.NormalizeProject(capture.Project)))
            .Where(project => !string.IsNullOrWhiteSpace(project))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(project => project, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SessionContextProjectChoices.Clear();
        SessionContextProjectChoices.Add(NoProjectLabel);
        foreach (var project in projectChoices)
        {
            SessionContextProjectChoices.Add(project);
        }

        var userChoices = _configuredUsers
            .Concat(_allSessions.SelectMany(session => session.Users ?? new List<string>()))
            .Concat(SessionCaptures.SelectMany(capture => capture.AssignedUsers))
            .Where(user => !string.IsNullOrWhiteSpace(user))
            .Select(CaptureService.NormalizeUser)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(user => user, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SessionContextUserChoices.Clear();
        foreach (var user in userChoices)
        {
            SessionContextUserChoices.Add(user);
        }

        if (SessionContextUserChoices.Count == 0)
        {
            SessionContextUserChoices.Add(NoUserLabel);
        }
    }

    private void ApplySessionFilters()
    {
        var previousSessionPath = SelectedSession?.FolderPath ?? string.Empty;
        var filtered = _allSessions
            .Where(session => session.CaptureCount > 0)
            .Where(session => MatchesSessionProjectFilter(session, SelectedSessionProjectFilter))
            .Where(session => MatchesSessionUserFilter(session, SelectedSessionUserFilter))
            .OrderByDescending(session => session.StartLocal)
            .ToArray();

        Sessions.Clear();
        foreach (var session in filtered)
        {
            Sessions.Add(session);
        }

        SelectedSession = Sessions.FirstOrDefault(session =>
            string.Equals(session.FolderPath, previousSessionPath, StringComparison.OrdinalIgnoreCase))
            ?? Sessions.FirstOrDefault();

        OnPropertyChanged(nameof(LeftPaneTitle));
    }

    private static string NormalizeSessionProjectFilterChoice(string? choice)
    {
        if (string.IsNullOrWhiteSpace(choice))
        {
            return AllProjectsLabel;
        }

        return choice.Trim();
    }

    private static bool MatchesSessionProjectFilter(SessionFolderSummary session, string projectChoice)
    {
        var normalizedSessionProject = CaptureService.NormalizeProject(session.Project);
        if (string.Equals(projectChoice, AllProjectsLabel, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(projectChoice, NoProjectLabel, StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(normalizedSessionProject);
        }

        return string.Equals(normalizedSessionProject, projectChoice, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSessionUserFilterChoice(string? choice)
    {
        if (string.IsNullOrWhiteSpace(choice))
        {
            return AllUsersLabel;
        }

        return choice.Trim();
    }

    private static bool MatchesSessionUserFilter(SessionFolderSummary session, string userChoice)
    {
        if (string.Equals(userChoice, AllUsersLabel, StringComparison.Ordinal))
        {
            return true;
        }

        var users = session.Users ?? new List<string>();
        if (string.Equals(userChoice, NoUserLabel, StringComparison.Ordinal))
        {
            return users.Count == 0;
        }

        return users.Any(user => string.Equals(CaptureService.NormalizeUser(user), userChoice, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeCaptureUserChoice(string? choice)
    {
        var normalized = CaptureService.NormalizeUser(choice);
        return string.IsNullOrWhiteSpace(normalized) ? NoUserLabel : normalized;
    }

    private static string ToCaptureUserChoice(string? userName)
    {
        return NormalizeCaptureUserChoice(userName);
    }

    private static string ToCaptureUserName(string? userChoice)
    {
        var normalized = NormalizeCaptureUserChoice(userChoice);
        return string.Equals(normalized, NoUserLabel, StringComparison.Ordinal) ? string.Empty : normalized;
    }

    private void SetSelectedCapturePreviewPath(string? path)
    {
        var normalized = (path ?? string.Empty).Trim();
        if (string.Equals(_selectedCapturePreviewPath, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _selectedCapturePreviewPath = normalized;
        OnPropertyChanged(nameof(SelectedCapturePreviewPath));
    }

    private void SetCaptureCompareTabIndex(int tabIndex, bool applySelection)
    {
        var normalized = tabIndex <= 0 ? 0 : 1;
        if (_selectedCaptureCompareTabIndex == normalized)
        {
            if (applySelection)
            {
                ApplyCompareTabSelection();
            }

            return;
        }

        _selectedCaptureCompareTabIndex = normalized;
        OnPropertyChanged(nameof(IsReviewCompareTabActive));
        OnPropertyChanged(nameof(IsRevisedUploadTabActive));
        OnPropertyChanged(nameof(CaptureUploadHintText));
        if (applySelection)
        {
            ApplyCompareTabSelection();
        }
    }

    private void ApplyCompareTabSelection()
    {
        if (SelectedCapture is null || SelectedCapture.IsVideo)
        {
            return;
        }

        if (_selectedCaptureCompareTabIndex == 0)
        {
            var original = CapturePreviewVariants.FirstOrDefault(variant => variant.IsOriginal);
            if (original is not null && !ReferenceEquals(SelectedCapturePreviewVariant, original))
            {
                SelectedCapturePreviewVariant = original;
            }

            return;
        }

        if (SelectedCapturePreviewVariant is { IsOriginal: false })
        {
            return;
        }

        var firstUpload = UploadedCapturePreviewVariants.FirstOrDefault();
        if (firstUpload is not null)
        {
            SelectedCapturePreviewVariant = firstUpload;
        }
    }

    private void ApplySelectedCapturePreviewVariant()
    {
        if (SelectedCapture is null || SelectedCapture.IsVideo)
        {
            return;
        }

        var selectedVariant = SelectedCapturePreviewVariant;
        var overridePath = selectedVariant is not null && !selectedVariant.IsOriginal
            ? selectedVariant.ImagePath
            : string.Empty;
        SelectedCapture.SetPreviewOverridePath(overridePath);
    }

    private void RebuildCapturePreviewVariants(string? preferredPath = null)
    {
        var previousPath = !string.IsNullOrWhiteSpace(preferredPath)
            ? preferredPath.Trim()
            : (_selectedCapturePreviewVariant?.ImagePath ?? string.Empty);

        CapturePreviewVariants.Clear();
        UploadedCapturePreviewVariants.Clear();
        if (SelectedCapture is null || SelectedCapture.IsVideo)
        {
            _selectedCapturePreviewVariant = null;
            _selectedUploadedCapturePreviewVariant = null;
            OnPropertyChanged(nameof(SelectedCapturePreviewVariant));
            OnPropertyChanged(nameof(SelectedUploadedCapturePreviewVariant));
            SetSelectedCapturePreviewPath(string.Empty);
            SetCaptureCompareTabIndex(0, applySelection: false);
            OnPropertyChanged(nameof(CaptureUploadHintText));
            return;
        }

        var originalPath = (SelectedCapture.ImagePath ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(originalPath) && File.Exists(originalPath))
        {
            CapturePreviewVariants.Add(new CapturePreviewVariant("original", "Original Capture", originalPath, isOriginal: true));
        }

        var metadataPaths = SelectedCapture.Record.Metadata.UploadedImagePaths ?? new List<string>();
        var normalizedStoredPaths = new List<string>();
        foreach (var storedPath in metadataPaths)
        {
            if (!TryResolveCaptureVariantPath(SelectedCapture, storedPath, out var resolvedPath))
            {
                continue;
            }

            var normalizedStoredPath = ToStoredUploadPath(SelectedCapture, resolvedPath);
            if (normalizedStoredPaths.Contains(normalizedStoredPath, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            normalizedStoredPaths.Add(normalizedStoredPath);
            var uploadedVariant = new CapturePreviewVariant(
                normalizedStoredPath,
                $"Upload: {Path.GetFileName(resolvedPath)}",
                resolvedPath,
                isOriginal: false);
            CapturePreviewVariants.Add(uploadedVariant);
            UploadedCapturePreviewVariants.Add(uploadedVariant);
        }

        SelectedCapture.Record.Metadata.UploadedImagePaths = normalizedStoredPaths;
        var selection = CapturePreviewVariants.FirstOrDefault(variant =>
                            string.Equals(variant.ImagePath, previousPath, StringComparison.OrdinalIgnoreCase)) ??
                        CapturePreviewVariants.FirstOrDefault();
        _selectedCapturePreviewVariant = null;
        _selectedUploadedCapturePreviewVariant = null;
        OnPropertyChanged(nameof(SelectedCapturePreviewVariant));
        OnPropertyChanged(nameof(SelectedUploadedCapturePreviewVariant));
        if (selection is null)
        {
            SetSelectedCapturePreviewPath(string.Empty);
            SetCaptureCompareTabIndex(0, applySelection: false);
            OnPropertyChanged(nameof(CaptureUploadHintText));
            return;
        }

        SetCaptureCompareTabIndex(selection.IsOriginal ? 0 : 1, applySelection: false);
        SelectedCapturePreviewVariant = selection;
    }

    private static bool TryResolveCaptureVariantPath(CaptureListItemViewModel capture, string? storedPath, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        var rawPath = (storedPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return false;
        }

        try
        {
            resolvedPath = Path.IsPathRooted(rawPath)
                ? Path.GetFullPath(rawPath)
                : Path.GetFullPath(Path.Combine(capture.CaptureDirectory ?? string.Empty, rawPath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(resolvedPath) &&
               File.Exists(resolvedPath) &&
               IsSupportedUploadImagePath(resolvedPath);
    }

    private static string ToStoredUploadPath(CaptureListItemViewModel capture, string absolutePath)
    {
        var normalizedPath = (absolutePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return string.Empty;
        }

        var captureDirectory = (capture.CaptureDirectory ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(captureDirectory))
        {
            return normalizedPath;
        }

        try
        {
            var normalizedCaptureDirectory = Path.GetFullPath(captureDirectory);
            var normalizedAbsolutePath = Path.GetFullPath(normalizedPath);
            var relativePath = Path.GetRelativePath(normalizedCaptureDirectory, normalizedAbsolutePath);
            if (!relativePath.StartsWith("..", StringComparison.Ordinal))
            {
                return relativePath.Replace('\\', '/');
            }
        }
        catch
        {
            // Fallback to absolute path.
        }

        return normalizedPath;
    }

    private static bool IsSupportedUploadImagePath(string path)
    {
        var extension = Path.GetExtension(path ?? string.Empty);
        return SupportedUploadImageExtensions.Contains(extension);
    }

    private async Task LoadSessionPreviewAsync(SessionFolderSummary? session)
    {
        var requestId = Interlocked.Increment(ref _sessionPreviewRequestId);
        _sessionPreviewLoadCts?.Cancel();
        _sessionPreviewLoadCts?.Dispose();
        _sessionPreviewLoadCts = new CancellationTokenSource();
        var cancellationToken = _sessionPreviewLoadCts.Token;

        SessionPreviewCaptures.Clear();
        if (session is null || IsInCaptureMode)
        {
            if (requestId == _sessionPreviewRequestId)
            {
                IsLoadingSessionPreview = false;
            }

            return;
        }

        IsLoadingSessionPreview = true;
        try
        {
            var records = await Task.Run(
                () => _sessionBrowseService.GetSessionPreviewCaptures(session.FolderPath, 12),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (requestId != _sessionPreviewRequestId || !ReferenceEquals(_selectedSession, session) || IsInCaptureMode)
            {
                return;
            }

            var validRecords = records
                .Where(HasValidImageFile)
                .OrderBy(record => record.LocalTimestamp)
                .ToArray();

            foreach (var record in validRecords)
            {
                try
                {
                    SessionPreviewCaptures.Add(new CaptureListItemViewModel(record));
                }
                catch (Exception ex)
                {
                    AppLogService.Warn("Session.Preview.ViewModelSkipped", "Skipping invalid session preview record.", new
                    {
                        record.Metadata.JsonPath,
                        Error = ex.Message
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore canceled loads.
        }
        catch (Exception ex)
        {
            if (requestId == _sessionPreviewRequestId)
            {
                StatusText = $"Session preview failed: {ex.Message}";
            }
        }
        finally
        {
            if (requestId == _sessionPreviewRequestId)
            {
                IsLoadingSessionPreview = false;
            }
        }
    }

    private void OpenSelectedSession()
    {
        _ = OpenSelectedSessionAsync();
    }

    private async Task OpenSelectedSessionAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (SelectedSession is null)
        {
            StatusText = "Select a session first.";
            return;
        }

        var targetSession = SelectedSession;
        IsBusy = true;
        StatusText = $"Opening '{targetSession.SessionName}'...";
        try
        {
            var records = await Task.Run(() => _sessionBrowseService.GetSessionCaptures(targetSession.FolderPath));
            if (!ReferenceEquals(SelectedSession, targetSession))
            {
                return;
            }

            var validRecords = records.Where(HasValidImageFile).ToArray();
            var skippedCount = records.Count - validRecords.Length;
            StatusText = $"Preparing {validRecords.Length:n0} capture(s)...";

            var prepared = await Task.Run(() =>
            {
                var list = new List<CaptureListItemViewModel>(validRecords.Length);
                var skipped = 0;
                foreach (var record in validRecords)
                {
                    try
                    {
                        list.Add(new CaptureListItemViewModel(record));
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        AppLogService.Warn("Session.Open.ViewModelSkipped", "Skipping invalid session capture record.", new
                        {
                            record.Metadata.JsonPath,
                            Error = ex.Message
                        });
                    }
                }

                // Preload some visuals off the UI thread to reduce initial hitching.
                var prewarmCount = Math.Min(28, list.Count);
                for (var i = 0; i < prewarmCount; i++)
                {
                    _ = list[i].Thumbnail;
                }

                if (list.Count > 0 && !list[0].IsVideo)
                {
                    _ = list[0].PreviewImage;
                }

                return (list, skipped);
            });
            skippedCount += prepared.skipped;
            if (!ReferenceEquals(SelectedSession, targetSession))
            {
                return;
            }

            SessionCaptures.Clear();
            const int addBatchSize = 64;
            for (var i = 0; i < prepared.list.Count; i++)
            {
                SessionCaptures.Add(prepared.list[i]);
                if ((i + 1) % addBatchSize == 0)
                {
                    StatusText = $"Opening '{targetSession.SessionName}'... {i + 1:n0}/{prepared.list.Count:n0}";
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                    if (!ReferenceEquals(SelectedSession, targetSession))
                    {
                        return;
                    }
                }
            }

            RebuildSessionCaptureUserChoices();
            RebuildSessionContextChoices();

            if (SessionCaptures.Count == 0)
            {
                IsInCaptureMode = false;
                SelectedCapture = null;
                StatusText = records.Count == 0
                    ? $"Session '{targetSession.SessionName}' has no captures."
                    : $"Session '{targetSession.SessionName}' has captures, but no accessible media files.";
                return;
            }

            IsInCaptureMode = true;
            SelectedCapture = SessionCaptures.FirstOrDefault();
            await EnsureSelectedCapturePreviewReadyAsync();
            MarkSessionAsViewed(targetSession);
            StatusText = skippedCount > 0
                ? $"Loaded {SessionCaptures.Count} capture(s), skipped {skippedCount} invalid item(s)."
                : $"Loaded {SessionCaptures.Count} capture(s) for '{targetSession.SessionName}'.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to open session: {ex.Message}";
            AppLogService.Error("Session.Open.Failed", "Failed to open session captures.", ex, new
            {
                SelectedSession?.FolderPath
            });
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task EnsureSelectedCapturePreviewReadyAsync()
    {
        if (SelectedCapture is null)
        {
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            if (!SelectedCapture.IsVideo)
            {
                _ = SelectedCapture.PreviewImage;
                FitPreviewToViewport();
            }

            SessionPreviewScrollViewer.UpdateLayout();
        }, DispatcherPriority.Background);

        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
    }

    private async void DeleteSession(SessionFolderSummary? session)
    {
        if (session is null)
        {
            return;
        }

        if (IsBusy)
        {
            return;
        }

        var result = System.Windows.MessageBox.Show(
            $"Are you sure you want to delete session '{session.SessionName}' and its files?",
            "Delete Session",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var deletedSessionName = session.SessionName;
        var deletedSessionPath = session.FolderPath;
        var shouldRefreshSessions = false;
        string? refreshStatusOverride = null;

        MarkSessionPendingDelete(deletedSessionPath);
        IsBusy = true;
        StatusText = $"Deleting session '{deletedSessionName}'...";
        try
        {
            // Remove the session from bound collections first so WPF releases image/file handles.
            if (ReferenceEquals(SelectedSession, session))
            {
                SelectedSession = null;
            }
            _allSessions.RemoveAll(existing => string.Equals(existing.FolderPath, deletedSessionPath, StringComparison.OrdinalIgnoreCase));
            ApplySessionFilters();
            SessionPreviewCaptures.Clear();
            SessionCaptures.Clear();
            SelectedCapture = null;
            IsInCaptureMode = false;

            StatusText = $"Deleting session '{deletedSessionName}'... releasing preview handles";
            ReleaseSessionHandles(session);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

            if (DataBackupService.Enabled)
            {
                var backupPath = DataBackupService.TryBackupDirectory(deletedSessionPath, "sessions", deletedSessionName);
                if (!string.IsNullOrWhiteSpace(backupPath))
                {
                    AppLogService.Info("Session.Backup", "Session backup created before delete.", new
                    {
                        Session = deletedSessionName,
                        Backup = backupPath
                    });
                }
            }

            StatusText = $"Deleting session '{deletedSessionName}'... removing files";
            var deleteResult = await RemoveSessionDirectoryAsync(deletedSessionPath);
            if (!deleteResult.success)
            {
                var reason = string.IsNullOrWhiteSpace(deleteResult.error)
                    ? $"'{deletedSessionName}' is in use. Close any app using it and retry."
                    : deleteResult.error;
                shouldRefreshSessions = true;
                refreshStatusOverride = $"Delete session failed: {reason}";
                return;
            }

            _allSessions.RemoveAll(existing => string.Equals(existing.FolderPath, deletedSessionPath, StringComparison.OrdinalIgnoreCase));
            RebuildSessionProjectFilterChoices();
            RebuildSessionUserFilterChoices();
            ApplySessionFilters();

            StatusText = deleteResult.pendingCleanup
                ? $"Deleted session '{deletedSessionName}' (pending cleanup in background)."
                : $"Deleted session '{deletedSessionName}'.";
            AppLogService.Info("Session.Delete", "Session deleted.", new
            {
                Session = deletedSessionName,
                Path = deletedSessionPath,
                deleteResult.pendingCleanup
            });
        }
        catch (Exception ex)
        {
            shouldRefreshSessions = true;
            refreshStatusOverride = $"Delete session failed: {ex.Message}";
            AppLogService.Error("Session.Delete.Failed", "Session delete failed.", ex, new
            {
                Session = deletedSessionName,
                Path = deletedSessionPath
            });
        }
        finally
        {
            IsBusy = false;
        }

        if (!shouldRefreshSessions)
        {
            return;
        }

        await RefreshSessionsAsync();
        if (!string.IsNullOrWhiteSpace(refreshStatusOverride))
        {
            StatusText = refreshStatusOverride;
        }
    }

    private static void MarkSessionPendingDelete(string sessionFolderPath)
    {
        if (string.IsNullOrWhiteSpace(sessionFolderPath) || !Directory.Exists(sessionFolderPath))
        {
            return;
        }

        try
        {
            var markerPath = Path.Combine(sessionFolderPath, DeletePendingMarkerFileName);
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            // Best effort marker only.
        }
    }

    private void ReleaseSessionHandles(SessionFolderSummary session)
    {
        _sessionPreviewLoadCts?.Cancel();
        _sessionPreviewLoadCts?.Dispose();
        _sessionPreviewLoadCts = null;
        Interlocked.Increment(ref _sessionPreviewRequestId);

        _sessionNoteSavedFeedbackTimer.Stop();
        IsSessionNoteSavedVisible = false;
        StopPanning();
        SessionPreviewImage.Source = null;
        ClearSessionVideoPreview();

        if (!ReferenceEquals(SelectedSession, session))
        {
            return;
        }

        SelectedCapture = null;
        SessionCaptures.Clear();
        SessionPreviewCaptures.Clear();
        IsInCaptureMode = false;
        SelectedSession = null;
        ResetPreviewViewport();
    }

    private static async Task<bool> DeleteDirectoryWithRetriesAsync(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            return true;
        }

        const int maxAttempts = 8;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                ClearReadOnlyAttributes(folderPath);
                Directory.Delete(folderPath, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (attempt == 1)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }

                if (attempt == maxAttempts)
                {
                    return false;
                }

                await Task.Delay(160 * attempt);
            }
        }

        return false;
    }

    private async Task<(bool success, bool pendingCleanup, string error)> RemoveSessionDirectoryAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return (false, false, "Invalid session path.");
        }

        if (!Directory.Exists(folderPath))
        {
            return (true, false, string.Empty);
        }

        var queueRoot = Path.Combine(_storageRoot, ".revie-delete-queue");
        try
        {
            Directory.CreateDirectory(queueRoot);
        }
        catch
        {
            // Fallback to direct delete if queue cannot be created.
        }

        var relocatedPath = string.Empty;
        if (Directory.Exists(queueRoot))
        {
            try
            {
                var safeName = Path.GetFileName(folderPath).Trim();
                if (string.IsNullOrWhiteSpace(safeName))
                {
                    safeName = "session";
                }

                relocatedPath = Path.Combine(
                    queueRoot,
                    $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmssfff}_{Guid.NewGuid():N}");
                Directory.Move(folderPath, relocatedPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                relocatedPath = string.Empty;
            }
        }

        if (!string.IsNullOrWhiteSpace(relocatedPath))
        {
            var deletedQueueFolder = await DeleteDirectoryWithRetriesAsync(relocatedPath);
            if (deletedQueueFolder || !Directory.Exists(relocatedPath))
            {
                return (true, false, string.Empty);
            }

            // Session is detached from Sessions root, so it won't reappear.
            return (true, true, string.Empty);
        }

        var deleted = await DeleteDirectoryWithRetriesAsync(folderPath);
        if (deleted || !Directory.Exists(folderPath))
        {
            return (true, false, string.Empty);
        }

        return (false, false, $"'{folderPath}' is in use.");
    }

    private static void ClearReadOnlyAttributes(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(filePath, FileAttributes.Normal);
            }
            catch
            {
                // Best effort only.
            }
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(directoryPath, FileAttributes.Normal);
            }
            catch
            {
                // Best effort only.
            }
        }

        try
        {
            File.SetAttributes(rootPath, FileAttributes.Normal);
        }
        catch
        {
            // Best effort only.
        }
    }

    private void BackButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!IsInCaptureMode)
        {
            return;
        }

        SessionCaptures.Clear();
        SelectedCapture = null;
        IsInCaptureMode = false;
        ClearSessionVideoPreview();
        ResetPreviewViewport();
        _ = LoadSessionPreviewAsync(SelectedSession);
        StatusText = "Returned to review sessions.";
    }

    private void OpenButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!IsInCaptureMode)
        {
            OpenSelectedSession();
            return;
        }

        if (SelectedCapture is null)
        {
            StatusText = "Select a capture first.";
            return;
        }

        try
        {
            var targetPath = SelectedCapture.IsVideo
                ? SelectedCapture.MediaPath
                : (!string.IsNullOrWhiteSpace(SelectedCapturePreviewPath) && File.Exists(SelectedCapturePreviewPath)
                    ? SelectedCapturePreviewPath
                    : SelectedCapture.MediaPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{targetPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Open in Explorer failed: {ex.Message}";
        }
    }

    private void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!IsInCaptureMode)
        {
            RefreshSessions();
            return;
        }

        OpenSelectedSession();
    }

    private void SessionVideoFullscreenButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetSessionPreviewFullscreen(!_isSessionPreviewFullscreen);
    }

    private void SessionBrowserWindow_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_isSessionPreviewFullscreen)
        {
            return;
        }

        SetSessionPreviewFullscreen(false);
        e.Handled = true;
    }

    private void ToggleCaptureDetailsButton_OnClick(object sender, RoutedEventArgs e)
    {
        IsCaptureDetailsVisible = !IsCaptureDetailsVisible;
        Dispatcher.BeginInvoke(new Action(FitPreviewToViewport), DispatcherPriority.Background);
    }

    private void DeleteSessionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not SessionFolderSummary session)
        {
            return;
        }

        DeleteSession(session);
    }

    private void SessionListBox_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var item = FindAncestor<System.Windows.Controls.ListBoxItem>(source);
        if (item?.DataContext is SessionFolderSummary session)
        {
            SelectedSession = session;
        }
    }

    private void SessionContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu contextMenu)
        {
            return;
        }

        RebuildSessionContextChoices();

        var projectRoot = contextMenu.Items
            .OfType<System.Windows.Controls.MenuItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, "sessionProjectRoot", StringComparison.Ordinal));
        if (projectRoot is not null)
        {
            projectRoot.Items.Clear();
            foreach (var project in SessionContextProjectChoices)
            {
                var item = new System.Windows.Controls.MenuItem
                {
                    Header = project,
                    Tag = project
                };
                item.Click += AssignSessionProjectFromContextMenu_OnClick;
                projectRoot.Items.Add(item);
            }
        }

        var userRoot = contextMenu.Items
            .OfType<System.Windows.Controls.MenuItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, "sessionUserRoot", StringComparison.Ordinal));
        if (userRoot is not null)
        {
            userRoot.Items.Clear();
            foreach (var user in SessionContextUserChoices)
            {
                if (string.Equals(user, NoUserLabel, StringComparison.Ordinal))
                {
                    continue;
                }

                var item = new System.Windows.Controls.MenuItem
                {
                    Header = user,
                    Tag = user
                };
                item.Click += AssignSessionUserFromContextMenu_OnClick;
                userRoot.Items.Add(item);
            }

            if (userRoot.Items.Count == 0)
            {
                userRoot.Items.Add(new System.Windows.Controls.MenuItem
                {
                    Header = "(No users configured)",
                    IsEnabled = false
                });
            }
        }
    }

    private async void AssignSessionProjectFromContextMenu_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSession is null || sender is not System.Windows.Controls.MenuItem { Tag: string projectChoice })
        {
            return;
        }

        var targetProject = string.Equals(projectChoice, NoProjectLabel, StringComparison.Ordinal)
            ? string.Empty
            : CaptureService.NormalizeProject(projectChoice);
        var currentProject = CaptureService.NormalizeProject(SelectedSession.Project);
        if (string.Equals(currentProject, targetProject, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var selectedFolderPath = SelectedSession.FolderPath;
        try
        {
            var updated = await ApplyProjectToSessionAsync(selectedFolderPath, targetProject);
            RefreshSessions();
            SelectedSession = Sessions.FirstOrDefault(session =>
                string.Equals(session.FolderPath, selectedFolderPath, StringComparison.OrdinalIgnoreCase));

            StatusText = string.IsNullOrWhiteSpace(targetProject)
                ? $"Session project cleared ({updated} capture(s) updated)."
                : $"Session assigned to project '{targetProject}' ({updated} capture(s) updated).";
        }
        catch (Exception ex)
        {
            StatusText = $"Session project update failed: {ex.Message}";
        }
    }

    private async void AssignSessionUserFromContextMenu_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSession is null || sender is not System.Windows.Controls.MenuItem { Tag: string userChoice })
        {
            return;
        }

        var targetUser = CaptureService.NormalizeUser(userChoice);
        if (string.IsNullOrWhiteSpace(targetUser) || string.Equals(targetUser, NoUserLabel, StringComparison.Ordinal))
        {
            return;
        }

        var selectedFolderPath = SelectedSession.FolderPath;
        try
        {
            var updated = await AddUserTagToSessionAsync(selectedFolderPath, targetUser);
            RefreshSessions();
            SelectedSession = Sessions.FirstOrDefault(session =>
                string.Equals(session.FolderPath, selectedFolderPath, StringComparison.OrdinalIgnoreCase));

            StatusText = updated == 0
                ? $"Session already tagged for '{targetUser}'."
                : $"Session tagged for '{targetUser}' ({updated} capture(s) updated).";
        }
        catch (Exception ex)
        {
            StatusText = $"Session user update failed: {ex.Message}";
        }
    }

    private async void ClearSessionUsersFromContextMenu_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSession is null)
        {
            return;
        }

        var selectedFolderPath = SelectedSession.FolderPath;
        try
        {
            var updated = await ClearSessionUserTagsAsync(selectedFolderPath);
            RefreshSessions();
            SelectedSession = Sessions.FirstOrDefault(session =>
                string.Equals(session.FolderPath, selectedFolderPath, StringComparison.OrdinalIgnoreCase));

            StatusText = updated == 0
                ? "Session had no user tags to clear."
                : $"Cleared user tags for {updated} capture(s) in session.";
        }
        catch (Exception ex)
        {
            StatusText = $"Session user clear failed: {ex.Message}";
        }
    }

    private async void SetSessionReviewStateInReview_OnClick(object sender, RoutedEventArgs e)
    {
        await SetSelectedSessionReviewStateAsync(ReviewState.InReview);
    }

    private async void SetSessionReviewStateCompleted_OnClick(object sender, RoutedEventArgs e)
    {
        await SetSelectedSessionReviewStateAsync(ReviewState.Completed);
    }

    private async void ClearSessionReviewState_OnClick(object sender, RoutedEventArgs e)
    {
        await SetSelectedSessionReviewStateAsync(ReviewState.None);
    }

    private async Task SetSelectedSessionReviewStateAsync(string reviewState)
    {
        if (SelectedSession is null)
        {
            return;
        }

        var normalizedState = ReviewState.Normalize(reviewState);
        var selectedFolderPath = SelectedSession.FolderPath;
        try
        {
            var updated = await SetSessionReviewStateAsync(selectedFolderPath, normalizedState);
            RefreshSessions();
            SelectedSession = Sessions.FirstOrDefault(session =>
                string.Equals(session.FolderPath, selectedFolderPath, StringComparison.OrdinalIgnoreCase));

            StatusText = string.IsNullOrWhiteSpace(normalizedState)
                ? $"Session review state cleared ({updated} capture(s) updated)."
                : $"Session review state set to '{ReviewState.ToDisplay(normalizedState)}' ({updated} capture(s) updated).";
        }
        catch (Exception ex)
        {
            StatusText = $"Session review state update failed: {ex.Message}";
        }
    }

    private void CaptureListBox_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var item = FindAncestor<System.Windows.Controls.ListBoxItem>(source);
        if (item?.DataContext is CaptureListItemViewModel capture)
        {
            SelectedCapture = capture;
        }
    }

    private void CaptureContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu contextMenu)
        {
            return;
        }

        RebuildSessionContextChoices();

        var projectRoot = contextMenu.Items
            .OfType<System.Windows.Controls.MenuItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, "projectRoot", StringComparison.Ordinal));
        if (projectRoot is not null)
        {
            projectRoot.Items.Clear();
            foreach (var project in SessionContextProjectChoices)
            {
                var item = new System.Windows.Controls.MenuItem
                {
                    Header = project,
                    Tag = project
                };
                item.Click += AssignProjectFromContextMenu_OnClick;
                projectRoot.Items.Add(item);
            }
        }

        var userRoot = contextMenu.Items
            .OfType<System.Windows.Controls.MenuItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, "userRoot", StringComparison.Ordinal));
        if (userRoot is not null)
        {
            userRoot.Items.Clear();
            foreach (var user in SessionContextUserChoices)
            {
                if (string.Equals(user, NoUserLabel, StringComparison.Ordinal))
                {
                    continue;
                }

                var item = new System.Windows.Controls.MenuItem
                {
                    Header = user,
                    Tag = user
                };
                item.Click += AssignUserFromContextMenu_OnClick;
                userRoot.Items.Add(item);
            }

            if (userRoot.Items.Count == 0)
            {
                userRoot.Items.Add(new System.Windows.Controls.MenuItem
                {
                    Header = "(No users configured)",
                    IsEnabled = false
                });
            }
        }
    }

    private async void AssignProjectFromContextMenu_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedCapture is null || sender is not System.Windows.Controls.MenuItem { Tag: string projectChoice })
        {
            return;
        }

        var targetProject = string.Equals(projectChoice, NoProjectLabel, StringComparison.Ordinal)
            ? string.Empty
            : CaptureService.NormalizeProject(projectChoice);
        if (string.Equals(SelectedCapture.Project, targetProject, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var selectedCapture = SelectedCapture;
        SelectedCapture.Project = targetProject;
        if (!await PersistSelectedCaptureMetadataAsync(SelectedCapture))
        {
            return;
        }

        RebuildSessionContextChoices();
        RebuildSessionProjectFilterChoices();
        ApplySessionFilters();
        if (selectedCapture is not null && SessionCaptures.Contains(selectedCapture))
        {
            SelectedCapture = selectedCapture;
        }
        StatusText = string.IsNullOrWhiteSpace(targetProject)
            ? "Capture project cleared."
            : $"Capture assigned to project '{targetProject}'.";
        ShowSessionNoteSavedFeedback();
    }

    private async void AssignUserFromContextMenu_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedCapture is null || sender is not System.Windows.Controls.MenuItem { Tag: string userChoice })
        {
            return;
        }

        var normalizedUser = CaptureService.NormalizeUser(userChoice);
        if (string.IsNullOrWhiteSpace(normalizedUser) || string.Equals(normalizedUser, NoUserLabel, StringComparison.Ordinal))
        {
            return;
        }

        var tags = SelectedCapture.AssignedUsers.ToList();
        if (tags.Contains(normalizedUser, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var selectedCapture = SelectedCapture;
        tags.Add(normalizedUser);
        SelectedCapture.UserTagsText = string.Join(", ", CaptureService.NormalizeUsers(tags));
        if (!await PersistSelectedCaptureMetadataAsync(SelectedCapture))
        {
            return;
        }

        RebuildSessionCaptureUserChoices();
        RebuildSessionContextChoices();
        RebuildSessionUserFilterChoices();
        ApplySessionFilters();
        if (selectedCapture is not null && SessionCaptures.Contains(selectedCapture))
        {
            SelectedCapture = selectedCapture;
        }
        StatusText = $"Capture tagged for '{normalizedUser}'.";
        ShowSessionNoteSavedFeedback();
    }

    private async void ClearCaptureUsersFromContextMenu_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedCapture is null || SelectedCapture.AssignedUsers.Count == 0)
        {
            return;
        }

        var selectedCapture = SelectedCapture;
        SelectedCapture.UserTagsText = string.Empty;
        if (!await PersistSelectedCaptureMetadataAsync(SelectedCapture))
        {
            return;
        }

        RebuildSessionCaptureUserChoices();
        RebuildSessionContextChoices();
        RebuildSessionUserFilterChoices();
        ApplySessionFilters();
        if (selectedCapture is not null && SessionCaptures.Contains(selectedCapture))
        {
            SelectedCapture = selectedCapture;
        }
        StatusText = "Capture user tags cleared.";
        ShowSessionNoteSavedFeedback();
    }

    private async void SetCaptureReviewStateInReview_OnClick(object sender, RoutedEventArgs e)
    {
        await SetSelectedCaptureReviewStateAsync(ReviewState.InReview);
    }

    private async void SetCaptureReviewStateCompleted_OnClick(object sender, RoutedEventArgs e)
    {
        await SetSelectedCaptureReviewStateAsync(ReviewState.Completed);
    }

    private async void ClearCaptureReviewState_OnClick(object sender, RoutedEventArgs e)
    {
        await SetSelectedCaptureReviewStateAsync(ReviewState.None);
    }

    private async Task SetSelectedCaptureReviewStateAsync(string reviewState)
    {
        if (SelectedCapture is null)
        {
            return;
        }

        var normalizedState = ReviewState.Normalize(reviewState);
        if (string.Equals(SelectedCapture.ReviewState, normalizedState, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var selectedCapture = SelectedCapture;
        SelectedCapture.ReviewState = normalizedState;
        if (!await PersistSelectedCaptureMetadataAsync(SelectedCapture))
        {
            return;
        }

        if (selectedCapture is not null && SessionCaptures.Contains(selectedCapture))
        {
            SelectedCapture = selectedCapture;
        }

        StatusText = string.IsNullOrWhiteSpace(normalizedState)
            ? "Review state cleared."
            : $"Review state set to '{ReviewState.ToDisplay(normalizedState)}'.";
        AppLogService.Info("Session.Capture.ReviewState", "Session capture review state updated.", new
        {
            SelectedCapture.Record.Metadata.JsonPath,
            State = normalizedState
        });
        ShowSessionNoteSavedFeedback();
    }

    private void SessionCaptureNoteTextBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        IsSessionNoteSavedVisible = false;
        _sessionNoteSavedFeedbackTimer.Stop();
    }

    private async void SaveCaptureNoteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedCapture is null)
        {
            StatusText = "Select a capture to save notes.";
            return;
        }

        var success = await PersistSelectedCaptureMetadataAsync(SelectedCapture);
        if (!success)
        {
            return;
        }

        StatusText = "Note saved.";
        ShowSessionNoteSavedFeedback();
    }

    private async void AssignCaptureUserButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedCapture is null)
        {
            StatusText = "Select a capture first.";
            return;
        }

        var targetUser = ToCaptureUserName(SelectedCaptureUserChoice);
        if (string.IsNullOrWhiteSpace(targetUser))
        {
            SelectedCapture.UserTagsText = string.Empty;
        }
        else
        {
            var tags = SelectedCapture.AssignedUsers.ToList();
            if (!tags.Contains(targetUser, StringComparer.OrdinalIgnoreCase))
            {
                tags.Add(targetUser);
                SelectedCapture.UserTagsText = string.Join(", ", CaptureService.NormalizeUsers(tags));
            }
        }
        RebuildSessionCaptureUserChoices();

        var success = await PersistSelectedCaptureMetadataAsync(SelectedCapture);
        if (!success)
        {
            return;
        }

        StatusText = SelectedCapture.AssignedUsers.Count == 0
            ? "Capture user cleared."
            : $"Capture tagged for '{targetUser}'.";
        ShowSessionNoteSavedFeedback();
    }

    private async void UploadCaptureImagesButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!CanManageCaptureUploads || SelectedCapture is null)
        {
            StatusText = "Select an image capture first.";
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select revised images",
            Multiselect = true,
            CheckFileExists = true,
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All Files|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await ImportCaptureUploadImagesAsync(dialog.FileNames);
    }

    private void CaptureUploadDropZone_OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!CanManageCaptureUploads || !TryGetImagePathsFromDropData(e.Data, out _))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = System.Windows.DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void CaptureUploadDropZone_OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (!CanManageCaptureUploads || !TryGetImagePathsFromDropData(e.Data, out var filePaths))
        {
            StatusText = "Drop one or more image files.";
            return;
        }

        await ImportCaptureUploadImagesAsync(filePaths);
    }

    private static bool TryGetImagePathsFromDropData(System.Windows.IDataObject dataObject, out string[] filePaths)
    {
        filePaths = Array.Empty<string>();
        if (!dataObject.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            return false;
        }

        var dropped = dataObject.GetData(System.Windows.DataFormats.FileDrop) as string[];
        if (dropped is null || dropped.Length == 0)
        {
            return false;
        }

        filePaths = dropped
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Where(IsSupportedUploadImagePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return filePaths.Length > 0;
    }

    private async Task ImportCaptureUploadImagesAsync(IEnumerable<string> sourceImagePaths)
    {
        if (SelectedCapture is null || SelectedCapture.IsVideo)
        {
            StatusText = "Select an image capture first.";
            return;
        }

        var sourcePaths = sourceImagePaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Where(IsSupportedUploadImagePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourcePaths.Length == 0)
        {
            StatusText = "No valid image files were provided.";
            return;
        }

        var capture = SelectedCapture;
        try
        {
            var captureDirectory = (capture.CaptureDirectory ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(captureDirectory))
            {
                StatusText = "Cannot upload images: capture folder is missing.";
                return;
            }

            Directory.CreateDirectory(captureDirectory);
            var uploadDirectory = Path.Combine(captureDirectory, UploadsFolderName);
            Directory.CreateDirectory(uploadDirectory);

            var metadataUploadPaths = capture.Record.Metadata.UploadedImagePaths?.ToList() ?? new List<string>();
            var importedCount = 0;
            string? lastImportedResolvedPath = null;
            foreach (var sourcePath in sourcePaths)
            {
                var destinationPath = BuildUniqueUploadDestinationPath(uploadDirectory, Path.GetFileName(sourcePath));
                File.Copy(sourcePath, destinationPath, overwrite: false);
                var storedPath = ToStoredUploadPath(capture, destinationPath);
                if (metadataUploadPaths.Contains(storedPath, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                metadataUploadPaths.Add(storedPath);
                importedCount++;
                lastImportedResolvedPath = destinationPath;
            }

            if (importedCount <= 0)
            {
                StatusText = "No new images were added.";
                return;
            }

            capture.Record.Metadata.UploadedImagePaths = metadataUploadPaths;
            if (!await PersistSelectedCaptureMetadataAsync(capture))
            {
                return;
            }

            RebuildCapturePreviewVariants(lastImportedResolvedPath);
            StatusText = importedCount == 1
                ? "Added 1 uploaded image."
                : $"Added {importedCount:n0} uploaded images.";
            ShowSessionNoteSavedFeedback();
        }
        catch (Exception ex)
        {
            StatusText = $"Image upload failed: {ex.Message}";
            AppLogService.Error("Session.CaptureUpload.Failed", "Failed importing session capture uploads.", ex, new
            {
                capture.Record.Metadata.JsonPath
            });
        }
    }

    private static string BuildUniqueUploadDestinationPath(string uploadDirectory, string sourceFileName)
    {
        var baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(sourceFileName));
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "upload";
        }

        var extension = Path.GetExtension(sourceFileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".png";
        }

        var candidate = Path.Combine(uploadDirectory, $"{baseName}{extension}");
        var suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(uploadDirectory, $"{baseName}_{suffix:00}{extension}");
            suffix++;
        }

        return candidate;
    }

    private static string SanitizeFileName(string value)
    {
        var sanitized = (value ?? string.Empty).Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalid, '_');
        }

        return sanitized.Trim();
    }

    private void SessionListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInCaptureMode || SelectedSession is null)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source &&
            FindAncestor<System.Windows.Controls.Button>(source) is not null)
        {
            return;
        }

        OpenSelectedSession();
        e.Handled = true;
    }

    private void SessionPreviewScrollViewer_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (SelectedCapture is null || IsSelectedCaptureVideo)
        {
            return;
        }

        var oldScale = PreviewScale;
        var zoomFactor = e.Delta > 0 ? 1.12 : 0.88;
        var newScale = Math.Clamp(oldScale * zoomFactor, 0.2, 8.0);
        if (Math.Abs(newScale - oldScale) < 0.0001)
        {
            return;
        }

        var pointer = e.GetPosition(SessionPreviewScrollViewer);
        var contentX = (pointer.X + SessionPreviewScrollViewer.HorizontalOffset) / oldScale;
        var contentY = (pointer.Y + SessionPreviewScrollViewer.VerticalOffset) / oldScale;

        PreviewScale = newScale;
        SessionPreviewScrollViewer.UpdateLayout();
        SessionPreviewScrollViewer.ScrollToHorizontalOffset(Math.Max(0, (contentX * newScale) - pointer.X));
        SessionPreviewScrollViewer.ScrollToVerticalOffset(Math.Max(0, (contentY * newScale) - pointer.Y));
        e.Handled = true;
    }

    private void SessionPreviewScrollViewer_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (SelectedCapture is null || IsSelectedCaptureVideo)
        {
            return;
        }

        if (SessionPreviewScrollViewer.ExtentWidth <= SessionPreviewScrollViewer.ViewportWidth &&
            SessionPreviewScrollViewer.ExtentHeight <= SessionPreviewScrollViewer.ViewportHeight)
        {
            return;
        }

        _isPanning = true;
        _panStartPoint = e.GetPosition(SessionPreviewScrollViewer);
        _panStartHorizontalOffset = SessionPreviewScrollViewer.HorizontalOffset;
        _panStartVerticalOffset = SessionPreviewScrollViewer.VerticalOffset;
        SessionPreviewScrollViewer.Cursor = System.Windows.Input.Cursors.SizeAll;
        SessionPreviewScrollViewer.CaptureMouse();
        e.Handled = true;
    }

    private void SessionPreviewScrollViewer_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        var currentPoint = e.GetPosition(SessionPreviewScrollViewer);
        var deltaX = currentPoint.X - _panStartPoint.X;
        var deltaY = currentPoint.Y - _panStartPoint.Y;

        SessionPreviewScrollViewer.ScrollToHorizontalOffset(_panStartHorizontalOffset - deltaX);
        SessionPreviewScrollViewer.ScrollToVerticalOffset(_panStartVerticalOffset - deltaY);
    }

    private void SessionPreviewScrollViewer_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopPanning();
    }

    private void SessionPreviewScrollViewer_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        StopPanning();
    }

    private void SessionPreviewScrollViewer_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsInCaptureMode && SelectedCapture is not null && !_isPanning)
        {
            FitPreviewToViewport();
        }
    }

    private void StopPanning()
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        SessionPreviewScrollViewer.Cursor = System.Windows.Input.Cursors.Arrow;
        SessionPreviewScrollViewer.ReleaseMouseCapture();
    }

    private void FitPreviewToViewport()
    {
        if (!IsLoaded)
        {
            return;
        }

        if (SelectedCapture is null || !IsInCaptureMode || IsSelectedCaptureVideo)
        {
            ResetPreviewViewport();
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            SessionPreviewScrollViewer.UpdateLayout();
            if (SessionPreviewImage.Source is not System.Windows.Media.Imaging.BitmapSource source ||
                source.PixelWidth <= 0 ||
                source.PixelHeight <= 0)
            {
                ResetPreviewViewport();
                return;
            }

            var viewportWidth = SessionPreviewScrollViewer.ViewportWidth;
            var viewportHeight = SessionPreviewScrollViewer.ViewportHeight;
            if (viewportWidth <= 1 || viewportHeight <= 1)
            {
                viewportWidth = Math.Max(1, SessionPreviewScrollViewer.ActualWidth - 20);
                viewportHeight = Math.Max(1, SessionPreviewScrollViewer.ActualHeight - 20);
            }

            var fitScale = Math.Min(viewportWidth / source.PixelWidth, viewportHeight / source.PixelHeight);
            if (double.IsNaN(fitScale) || double.IsInfinity(fitScale) || fitScale <= 0)
            {
                fitScale = 1.0;
            }

            PreviewScale = Math.Clamp(fitScale, 0.1, 1.0);
            SessionPreviewScrollViewer.UpdateLayout();
            SessionPreviewScrollViewer.ScrollToHorizontalOffset(0);
            SessionPreviewScrollViewer.ScrollToVerticalOffset(0);
        }));
    }

    private void ResetPreviewViewport()
    {
        PreviewScale = 1.0;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            SessionPreviewScrollViewer.ScrollToHorizontalOffset(0);
            SessionPreviewScrollViewer.ScrollToVerticalOffset(0);
        }));
    }

    private void SetSessionPreviewFullscreen(bool isFullscreen)
    {
        if (_isSessionPreviewFullscreen == isFullscreen)
        {
            return;
        }

        _isSessionPreviewFullscreen = isFullscreen;
        if (isFullscreen)
        {
            SessionTopRow.Height = new GridLength(0);
            SessionBottomRow.Height = new GridLength(0);
            SessionLeftColumn.Width = new GridLength(0);
            SessionSpacerColumn.Width = new GridLength(0);
            SessionWindowRootGrid.Margin = new Thickness(0);
            SessionVideoFullscreenButton.Content = "\uE73F";
            SessionVideoFullscreenButton.ToolTip = "Exit fullscreen preview";
        }
        else
        {
            SessionTopRow.Height = _defaultSessionTopRowHeight;
            SessionBottomRow.Height = _defaultSessionBottomRowHeight;
            SessionLeftColumn.Width = _defaultSessionLeftColumnWidth;
            SessionSpacerColumn.Width = _defaultSessionSpacerColumnWidth;
            SessionWindowRootGrid.Margin = _defaultSessionRootMargin;
            SessionVideoFullscreenButton.Content = "\uE740";
            SessionVideoFullscreenButton.ToolTip = "Fullscreen preview";
        }

        ShowSessionVideoToolbar(restartAutoHide: true);
        Dispatcher.BeginInvoke(new Action(FitPreviewToViewport), DispatcherPriority.Background);
    }

    private void SessionPreviewContentGrid_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ShowSessionVideoToolbar(restartAutoHide: true);
    }

    private void SessionPreviewContentGrid_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        StartSessionVideoToolbarFadeCountdown();
    }

    private void SessionVideoToolbarBorder_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isSessionVideoToolbarHovered = true;
        ShowSessionVideoToolbar(restartAutoHide: false);
        _sessionVideoToolbarFadeTimer.Stop();
    }

    private void SessionVideoToolbarBorder_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isSessionVideoToolbarHovered = false;
        StartSessionVideoToolbarFadeCountdown();
    }

    private void SessionVideoToolbarFadeTimerOnTick(object? sender, EventArgs e)
    {
        _sessionVideoToolbarFadeTimer.Stop();
        if (_isSessionVideoToolbarHovered || _isSessionVideoSeekDragging || !IsSelectedCaptureVideo)
        {
            return;
        }

        FadeSessionVideoToolbarTo(0.08);
    }

    private void ShowSessionVideoToolbar(bool restartAutoHide)
    {
        if (!IsSelectedCaptureVideo || SessionVideoToolbarBorder.Visibility != Visibility.Visible)
        {
            return;
        }

        FadeSessionVideoToolbarTo(1.0);
        if (restartAutoHide)
        {
            StartSessionVideoToolbarFadeCountdown();
        }
    }

    private void StartSessionVideoToolbarFadeCountdown()
    {
        if (!IsSelectedCaptureVideo || _isSessionVideoToolbarHovered || _isSessionVideoSeekDragging)
        {
            return;
        }

        _sessionVideoToolbarFadeTimer.Stop();
        _sessionVideoToolbarFadeTimer.Start();
    }

    private void FadeSessionVideoToolbarTo(double targetOpacity)
    {
        var animation = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new QuadraticEase()
        };
        SessionVideoToolbarBorder.BeginAnimation(OpacityProperty, animation);
    }

    private void SessionVideoPositionTimerOnTick(object? sender, EventArgs e)
    {
        UpdateSessionVideoSeekSliderFromPlayer();
    }

    private void UpdateSessionVideoSeekSliderFromPlayer()
    {
        if (_isSessionVideoSeekDragging || _isUpdatingSessionVideoSeekSlider || SessionVideoPreviewElement.Source is null)
        {
            return;
        }

        if (!SessionVideoPreviewElement.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        var durationSeconds = Math.Max(0.1, SessionVideoPreviewElement.NaturalDuration.TimeSpan.TotalSeconds);
        var currentSeconds = Math.Clamp(SessionVideoPreviewElement.Position.TotalSeconds, 0, durationSeconds);

        _isUpdatingSessionVideoSeekSlider = true;
        try
        {
            SessionVideoSeekSlider.Maximum = durationSeconds;
            SessionVideoSeekSlider.Value = currentSeconds;
            SessionVideoTimeTextBlock.Text = BuildSessionVideoTimeText(TimeSpan.FromSeconds(currentSeconds), TimeSpan.FromSeconds(durationSeconds));
        }
        finally
        {
            _isUpdatingSessionVideoSeekSlider = false;
        }
    }

    private void ResetSessionVideoSeekSlider()
    {
        _isUpdatingSessionVideoSeekSlider = true;
        try
        {
            SessionVideoSeekSlider.Minimum = 0;
            SessionVideoSeekSlider.Maximum = 1;
            SessionVideoSeekSlider.Value = 0;
            SessionVideoTimeTextBlock.Text = "00:00 / 00:00";
        }
        finally
        {
            _isUpdatingSessionVideoSeekSlider = false;
        }
    }

    private void SeekSessionVideoToSliderPosition()
    {
        if (SessionVideoPreviewElement.Source is null || !SessionVideoPreviewElement.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        var durationSeconds = Math.Max(0.1, SessionVideoPreviewElement.NaturalDuration.TimeSpan.TotalSeconds);
        var seekSeconds = Math.Clamp(SessionVideoSeekSlider.Value, 0, durationSeconds);
        SessionVideoPreviewElement.Position = TimeSpan.FromSeconds(seekSeconds);
        SessionVideoTimeTextBlock.Text = BuildSessionVideoTimeText(TimeSpan.FromSeconds(seekSeconds), TimeSpan.FromSeconds(durationSeconds));
    }

    private static string BuildSessionVideoTimeText(TimeSpan current, TimeSpan total)
    {
        static string Format(TimeSpan value)
        {
            return value.TotalHours >= 1
                ? value.ToString(@"hh\:mm\:ss")
                : value.ToString(@"mm\:ss");
        }

        return $"{Format(current)} / {Format(total)}";
    }

    private void SessionVideoSeekSlider_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _sessionVideoToolbarFadeTimer.Stop();
        ShowSessionVideoToolbar(restartAutoHide: false);
        _resumeSessionVideoAfterSeek = _isSessionVideoPlaying;
        if (SessionVideoPreviewElement.Source is not null)
        {
            try
            {
                SessionVideoPreviewElement.Pause();
            }
            catch
            {
                // Ignore preview pause failures during seek.
            }
        }

        _isSessionVideoPlaying = false;
        _sessionVideoPositionTimer.Stop();
        _isSessionVideoSeekDragging = true;
    }

    private void SessionVideoSeekSlider_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSessionVideoSeekDragging)
        {
            return;
        }

        SeekSessionVideoToSliderPosition();
        _isSessionVideoSeekDragging = false;
        if (_resumeSessionVideoAfterSeek)
        {
            try
            {
                SessionVideoPreviewElement.Play();
                _isSessionVideoPlaying = true;
                _sessionVideoPositionTimer.Start();
            }
            catch
            {
                _isSessionVideoPlaying = false;
            }
        }
        _resumeSessionVideoAfterSeek = false;
        StartSessionVideoToolbarFadeCountdown();
    }

    private void SessionVideoSeekSlider_OnLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isSessionVideoSeekDragging)
        {
            return;
        }

        SeekSessionVideoToSliderPosition();
        _isSessionVideoSeekDragging = false;
        if (_resumeSessionVideoAfterSeek)
        {
            try
            {
                SessionVideoPreviewElement.Play();
                _isSessionVideoPlaying = true;
                _sessionVideoPositionTimer.Start();
            }
            catch
            {
                _isSessionVideoPlaying = false;
            }
        }
        _resumeSessionVideoAfterSeek = false;
        StartSessionVideoToolbarFadeCountdown();
    }

    private void SessionVideoSeekSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingSessionVideoSeekSlider)
        {
            return;
        }

        SeekSessionVideoToSliderPosition();
        ShowSessionVideoToolbar(restartAutoHide: true);
    }

    private void ConfigureSessionVideoPreview(string? videoPath)
    {
        try
        {
            _sessionVideoToolbarFadeTimer.Stop();
            _isSessionVideoToolbarHovered = false;
            _sessionVideoPositionTimer.Stop();
            SessionVideoPreviewElement.Stop();
            SessionVideoPreviewElement.Source = null;
            ResetSessionVideoSeekSlider();
            _isSessionVideoPlaying = false;
            if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            {
                StatusText = "Session video preview unavailable: file not found.";
                return;
            }

            SessionVideoPreviewElement.Source = new Uri(videoPath, UriKind.Absolute);
            SessionVideoPreviewElement.Position = TimeSpan.Zero;
            SessionVideoPreviewElement.Play();
            ShowSessionVideoToolbar(restartAutoHide: true);
        }
        catch
        {
            // Ignore video setup failures.
        }
    }

    private void ClearSessionVideoPreview()
    {
        try
        {
            _sessionVideoToolbarFadeTimer.Stop();
            _isSessionVideoToolbarHovered = false;
            _sessionVideoPositionTimer.Stop();
            SessionVideoPreviewElement.Stop();
            SessionVideoPreviewElement.Source = null;
            ResetSessionVideoSeekSlider();
            _isSessionVideoPlaying = false;
            SessionVideoToolbarBorder.BeginAnimation(OpacityProperty, null);
            SessionVideoToolbarBorder.Opacity = 1;
        }
        catch
        {
            // Ignore release failures.
        }
    }

    private void SessionVideoPlayButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SessionVideoPreviewElement.Play();
            _isSessionVideoPlaying = true;
            _sessionVideoPositionTimer.Start();
            ShowSessionVideoToolbar(restartAutoHide: true);
        }
        catch
        {
            // Ignore playback failures.
        }
    }

    private void SessionVideoPauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SessionVideoPreviewElement.Pause();
            _isSessionVideoPlaying = false;
            UpdateSessionVideoSeekSliderFromPlayer();
            ShowSessionVideoToolbar(restartAutoHide: true);
        }
        catch
        {
            // Ignore playback failures.
        }
    }

    private void SessionVideoStopButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SessionVideoPreviewElement.Stop();
            _sessionVideoPositionTimer.Stop();
            ResetSessionVideoSeekSlider();
            _isSessionVideoPlaying = false;
            ShowSessionVideoToolbar(restartAutoHide: true);
        }
        catch
        {
            // Ignore playback failures.
        }
    }

    private void SessionVideoRestartButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SessionVideoPreviewElement.Stop();
            SessionVideoPreviewElement.Position = TimeSpan.Zero;
            SessionVideoPreviewElement.Play();
            _isSessionVideoPlaying = true;
            _sessionVideoPositionTimer.Start();
            ShowSessionVideoToolbar(restartAutoHide: true);
        }
        catch
        {
            // Ignore playback failures.
        }
    }

    private void SessionVideoPreviewElement_OnMediaOpened(object sender, RoutedEventArgs e)
    {
        try
        {
            SessionVideoPreviewElement.Position = TimeSpan.Zero;
            SessionVideoPreviewElement.Pause();
            _isSessionVideoPlaying = false;
            UpdateSessionVideoSeekSliderFromPlayer();
            _sessionVideoPositionTimer.Start();
            ShowSessionVideoToolbar(restartAutoHide: true);
        }
        catch
        {
            // Ignore media-open failures.
        }
    }

    private void SessionVideoPreviewElement_OnMediaEnded(object sender, RoutedEventArgs e)
    {
        _isSessionVideoPlaying = false;
        _sessionVideoPositionTimer.Stop();
        UpdateSessionVideoSeekSliderFromPlayer();
        ShowSessionVideoToolbar(restartAutoHide: true);
    }

    private void SessionVideoPreviewElement_OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _sessionVideoToolbarFadeTimer.Stop();
        _sessionVideoPositionTimer.Stop();
        ResetSessionVideoSeekSlider();
        _isSessionVideoPlaying = false;
        SessionVideoToolbarBorder.BeginAnimation(OpacityProperty, null);
        SessionVideoToolbarBorder.Opacity = 1;
        StatusText = $"Session video preview failed: {e.ErrorException?.Message ?? "Unsupported media format."}";
    }

    private static bool HasValidImageFile(CaptureRecord record)
    {
        if (record.IsVideo)
        {
            return !string.IsNullOrWhiteSpace(record.VideoPath) && File.Exists(record.VideoPath);
        }

        return !string.IsNullOrWhiteSpace(record.ImagePath) && File.Exists(record.ImagePath);
    }

    private void ApplySessionUnreadIndicators()
    {
        foreach (var session in _allSessions)
        {
            session.UnviewedCaptureCount = _sessionNotificationService.GetUnviewedCaptureCount(
                _sessionNotificationState,
                session.FolderPath,
                session.CaptureCount);
        }

        UnviewedSessionsCountChanged?.Invoke(_allSessions.Count(session => session.HasUnviewedCaptures));
    }

    private void MarkSessionAsViewed(SessionFolderSummary? session)
    {
        if (session is null)
        {
            return;
        }

        _sessionNotificationService.MarkSessionViewed(_sessionNotificationState, session.FolderPath, session.CaptureCount);
        _sessionNotificationService.Save(_sessionNotificationState);
        session.UnviewedCaptureCount = 0;
        UnviewedSessionsCountChanged?.Invoke(_allSessions.Count(item => item.HasUnviewedCaptures));
    }

    private void RebuildSessionCaptureUserChoices()
    {
        var currentChoice = SelectedCaptureUserChoice;
        var users = _configuredUsers
            .Concat(SessionCaptures.SelectMany(capture => capture.AssignedUsers))
            .Where(user => !string.IsNullOrWhiteSpace(user))
            .Select(CaptureService.NormalizeUser)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(user => user, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SessionUserChoices.Clear();
        SessionUserChoices.Add(NoUserLabel);
        foreach (var user in users)
        {
            SessionUserChoices.Add(user);
        }

        SelectedCaptureUserChoice = SessionUserChoices.Contains(currentChoice, StringComparer.OrdinalIgnoreCase)
            ? currentChoice
            : ToCaptureUserChoice(SelectedCapture?.AssignedUser);
    }

    private async Task<int> ApplyProjectToSessionAsync(string sessionFolderPath, string targetProject)
    {
        var normalizedProject = CaptureService.NormalizeProject(targetProject);
        var updated = await UpdateSessionCaptureMetadataAsync(sessionFolderPath, metadata =>
        {
            var currentProject = CaptureService.NormalizeProject(metadata.Project);
            if (string.Equals(currentProject, normalizedProject, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            metadata.Project = normalizedProject;
            return true;
        });

        await UpdateSessionManifestProjectAsync(sessionFolderPath, normalizedProject);
        return updated;
    }

    private async Task<int> SetSessionReviewStateAsync(string sessionFolderPath, string reviewState)
    {
        var normalizedState = ReviewState.Normalize(reviewState);
        return await UpdateSessionCaptureMetadataAsync(sessionFolderPath, metadata =>
        {
            var current = ReviewState.Normalize(metadata.ReviewState);
            if (string.Equals(current, normalizedState, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            metadata.ReviewState = normalizedState;
            return true;
        });
    }

    private async Task<int> AddUserTagToSessionAsync(string sessionFolderPath, string userTag)
    {
        var normalizedUser = CaptureService.NormalizeUser(userTag);
        if (string.IsNullOrWhiteSpace(normalizedUser))
        {
            return 0;
        }

        return await UpdateSessionCaptureMetadataAsync(sessionFolderPath, metadata =>
        {
            var users = metadata.AssignedUsers is { Count: > 0 }
                ? CaptureService.NormalizeUsers(metadata.AssignedUsers).ToList()
                : CaptureService.NormalizeUsers(new[] { metadata.AssignedUser }).ToList();
            if (users.Contains(normalizedUser, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            users.Add(normalizedUser);
            users = CaptureService.NormalizeUsers(users).ToList();
            metadata.AssignedUsers = users;
            metadata.AssignedUser = users.FirstOrDefault() ?? string.Empty;
            return true;
        });
    }

    private async Task<int> ClearSessionUserTagsAsync(string sessionFolderPath)
    {
        return await UpdateSessionCaptureMetadataAsync(sessionFolderPath, metadata =>
        {
            var users = metadata.AssignedUsers is { Count: > 0 }
                ? CaptureService.NormalizeUsers(metadata.AssignedUsers).ToList()
                : CaptureService.NormalizeUsers(new[] { metadata.AssignedUser }).ToList();
            if (users.Count == 0)
            {
                return false;
            }

            metadata.AssignedUsers = new List<string>();
            metadata.AssignedUser = string.Empty;
            return true;
        });
    }

    private async Task<int> UpdateSessionCaptureMetadataAsync(
        string sessionFolderPath,
        Func<CaptureMetadata, bool> update)
    {
        var metadataPaths = _sessionBrowseService.GetSessionCaptureMetadataPaths(sessionFolderPath);
        if (metadataPaths.Count == 0)
        {
            return 0;
        }

        var updatedCount = 0;
        foreach (var jsonPath in metadataPaths)
        {
            CaptureMetadata? metadata;
            try
            {
                metadata = JsonSerializer.Deserialize<CaptureMetadata>(await File.ReadAllTextAsync(jsonPath), MetadataJsonOptions);
            }
            catch
            {
                continue;
            }

            if (metadata is null)
            {
                continue;
            }

            metadata.MetadataVersion = metadata.MetadataVersion <= 0 ? CaptureMetadata.CurrentMetadataVersion : metadata.MetadataVersion;
            metadata.ReviewState = ReviewState.Normalize(metadata.ReviewState);
            metadata.JsonPath = jsonPath;
            if (!update(metadata))
            {
                continue;
            }

            metadata.MetadataVersion = CaptureMetadata.CurrentMetadataVersion;
            metadata.ReviewState = ReviewState.Normalize(metadata.ReviewState);
            await SafeFileWriteService.WriteJsonAtomicAsync(jsonPath, metadata, MetadataJsonOptions);
            await PersistLinkedSourceMetadataAsync(metadata);
            updatedCount++;
        }

        return updatedCount;
    }

    private static async Task UpdateSessionManifestProjectAsync(string sessionFolderPath, string project)
    {
        var manifestPath = Path.Combine(sessionFolderPath ?? string.Empty, "session.json");
        if (!File.Exists(manifestPath))
        {
            return;
        }

        try
        {
            var node = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath)) as JsonObject;
            if (node is null)
            {
                return;
            }

            node["ActiveProjectAtSave"] = project;
            await SafeFileWriteService.WriteAllTextAtomicAsync(
                manifestPath,
                node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort update for manifest project.
        }
    }

    private async Task<bool> PersistSelectedCaptureMetadataAsync(CaptureListItemViewModel capture)
    {
        var jsonPath = capture.Record.Metadata.JsonPath;
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            StatusText = "Cannot save note: metadata path is missing.";
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(jsonPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            capture.Record.Metadata.MetadataVersion = CaptureMetadata.CurrentMetadataVersion;
            capture.Record.Metadata.ReviewState = ReviewState.Normalize(capture.Record.Metadata.ReviewState);
            await SafeFileWriteService.WriteJsonAtomicAsync(jsonPath, capture.Record.Metadata, MetadataJsonOptions);
            await PersistLinkedSourceMetadataAsync(capture.Record.Metadata);
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Note save failed: {ex.Message}";
            AppLogService.Error("Session.CaptureMetadata.SaveFailed", "Failed saving session capture metadata.", ex, new
            {
                capture.Record.Metadata.JsonPath
            });
            return false;
        }
    }

    private static async Task PersistLinkedSourceMetadataAsync(CaptureMetadata metadata)
    {
        var sourceJsonPath = metadata.SourceJsonPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceJsonPath) ||
            string.Equals(sourceJsonPath, metadata.JsonPath, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(sourceJsonPath))
        {
            return;
        }

        try
        {
            var sourceJson = await File.ReadAllTextAsync(sourceJsonPath);
            var sourceMetadata = JsonSerializer.Deserialize<CaptureMetadata>(sourceJson, MetadataJsonOptions);
            if (sourceMetadata is null)
            {
                return;
            }

            sourceMetadata.Note = metadata.Note ?? string.Empty;
            sourceMetadata.Project = CaptureService.NormalizeProject(metadata.Project);
            var assignedUsers = metadata.AssignedUsers is { Count: > 0 }
                ? CaptureService.NormalizeUsers(metadata.AssignedUsers).ToList()
                : CaptureService.NormalizeUsers(new[] { metadata.AssignedUser }).ToList();
            sourceMetadata.AssignedUsers = assignedUsers;
            sourceMetadata.AssignedUser = assignedUsers.FirstOrDefault() ?? string.Empty;
            sourceMetadata.ReviewState = ReviewState.Normalize(metadata.ReviewState);
            sourceMetadata.MetadataVersion = CaptureMetadata.CurrentMetadataVersion;
            await SafeFileWriteService.WriteJsonAtomicAsync(sourceJsonPath, sourceMetadata, MetadataJsonOptions);
        }
        catch
        {
            // Best effort only for linked source annotations.
        }
    }

    private void ShowSessionNoteSavedFeedback()
    {
        IsSessionNoteSavedVisible = true;
        SessionNoteSavedBadge.Opacity = 1;
        var pulse = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 1,
            To = 0.4,
            Duration = TimeSpan.FromMilliseconds(280),
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2)
        };
        SessionNoteSavedBadge.BeginAnimation(OpacityProperty, pulse);

        _sessionNoteSavedFeedbackTimer.Stop();
        _sessionNoteSavedFeedbackTimer.Start();
    }

    public sealed class CapturePreviewVariant
    {
        public CapturePreviewVariant(string key, string label, string imagePath, bool isOriginal)
        {
            Key = key;
            Label = label;
            ImagePath = imagePath;
            IsOriginal = isOriginal;
        }

        public string Key { get; }
        public string Label { get; }
        public string ImagePath { get; }
        public bool IsOriginal { get; }
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T target)
            {
                return target;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void OnSessionCapturesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasSessionCaptures));
        OnPropertyChanged(nameof(LeftEmptyVisible));
        OnPropertyChanged(nameof(LeftEmptyMessage));
        RebuildSessionCaptureUserChoices();
        RebuildSessionContextChoices();
    }

    private void OnSessionPreviewCapturesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasSessionPreviewCaptures));
        OnPropertyChanged(nameof(CanOpen));
        OnPropertyChanged(nameof(RightPaneSubtitle));
        OnPropertyChanged(nameof(SessionPreviewEmptyVisible));
        OnPropertyChanged(nameof(SessionPreviewEmptyMessage));
    }

    private void OnSessionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(LeftPaneTitle));
        OnPropertyChanged(nameof(LeftEmptyVisible));
        OnPropertyChanged(nameof(LeftEmptyMessage));
        RebuildSessionContextChoices();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _sessionPreviewLoadCts?.Cancel();
        _sessionPreviewLoadCts?.Dispose();
        _sessionPreviewLoadCts = null;
        _sessionNoteSavedFeedbackTimer.Stop();
        _sessionVideoToolbarFadeTimer.Stop();
        _sessionVideoPositionTimer.Stop();
        ClearSessionVideoPreview();
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
