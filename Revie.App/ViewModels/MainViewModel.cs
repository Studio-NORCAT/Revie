using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Revie.App.Infrastructure;
using Revie.App.Models;
using Revie.App.Services;

namespace Revie.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const string AllProjectsLabel = "(All Projects)";
    private const string NoProjectLabel = "(No Project)";
    private const string AllUsersLabel = "(All Users)";
    private const string NoUserLabel = "(No User)";

    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly CaptureIndexService _captureIndexService;
    private readonly Func<string> _storageRootProvider;
    private readonly HashSet<string> _knownProjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownUsers = new(StringComparer.OrdinalIgnoreCase);

    private bool _isBusy;
    private string _statusText = "Ready.";
    private string _activeCaptureProjectChoice = NoProjectLabel;
    private string _activeCaptureUserChoice = NoUserLabel;
    private string _filterSearchText = string.Empty;
    private string _filterStartText = string.Empty;
    private string _filterEndText = string.Empty;
    private string _filterProjectChoice = AllProjectsLabel;
    private string _filterUserChoice = AllUsersLabel;
    private string _filterUserText = string.Empty;
    private string _latestProjectFilterChoice = AllProjectsLabel;
    private string _latestUserFilterChoice = AllUsersLabel;
    private string _selectedProjectChoice = NoProjectLabel;
    private string _selectedUserChoice = NoUserLabel;
    private string _appliedSearchText = string.Empty;
    private DateTime? _appliedStartDate;
    private DateTime? _appliedEndDate;
    private string _appliedProjectChoice = AllProjectsLabel;
    private string _appliedUserChoice = AllUsersLabel;
    private string _appliedUserText = string.Empty;
    private CaptureListItemViewModel? _selectedCapture;
    private double _previewScale = 1.0;
    private bool _isPreviewLoading;
    private bool _isNotesVisible;
    private bool _isSessionActive;
    private string _sessionSummary = "No active review session.";
    private bool _isVideoRecording;
    private string _videoSummary = "No active video segment.";
    private int _unviewedSessionsCount;
    private bool _suppressActiveProjectNotifications;
    private bool _suppressActiveUserNotifications;

    public MainViewModel(CaptureIndexService captureIndexService, Func<string> storageRootProvider)
    {
        _captureIndexService = captureIndexService;
        _storageRootProvider = storageRootProvider;

        Captures = new ObservableCollection<CaptureListItemViewModel>();
        Captures.CollectionChanged += (_, _) => RaiseCommandCanExecuteChanged();
        CaptureView = CollectionViewSource.GetDefaultView(Captures);
        CaptureView.Filter = FilterCapture;
        CaptureView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CaptureListItemViewModel.DayGroup)));

        ProjectChoices = new ObservableCollection<string> { NoProjectLabel };
        UserChoices = new ObservableCollection<string> { NoUserLabel };
        ActiveCaptureProjectChoices = new ObservableCollection<string> { NoProjectLabel };
        ActiveCaptureUserChoices = new ObservableCollection<string> { NoUserLabel };
        FilterProjectChoices = new ObservableCollection<string> { AllProjectsLabel, NoProjectLabel };
        FilterUserChoices = new ObservableCollection<string> { AllUsersLabel, NoUserLabel };
        LatestProjectFilterChoices = new ObservableCollection<string> { AllProjectsLabel, NoProjectLabel };
        LatestUserFilterChoices = new ObservableCollection<string> { AllUsersLabel, NoUserLabel };

        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsBusy);
        OpenInExplorerCommand = new RelayCommand(_ => OpenSelectedInExplorer(), _ => SelectedCapture is not null);
        OpenSettingsCommand = new RelayCommand(_ => SettingsRequested?.Invoke(this, EventArgs.Empty));
        ApplyFiltersCommand = new RelayCommand(_ => ApplyFilters());
        ClearFiltersCommand = new RelayCommand(_ => ClearFilters());
        SetQuickRangeCommand = new RelayCommand(parameter => ApplyQuickRange(parameter as string));
        DeleteSelectedCaptureCommand = new RelayCommand(_ => DeleteSelectedCapture(), _ => SelectedCapture is not null);
        DeleteCaptureCommand = new RelayCommand(parameter => DeleteCapture(parameter as CaptureListItemViewModel), parameter => parameter is CaptureListItemViewModel);
        PurgeRecentCapturesCommand = new RelayCommand(_ => PurgeRecentCaptures(), _ => Captures.Count > 0 && !IsBusy);
        AssignProjectCommand = new RelayCommand(async _ => await AssignSelectedCaptureToProjectAsync(), _ => SelectedCapture is not null);
        AssignUserCommand = new RelayCommand(async _ => await AssignSelectedCaptureToUserAsync(), _ => SelectedCapture is not null);
        SaveNoteCommand = new RelayCommand(async _ => await PersistSelectedCaptureMetadataAsync(), _ => SelectedCapture is not null);
        StartSessionCommand = new RelayCommand(_ => StartSessionRequested?.Invoke(this, EventArgs.Empty), _ => !IsSessionActive);
        SaveSessionCommand = new RelayCommand(_ => SaveSessionRequested?.Invoke(this, EventArgs.Empty), _ => IsSessionActive);
        CancelSessionCommand = new RelayCommand(_ => CancelSessionRequested?.Invoke(this, EventArgs.Empty), _ => IsSessionActive);
        OpenSessionsCommand = new RelayCommand(_ => OpenSessionsRequested?.Invoke(this, EventArgs.Empty));
        StartVideoCommand = new RelayCommand(_ => StartVideoRequested?.Invoke(this, EventArgs.Empty), _ => !IsVideoRecording);
        StopVideoCommand = new RelayCommand(_ => StopVideoRequested?.Invoke(this, EventArgs.Empty), _ => IsVideoRecording);
    }

    public event EventHandler? SettingsRequested;
    public event EventHandler? StartSessionRequested;
    public event EventHandler? SaveSessionRequested;
    public event EventHandler? CancelSessionRequested;
    public event EventHandler? OpenSessionsRequested;
    public event EventHandler? StartVideoRequested;
    public event EventHandler? StopVideoRequested;
    public event Action<string>? ActiveCaptureProjectChanged;
    public event Action<string>? ActiveCaptureUserChanged;

    public ObservableCollection<CaptureListItemViewModel> Captures { get; }
    public ICollectionView CaptureView { get; }
    public ObservableCollection<string> ProjectChoices { get; }
    public ObservableCollection<string> UserChoices { get; }
    public ObservableCollection<string> ActiveCaptureProjectChoices { get; }
    public ObservableCollection<string> ActiveCaptureUserChoices { get; }
    public ObservableCollection<string> FilterProjectChoices { get; }
    public ObservableCollection<string> FilterUserChoices { get; }
    public ObservableCollection<string> LatestProjectFilterChoices { get; }
    public ObservableCollection<string> LatestUserFilterChoices { get; }
    public bool HasVisibleCaptures => CaptureView.Cast<object>().Any();
    public bool HasNoVisibleCaptures => !HasVisibleCaptures;

    public ICommand RefreshCommand { get; }
    public ICommand OpenInExplorerCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand ApplyFiltersCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public ICommand SetQuickRangeCommand { get; }
    public ICommand DeleteSelectedCaptureCommand { get; }
    public ICommand DeleteCaptureCommand { get; }
    public ICommand PurgeRecentCapturesCommand { get; }
    public ICommand AssignProjectCommand { get; }
    public ICommand AssignUserCommand { get; }
    public ICommand SaveNoteCommand { get; }
    public ICommand StartSessionCommand { get; }
    public ICommand SaveSessionCommand { get; }
    public ICommand CancelSessionCommand { get; }
    public ICommand OpenSessionsCommand { get; }
    public ICommand StartVideoCommand { get; }
    public ICommand StopVideoCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ActiveCaptureProjectChoice
    {
        get => _activeCaptureProjectChoice;
        set
        {
            var normalizedChoice = NormalizeProjectSelectionChoice(value, NoProjectLabel);
            if (!SetProperty(ref _activeCaptureProjectChoice, normalizedChoice))
            {
                return;
            }

            OnPropertyChanged(nameof(ActiveCaptureProjectName));

            if (_suppressActiveProjectNotifications)
            {
                return;
            }

            LatestProjectFilterChoice = ToLatestFilterChoiceFromActiveProjectChoice(normalizedChoice);
            ActiveCaptureProjectChanged?.Invoke(ToProjectName(normalizedChoice));
        }
    }

    public string ActiveCaptureProjectName => ToProjectName(_activeCaptureProjectChoice);

    public string ActiveCaptureUserChoice
    {
        get => _activeCaptureUserChoice;
        set
        {
            var normalizedChoice = NormalizeUserSelectionChoice(value, NoUserLabel);
            if (!SetProperty(ref _activeCaptureUserChoice, normalizedChoice))
            {
                return;
            }

            if (_suppressActiveUserNotifications)
            {
                return;
            }

            LatestUserFilterChoice = ToLatestUserFilterChoiceFromActiveUserChoice(normalizedChoice);
            ActiveCaptureUserChanged?.Invoke(ToUserName(normalizedChoice));
        }
    }

    public string FilterSearchText
    {
        get => _filterSearchText;
        set => SetProperty(ref _filterSearchText, value);
    }

    public string FilterStartText
    {
        get => _filterStartText;
        set => SetProperty(ref _filterStartText, value);
    }

    public string FilterEndText
    {
        get => _filterEndText;
        set => SetProperty(ref _filterEndText, value);
    }

    public string FilterProjectChoice
    {
        get => _filterProjectChoice;
        set => SetProperty(ref _filterProjectChoice, value);
    }

    public string FilterUserChoice
    {
        get => _filterUserChoice;
        set => SetProperty(ref _filterUserChoice, value);
    }

    public string FilterUserText
    {
        get => _filterUserText;
        set => SetProperty(ref _filterUserText, value);
    }

    public string SelectedProjectChoice
    {
        get => _selectedProjectChoice;
        set => SetProperty(ref _selectedProjectChoice, value);
    }

    public string SelectedUserChoice
    {
        get => _selectedUserChoice;
        set => SetProperty(ref _selectedUserChoice, value);
    }

    public string LatestProjectFilterChoice
    {
        get => _latestProjectFilterChoice;
        set
        {
            if (SetProperty(ref _latestProjectFilterChoice, NormalizeProjectFilterChoice(value)))
            {
                RefreshFilter();
            }
        }
    }

    public string LatestUserFilterChoice
    {
        get => _latestUserFilterChoice;
        set
        {
            if (SetProperty(ref _latestUserFilterChoice, NormalizeUserFilterChoice(value)))
            {
                RefreshFilter();
            }
        }
    }

    public string ActiveFilterSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_appliedSearchText) &&
                !_appliedStartDate.HasValue &&
                !_appliedEndDate.HasValue &&
                string.Equals(_appliedProjectChoice, AllProjectsLabel, StringComparison.Ordinal) &&
                string.Equals(_appliedUserChoice, AllUsersLabel, StringComparison.Ordinal) &&
                string.IsNullOrWhiteSpace(_appliedUserText))
            {
                return "All captures";
            }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(_appliedSearchText))
            {
                parts.Add($"Search: '{_appliedSearchText}'");
            }

            if (_appliedStartDate.HasValue || _appliedEndDate.HasValue)
            {
                var start = _appliedStartDate?.ToString("yyyy-MM-dd") ?? "...";
                var end = _appliedEndDate?.ToString("yyyy-MM-dd") ?? "...";
                parts.Add($"Date: {start} to {end}");
            }

            if (!string.Equals(_appliedProjectChoice, AllProjectsLabel, StringComparison.Ordinal))
            {
                parts.Add($"Project: {_appliedProjectChoice}");
            }

            if (!string.Equals(_appliedUserChoice, AllUsersLabel, StringComparison.Ordinal))
            {
                parts.Add($"User: {_appliedUserChoice}");
            }

            if (!string.IsNullOrWhiteSpace(_appliedUserText))
            {
                parts.Add($"User search: '{_appliedUserText.Trim()}'");
            }

            return string.Join(" | ", parts);
        }
    }

    public CaptureListItemViewModel? SelectedCapture
    {
        get => _selectedCapture;
        set
        {
            if (SetProperty(ref _selectedCapture, value))
            {
                SelectedProjectChoice = ToProjectChoice(value?.Project);
                SelectedUserChoice = ToUserChoice(value?.AssignedUser);
                OnPropertyChanged(nameof(IsSelectedCaptureVideo));
                RaiseCommandCanExecuteChanged();
            }
        }
    }

    public bool IsSelectedCaptureVideo => SelectedCapture?.IsVideo == true;

    public double PreviewScale
    {
        get => _previewScale;
        set => SetProperty(ref _previewScale, value);
    }

    public bool IsPreviewLoading
    {
        get => _isPreviewLoading;
        private set => SetProperty(ref _isPreviewLoading, value);
    }

    public bool IsNotesVisible
    {
        get => _isNotesVisible;
        set => SetProperty(ref _isNotesVisible, value);
    }

    public bool IsSessionActive
    {
        get => _isSessionActive;
        private set
        {
            if (SetProperty(ref _isSessionActive, value))
            {
                RaiseCommandCanExecuteChanged();
            }
        }
    }

    public string SessionSummary
    {
        get => _sessionSummary;
        private set => SetProperty(ref _sessionSummary, value);
    }

    public bool IsVideoRecording
    {
        get => _isVideoRecording;
        private set
        {
            if (SetProperty(ref _isVideoRecording, value))
            {
                RaiseCommandCanExecuteChanged();
            }
        }
    }

    public string VideoSummary
    {
        get => _videoSummary;
        private set => SetProperty(ref _videoSummary, value);
    }

    public int UnviewedSessionsCount
    {
        get => _unviewedSessionsCount;
        private set
        {
            if (!SetProperty(ref _unviewedSessionsCount, Math.Max(0, value)))
            {
                return;
            }

            OnPropertyChanged(nameof(HasUnviewedSessions));
            OnPropertyChanged(nameof(UnviewedSessionsBadgeText));
        }
    }

    public bool HasUnviewedSessions => _unviewedSessionsCount > 0;

    public string UnviewedSessionsBadgeText => _unviewedSessionsCount > 99
        ? "99+"
        : _unviewedSessionsCount.ToString(CultureInfo.InvariantCulture);

    public void ApplyTaxonomySettings(IEnumerable<string> projects, string activeProject, IEnumerable<string> users, string activeUser)
    {
        var activeChoice = ToProjectChoice(activeProject);
        var activeUserChoice = ToUserChoice(activeUser);
        _knownProjects.Clear();
        _knownUsers.Clear();
        AddProjects(projects);
        AddProject(CaptureService.NormalizeProject(activeProject));
        AddUsers(users);
        AddUser(CaptureService.NormalizeUser(activeUser));
        AddProjects(Captures.Select(capture => capture.Project));
        AddUsers(Captures.SelectMany(capture => capture.AssignedUsers));
        _suppressActiveProjectNotifications = true;
        _suppressActiveUserNotifications = true;
        try
        {
            ActiveCaptureProjectChoice = activeChoice;
            ActiveCaptureUserChoice = activeUserChoice;
        }
        finally
        {
            _suppressActiveProjectNotifications = false;
            _suppressActiveUserNotifications = false;
        }

        RebuildProjectCollections();
        LatestProjectFilterChoice = ToLatestFilterChoiceFromActiveProjectChoice(activeChoice);
        LatestUserFilterChoice = ToLatestUserFilterChoiceFromActiveUserChoice(activeUserChoice);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        var storageRoot = _storageRootProvider();
        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            StatusText = "Storage root is not configured.";
            return;
        }

        IsBusy = true;
        StatusText = "Scanning captures...";

        try
        {
            Captures.Clear();
            _knownProjects.Clear();
            _knownUsers.Clear();

            var skipped = 0;
            var loaded = 0;
            var batch = new List<CaptureListItemViewModel>(40);

            void FlushBatch()
            {
                if (batch.Count == 0)
                {
                    return;
                }

                using (CaptureView.DeferRefresh())
                {
                    foreach (var item in batch)
                    {
                        Captures.Add(item);
                    }
                }

                AddProjects(batch.Select(capture => capture.Project));
                AddUsers(batch.SelectMany(capture => capture.AssignedUsers));
                loaded += batch.Count;
                batch.Clear();
            }

            await foreach (var record in _captureIndexService.ScanStreamAsync(storageRoot, cancellationToken))
            {
                try
                {
                    batch.Add(new CaptureListItemViewModel(record));
                    if (batch.Count >= 40)
                    {
                        FlushBatch();
                        StatusText = $"Scanning captures... {loaded:n0} loaded";
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                            () => { },
                            DispatcherPriority.Background,
                            cancellationToken);
                    }
                }
                catch
                {
                    skipped++;
                }
            }

            FlushBatch();
            RebuildProjectCollections();
            CaptureView.Refresh();
            UpdateVisibleCaptureState();
            SelectedCapture = CaptureView.Cast<CaptureListItemViewModel>().FirstOrDefault();
            StatusText = skipped > 0
                ? $"Loaded {Captures.Count} capture(s), skipped {skipped} invalid item(s)."
                : $"Loaded {Captures.Count} capture(s).";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan canceled.";
            AppLogService.Warn("Capture.Scan.Canceled", "Capture scan canceled.");
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
            AppLogService.Error("Capture.Scan.Failed", "Capture scan failed.", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void AddCapture(CaptureRecord record)
    {
        var item = new CaptureListItemViewModel(record);
        Captures.Insert(0, item);
        AddProject(item.Project);
        AddUsers(item.AssignedUsers);
        RebuildProjectCollections();
        RefreshFilter();

        if (MatchesAppliedFilter(item))
        {
            SelectedCapture = item;
            StatusText = $"Capture saved: {record.LocalTimestamp:HH:mm:ss.fff}";
        }
        else
        {
            StatusText = "Capture saved, but hidden by current filters. Use Search to clear filters.";
        }
    }

    public void SetStatus(string status)
    {
        StatusText = status;
    }

    public void SetPreviewLoading(bool isLoading)
    {
        IsPreviewLoading = isLoading;
    }

    public void ToggleNotesVisibility()
    {
        IsNotesVisible = !IsNotesVisible;
    }

    public void SetSessionState(bool isActive, DateTime? startedAtLocal, int captureCount)
    {
        IsSessionActive = isActive;
        SessionSummary = isActive
            ? $"Session active since {startedAtLocal:HH:mm} | {captureCount} capture(s)"
            : "No active review session.";
    }

    public void SetVideoState(bool isRecording, DateTime? startedAtLocal)
    {
        IsVideoRecording = isRecording;
        VideoSummary = isRecording
            ? $"Video recording since {startedAtLocal:HH:mm:ss}"
            : "No active video segment.";
    }

    public void SetUnviewedSessionsCount(int count)
    {
        UnviewedSessionsCount = count;
    }

    public void RemoveCaptures(IEnumerable<CaptureRecord> capturesToRemove)
    {
        if (capturesToRemove is null)
        {
            return;
        }

        var records = capturesToRemove.ToArray();
        if (records.Length == 0)
        {
            return;
        }

        var recordSet = new HashSet<CaptureRecord>(records);
        var removed = 0;
        for (var i = Captures.Count - 1; i >= 0; i--)
        {
            var item = Captures[i];
            if (recordSet.Contains(item.Record) ||
                records.Any(record => string.Equals(record.Metadata.CaptureDirectory, item.CaptureDirectory, StringComparison.OrdinalIgnoreCase)))
            {
                Captures.RemoveAt(i);
                removed++;
            }
        }

        if (removed > 0)
        {
            RebuildProjectCollections();
            RefreshFilter();
            if (SelectedCapture is null || !CaptureView.Cast<CaptureListItemViewModel>().Contains(SelectedCapture))
            {
                SelectedCapture = CaptureView.Cast<CaptureListItemViewModel>().FirstOrDefault();
            }
        }
    }

    public void ResetPreviewScale()
    {
        PreviewScale = 1.0;
    }

    public async Task<bool> PersistSelectedCaptureMetadataAsync()
    {
        if (SelectedCapture is null)
        {
            return false;
        }

        try
        {
            await PersistMetadataAsync(SelectedCapture);
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Note save failed: {ex.Message}";
            AppLogService.Error("Capture.Note.SaveFailed", "Failed to save capture metadata.", ex, new
            {
                SelectedCapture?.Record.Metadata.JsonPath
            });
            return false;
        }
    }

    private bool FilterCapture(object obj)
    {
        if (obj is not CaptureListItemViewModel capture)
        {
            return false;
        }

        return MatchesAppliedFilter(capture);
    }

    private bool MatchesAppliedFilter(CaptureListItemViewModel capture)
    {
        if (_appliedStartDate.HasValue && capture.LocalTimestamp.Date < _appliedStartDate.Value.Date)
        {
            return false;
        }

        if (_appliedEndDate.HasValue && capture.LocalTimestamp.Date > _appliedEndDate.Value.Date)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_appliedSearchText) &&
            !capture.SearchBlob.Contains(_appliedSearchText, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_appliedUserText))
        {
            var lookup = _appliedUserText.Trim();
            if (!capture.AssignedUsers.Any(user => user.Contains(lookup, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        if (!MatchesProjectChoice(capture, _appliedProjectChoice))
        {
            return false;
        }

        if (!MatchesUserChoice(capture, _appliedUserChoice))
        {
            return false;
        }

        if (!MatchesProjectChoice(capture, _latestProjectFilterChoice))
        {
            return false;
        }

        return MatchesUserChoice(capture, _latestUserFilterChoice);
    }

    private void ApplyFilters()
    {
        if (!TryParseOptionalDate(FilterStartText, out var parsedStart, out var error))
        {
            StatusText = error;
            return;
        }

        if (!TryParseOptionalDate(FilterEndText, out var parsedEnd, out error))
        {
            StatusText = error;
            return;
        }

        if (parsedStart.HasValue && parsedEnd.HasValue && parsedStart.Value.Date > parsedEnd.Value.Date)
        {
            StatusText = "Invalid range: Start date must be on or before End date.";
            return;
        }

        _appliedStartDate = parsedStart?.Date;
        _appliedEndDate = parsedEnd?.Date;
        _appliedSearchText = FilterSearchText.Trim();
        _appliedProjectChoice = NormalizeProjectFilterChoice(FilterProjectChoice);
        _appliedUserChoice = NormalizeUserFilterChoice(FilterUserChoice);
        _appliedUserText = FilterUserText.Trim();

        RefreshFilter();
        OnPropertyChanged(nameof(ActiveFilterSummary));
        StatusText = $"Filter applied. Showing {CaptureView.Cast<object>().Count()} capture(s).";
    }

    private void ClearFilters()
    {
        FilterSearchText = string.Empty;
        FilterStartText = string.Empty;
        FilterEndText = string.Empty;
        FilterProjectChoice = AllProjectsLabel;
        FilterUserChoice = AllUsersLabel;
        FilterUserText = string.Empty;

        _appliedSearchText = string.Empty;
        _appliedStartDate = null;
        _appliedEndDate = null;
        _appliedProjectChoice = AllProjectsLabel;
        _appliedUserChoice = AllUsersLabel;
        _appliedUserText = string.Empty;

        RefreshFilter();
        OnPropertyChanged(nameof(ActiveFilterSummary));
        StatusText = "Filters cleared.";
    }

    private void ApplyQuickRange(string? parameter)
    {
        var today = DateTime.Today;
        switch ((parameter ?? string.Empty).ToLowerInvariant())
        {
            case "today":
                FilterStartText = today.ToString("yyyy-MM-dd");
                FilterEndText = today.ToString("yyyy-MM-dd");
                break;
            case "7d":
                FilterStartText = today.AddDays(-6).ToString("yyyy-MM-dd");
                FilterEndText = today.ToString("yyyy-MM-dd");
                break;
            case "30d":
                FilterStartText = today.AddDays(-29).ToString("yyyy-MM-dd");
                FilterEndText = today.ToString("yyyy-MM-dd");
                break;
            case "all":
            default:
                FilterStartText = string.Empty;
                FilterEndText = string.Empty;
                break;
        }

        ApplyFilters();
    }

    private static bool TryParseOptionalDate(string input, out DateTime? value, out string error)
    {
        value = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        var trimmed = input.Trim();
        if (DateTime.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exactDate))
        {
            value = exactDate.Date;
            return true;
        }

        if (DateTime.TryParse(trimmed, out var parsedDate))
        {
            value = parsedDate.Date;
            return true;
        }

        error = $"Invalid date '{trimmed}'. Use yyyy-MM-dd.";
        return false;
    }

    private void DeleteSelectedCapture()
    {
        if (SelectedCapture is null)
        {
            return;
        }

        DeleteCapture(SelectedCapture);
    }

    private void DeleteCapture(CaptureListItemViewModel? toDelete)
    {
        if (toDelete is null)
        {
            return;
        }

        var confirmation = System.Windows.MessageBox.Show(
            "Are you sure you want to delete this capture?",
            "Delete Capture",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(toDelete.CaptureDirectory) && Directory.Exists(toDelete.CaptureDirectory))
            {
                _ = DataBackupService.TryBackupDirectory(toDelete.CaptureDirectory, "captures", Path.GetFileName(toDelete.CaptureDirectory));
            }

            if (!string.IsNullOrWhiteSpace(toDelete.CaptureDirectory) && Directory.Exists(toDelete.CaptureDirectory))
            {
                Directory.Delete(toDelete.CaptureDirectory, recursive: true);
            }
            else
            {
                if (File.Exists(toDelete.MediaPath))
                {
                    File.Delete(toDelete.MediaPath);
                }

                if (File.Exists(toDelete.Record.Metadata.JsonPath))
                {
                    File.Delete(toDelete.Record.Metadata.JsonPath);
                }
            }

            Captures.Remove(toDelete);
            RebuildProjectCollections();
            RefreshFilter();
            if (ReferenceEquals(SelectedCapture, toDelete) || SelectedCapture is null)
            {
                SelectedCapture = CaptureView.Cast<CaptureListItemViewModel>().FirstOrDefault();
            }

            StatusText = "Capture deleted.";
            AppLogService.Info("Capture.Delete", "Capture deleted from latest captures.", new
            {
                toDelete.Record.Metadata.Project,
                toDelete.Record.Metadata.CaptureDirectory,
                toDelete.Record.Metadata.JsonPath
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Delete failed: {ex.Message}";
            AppLogService.Error("Capture.Delete.Failed", "Capture delete failed.", ex, new
            {
                toDelete.Record.Metadata.Project,
                toDelete.Record.Metadata.CaptureDirectory,
                toDelete.Record.Metadata.JsonPath
            });
        }
    }

    private void PurgeRecentCaptures()
    {
        if (Captures.Count == 0)
        {
            return;
        }

        var confirmation = System.Windows.MessageBox.Show(
            $"Purge all {Captures.Count} recent capture(s)? This does not affect saved Sessions.",
            "Purge Recent Captures",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        var snapshot = Captures.ToList();
        var deleted = 0;
        var failed = 0;
        foreach (var item in snapshot)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(item.CaptureDirectory) && Directory.Exists(item.CaptureDirectory))
                {
                    _ = DataBackupService.TryBackupDirectory(item.CaptureDirectory, "captures", Path.GetFileName(item.CaptureDirectory));
                    Directory.Delete(item.CaptureDirectory, recursive: true);
                }
                else
                {
                    if (File.Exists(item.MediaPath))
                    {
                        File.Delete(item.MediaPath);
                    }

                    if (File.Exists(item.Record.Metadata.JsonPath))
                    {
                        File.Delete(item.Record.Metadata.JsonPath);
                    }
                }

                Captures.Remove(item);
                deleted++;
            }
            catch
            {
                failed++;
            }
        }

        RebuildProjectCollections();
        RefreshFilter();
        SelectedCapture = CaptureView.Cast<CaptureListItemViewModel>().FirstOrDefault();
        StatusText = failed == 0
            ? $"Purged {deleted} recent capture(s)."
            : $"Purged {deleted} recent capture(s), {failed} failed.";
        AppLogService.Info("Capture.PurgeRecent", "Purged recent captures.", new
        {
            Deleted = deleted,
            Failed = failed
        });
    }

    private async Task AssignSelectedCaptureToProjectAsync()
    {
        if (SelectedCapture is null)
        {
            return;
        }

        var targetProject = ToProjectName(SelectedProjectChoice);
        var currentProject = CaptureService.NormalizeProject(SelectedCapture.Project);

        if (string.Equals(currentProject, targetProject, StringComparison.OrdinalIgnoreCase))
        {
            await PersistMetadataAsync(SelectedCapture);
            StatusText = "Capture metadata saved.";
            return;
        }

        try
        {
            var storageRoot = _storageRootProvider();
            var sourceDirectory = SelectedCapture.CaptureDirectory;

            if (!string.IsNullOrWhiteSpace(sourceDirectory) && Directory.Exists(sourceDirectory))
            {
                var timestamp = SelectedCapture.LocalTimestamp;
                var targetRoot = CaptureService.BuildProjectRoot(storageRoot, targetProject);
                var targetDirectory = BuildUniqueTargetDirectory(Path.Combine(
                    targetRoot,
                    timestamp.ToString("yyyy"),
                    timestamp.ToString("MM"),
                    timestamp.ToString("dd"),
                    timestamp.ToString("HHmmss_fff")));

                var targetParent = Path.GetDirectoryName(targetDirectory);
                if (!string.IsNullOrWhiteSpace(targetParent))
                {
                    Directory.CreateDirectory(targetParent);
                }

                Directory.Move(sourceDirectory, targetDirectory);

                var newImagePath = Path.Combine(targetDirectory, "capture.png");
                var newJsonPath = Path.Combine(targetDirectory, "capture.json");
                SelectedCapture.UpdatePaths(targetDirectory, newImagePath, newJsonPath);
            }

            SelectedCapture.Project = targetProject;
            AddProject(targetProject);
            RebuildProjectCollections();
            await PersistMetadataAsync(SelectedCapture);
            RefreshFilter();
            EnsureCaptureVisibleAndSelected(SelectedCapture);
            StatusText = string.IsNullOrWhiteSpace(targetProject)
                ? "Capture moved to no project."
                : $"Capture assigned to project '{targetProject}'.";
        }
        catch (Exception ex)
        {
            StatusText = $"Assign project failed: {ex.Message}";
            AppLogService.Error("Capture.AssignProject.Failed", "Assign project failed.", ex, new
            {
                SelectedCapture?.Record.Metadata.JsonPath,
                TargetProject = targetProject
            });
            if (SelectedCapture is not null)
            {
                SelectedProjectChoice = ToProjectChoice(SelectedCapture.Project);
            }
        }
    }

    private async Task AssignSelectedCaptureToUserAsync()
    {
        if (SelectedCapture is null)
        {
            return;
        }

        var targetUser = ToUserName(SelectedUserChoice);
        var existingUsers = SelectedCapture.AssignedUsers.ToList();
        if (string.IsNullOrWhiteSpace(targetUser) && existingUsers.Count == 0)
        {
            await PersistMetadataAsync(SelectedCapture);
            StatusText = "Capture metadata saved.";
            return;
        }

        if (string.IsNullOrWhiteSpace(targetUser))
        {
            SelectedCapture.UserTagsText = string.Empty;
        }
        else if (!existingUsers.Contains(targetUser, StringComparer.OrdinalIgnoreCase))
        {
            existingUsers.Add(targetUser);
            SelectedCapture.UserTagsText = string.Join(", ", CaptureService.NormalizeUsers(existingUsers));
        }

        AddUsers(SelectedCapture.AssignedUsers);
        RebuildProjectCollections();
        await PersistMetadataAsync(SelectedCapture);
        RefreshFilter();
        EnsureCaptureVisibleAndSelected(SelectedCapture);
        StatusText = string.IsNullOrWhiteSpace(targetUser)
            ? "Capture users cleared."
            : $"Capture tagged with '{targetUser}'.";
    }

    private async Task PersistMetadataAsync(CaptureListItemViewModel capture)
    {
        if (capture is null)
        {
            return;
        }

        capture.Record.Metadata.MetadataVersion = CaptureMetadata.CurrentMetadataVersion;
        capture.Record.Metadata.ReviewState = ReviewState.Normalize(capture.Record.Metadata.ReviewState);

        var jsonPath = capture.Record.Metadata.JsonPath;
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(jsonPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await SafeFileWriteService.WriteJsonAtomicAsync(jsonPath, capture.Record.Metadata, MetadataJsonOptions);
        await PersistLinkedSourceMetadataAsync(capture.Record.Metadata);
        SyncInMemoryLinkedAnnotations(capture);
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
            // Best effort sync for linked source metadata.
        }
    }

    private void SyncInMemoryLinkedAnnotations(CaptureListItemViewModel sourceCapture)
    {
        var sourceJsonPath = sourceCapture.Record.Metadata.JsonPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceJsonPath))
        {
            return;
        }

        foreach (var capture in Captures)
        {
            if (ReferenceEquals(capture, sourceCapture))
            {
                continue;
            }

            var captureJson = capture.Record.Metadata.JsonPath ?? string.Empty;
            var linkedSource = capture.Record.Metadata.SourceJsonPath ?? string.Empty;
            if (!string.Equals(captureJson, sourceCapture.Record.Metadata.SourceJsonPath, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(linkedSource, sourceJsonPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            capture.Note = sourceCapture.Note;
            capture.UserTagsText = sourceCapture.UserTagsText;
        }
    }

    private static string BuildUniqueTargetDirectory(string desiredDirectory)
    {
        if (!Directory.Exists(desiredDirectory))
        {
            return desiredDirectory;
        }

        var parent = Path.GetDirectoryName(desiredDirectory) ?? string.Empty;
        var leaf = Path.GetFileName(desiredDirectory);
        var suffix = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(parent, $"{leaf}_{suffix++:00}");
        } while (Directory.Exists(candidate));

        return candidate;
    }

    private void RefreshFilter()
    {
        CaptureView.Refresh();
        UpdateVisibleCaptureState();

        if (SelectedCapture is not null && !MatchesAppliedFilter(SelectedCapture))
        {
            SelectedCapture = CaptureView.Cast<CaptureListItemViewModel>().FirstOrDefault();
        }
    }

    private void UpdateVisibleCaptureState()
    {
        OnPropertyChanged(nameof(HasVisibleCaptures));
        OnPropertyChanged(nameof(HasNoVisibleCaptures));
    }

    private void RaiseCommandCanExecuteChanged()
    {
        (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenInExplorerCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteSelectedCaptureCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PurgeRecentCapturesCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AssignProjectCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AssignUserCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveNoteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StartSessionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveSessionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CancelSessionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StartVideoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StopVideoCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void EnsureCaptureVisibleAndSelected(CaptureListItemViewModel capture)
    {
        if (CaptureView.Cast<CaptureListItemViewModel>().Contains(capture))
        {
            SelectedCapture = capture;
            return;
        }

        var relaxed = false;
        if (!string.Equals(_latestProjectFilterChoice, AllProjectsLabel, StringComparison.Ordinal))
        {
            _latestProjectFilterChoice = AllProjectsLabel;
            OnPropertyChanged(nameof(LatestProjectFilterChoice));
            relaxed = true;
        }

        if (!string.Equals(_latestUserFilterChoice, AllUsersLabel, StringComparison.Ordinal))
        {
            _latestUserFilterChoice = AllUsersLabel;
            OnPropertyChanged(nameof(LatestUserFilterChoice));
            relaxed = true;
        }

        if (!string.Equals(_appliedProjectChoice, AllProjectsLabel, StringComparison.Ordinal))
        {
            _appliedProjectChoice = AllProjectsLabel;
            FilterProjectChoice = AllProjectsLabel;
            relaxed = true;
        }

        if (!string.Equals(_appliedUserChoice, AllUsersLabel, StringComparison.Ordinal))
        {
            _appliedUserChoice = AllUsersLabel;
            FilterUserChoice = AllUsersLabel;
            relaxed = true;
        }

        if (relaxed)
        {
            RefreshFilter();
            OnPropertyChanged(nameof(ActiveFilterSummary));
        }

        if (CaptureView.Cast<CaptureListItemViewModel>().Contains(capture))
        {
            SelectedCapture = capture;
        }
    }

    private void OpenSelectedInExplorer()
    {
        if (SelectedCapture is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{SelectedCapture.MediaPath}\"",
            UseShellExecute = true
        });
    }

    private void AddProjects(IEnumerable<string> projects)
    {
        foreach (var project in projects)
        {
            AddProject(project);
        }
    }

    private void AddUsers(IEnumerable<string> users)
    {
        foreach (var user in users)
        {
            AddUser(user);
        }
    }

    private void AddProject(string? project)
    {
        var normalized = CaptureService.NormalizeProject(project);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        _knownProjects.Add(normalized);
    }

    private void AddUser(string? user)
    {
        var normalized = CaptureService.NormalizeUser(user);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        _knownUsers.Add(normalized);
    }

    private void RebuildProjectCollections()
    {
        var currentSelection = SelectedProjectChoice;
        var currentUserSelection = SelectedUserChoice;
        var currentActiveCaptureChoice = ActiveCaptureProjectChoice;
        var currentActiveUserChoice = ActiveCaptureUserChoice;
        var currentFilter = FilterProjectChoice;
        var currentUserFilter = FilterUserChoice;
        var currentLatestFilter = LatestProjectFilterChoice;
        var currentLatestUserFilter = LatestUserFilterChoice;
        var projectList = _knownProjects.OrderBy(project => project, StringComparer.OrdinalIgnoreCase).ToArray();
        var userList = _knownUsers.OrderBy(user => user, StringComparer.OrdinalIgnoreCase).ToArray();

        ProjectChoices.Clear();
        ProjectChoices.Add(NoProjectLabel);
        UserChoices.Clear();
        UserChoices.Add(NoUserLabel);
        ActiveCaptureProjectChoices.Clear();
        ActiveCaptureProjectChoices.Add(NoProjectLabel);
        ActiveCaptureUserChoices.Clear();
        ActiveCaptureUserChoices.Add(NoUserLabel);
        foreach (var project in projectList)
        {
            ProjectChoices.Add(project);
            ActiveCaptureProjectChoices.Add(project);
        }
        foreach (var user in userList)
        {
            UserChoices.Add(user);
            ActiveCaptureUserChoices.Add(user);
        }

        FilterProjectChoices.Clear();
        FilterProjectChoices.Add(AllProjectsLabel);
        FilterProjectChoices.Add(NoProjectLabel);
        FilterUserChoices.Clear();
        FilterUserChoices.Add(AllUsersLabel);
        FilterUserChoices.Add(NoUserLabel);
        LatestProjectFilterChoices.Clear();
        LatestProjectFilterChoices.Add(AllProjectsLabel);
        LatestProjectFilterChoices.Add(NoProjectLabel);
        LatestUserFilterChoices.Clear();
        LatestUserFilterChoices.Add(AllUsersLabel);
        LatestUserFilterChoices.Add(NoUserLabel);
        foreach (var project in projectList)
        {
            FilterProjectChoices.Add(project);
            LatestProjectFilterChoices.Add(project);
        }
        foreach (var user in userList)
        {
            FilterUserChoices.Add(user);
            LatestUserFilterChoices.Add(user);
        }

        _suppressActiveProjectNotifications = true;
        _suppressActiveUserNotifications = true;
        try
        {
            SelectedProjectChoice = NormalizeProjectSelectionChoice(currentSelection, ToProjectChoice(SelectedCapture?.Project));
            SelectedUserChoice = NormalizeUserSelectionChoice(currentUserSelection, ToUserChoice(SelectedCapture?.AssignedUser));
            ActiveCaptureProjectChoice = NormalizeProjectSelectionChoice(currentActiveCaptureChoice, NoProjectLabel);
            ActiveCaptureUserChoice = NormalizeUserSelectionChoice(currentActiveUserChoice, NoUserLabel);
            FilterProjectChoice = NormalizeProjectSelectionChoice(currentFilter, AllProjectsLabel);
            FilterUserChoice = NormalizeUserSelectionChoice(currentUserFilter, AllUsersLabel);
            LatestProjectFilterChoice = NormalizeProjectSelectionChoice(currentLatestFilter, AllProjectsLabel);
            LatestUserFilterChoice = NormalizeUserSelectionChoice(currentLatestUserFilter, AllUsersLabel);
        }
        finally
        {
            _suppressActiveProjectNotifications = false;
            _suppressActiveUserNotifications = false;
        }

        _appliedProjectChoice = NormalizeProjectFilterChoice(_appliedProjectChoice);
        _appliedUserChoice = NormalizeUserFilterChoice(_appliedUserChoice);
        OnPropertyChanged(nameof(ActiveFilterSummary));
    }

    private static string NormalizeProjectSelectionChoice(string? selected, string fallback)
    {
        var choice = selected ?? fallback;
        if (string.IsNullOrWhiteSpace(choice))
        {
            return fallback;
        }

        return choice;
    }

    private static bool MatchesProjectChoice(CaptureListItemViewModel capture, string projectChoice)
    {
        if (string.Equals(projectChoice, AllProjectsLabel, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(projectChoice, NoProjectLabel, StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(capture.Project);
        }

        return string.Equals(capture.Project, projectChoice, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeProjectFilterChoice(string? choice)
    {
        if (string.IsNullOrWhiteSpace(choice))
        {
            return AllProjectsLabel;
        }

        return choice.Trim();
    }

    private static string ToProjectChoice(string? projectName)
    {
        var normalized = CaptureService.NormalizeProject(projectName);
        return string.IsNullOrWhiteSpace(normalized) ? NoProjectLabel : normalized;
    }

    private static string ToProjectName(string? projectChoice)
    {
        var normalizedChoice = NormalizeProjectFilterChoice(projectChoice);
        if (string.Equals(normalizedChoice, NoProjectLabel, StringComparison.Ordinal) ||
            string.Equals(normalizedChoice, AllProjectsLabel, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return CaptureService.NormalizeProject(normalizedChoice);
    }

    private static string ToLatestFilterChoiceFromActiveProjectChoice(string activeProjectChoice)
    {
        return string.Equals(activeProjectChoice, NoProjectLabel, StringComparison.Ordinal)
            ? NoProjectLabel
            : NormalizeProjectFilterChoice(activeProjectChoice);
    }

    private static bool MatchesUserChoice(CaptureListItemViewModel capture, string userChoice)
    {
        if (string.Equals(userChoice, AllUsersLabel, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(userChoice, NoUserLabel, StringComparison.Ordinal))
        {
            return capture.AssignedUsers.Count == 0;
        }

        return capture.AssignedUsers.Any(user => string.Equals(user, userChoice, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeUserFilterChoice(string? choice)
    {
        if (string.IsNullOrWhiteSpace(choice))
        {
            return AllUsersLabel;
        }

        return choice.Trim();
    }

    private static string ToUserChoice(string? userName)
    {
        var normalized = CaptureService.NormalizeUser(userName);
        return string.IsNullOrWhiteSpace(normalized) ? NoUserLabel : normalized;
    }

    private static string ToUserName(string? userChoice)
    {
        var normalizedChoice = NormalizeUserFilterChoice(userChoice);
        if (string.Equals(normalizedChoice, NoUserLabel, StringComparison.Ordinal) ||
            string.Equals(normalizedChoice, AllUsersLabel, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return CaptureService.NormalizeUser(normalizedChoice);
    }

    private static string ToLatestUserFilterChoiceFromActiveUserChoice(string activeUserChoice)
    {
        return string.Equals(activeUserChoice, NoUserLabel, StringComparison.Ordinal)
            ? NoUserLabel
            : NormalizeUserFilterChoice(activeUserChoice);
    }

    private static string NormalizeUserSelectionChoice(string? selected, string fallback)
    {
        var choice = selected ?? fallback;
        if (string.IsNullOrWhiteSpace(choice))
        {
            return fallback;
        }

        return choice;
    }
}
