using System.Windows;
using Revie.App.Models;
using Revie.App.Services;
using Revie.App.ViewModels;
using Revie.App.Views;
using Forms = System.Windows.Forms;

namespace Revie.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\Revie.SingleInstance";
    private readonly SettingsService _settingsService = new();
    private readonly StorageService _storageService = new();
    private readonly CaptureIndexService _captureIndexService = new();
    private readonly CaptureService _captureService = new();
    private readonly SessionArchiveService _sessionArchiveService = new();
    private readonly SessionBrowseService _sessionBrowseService = new();
    private readonly SessionNotificationService _sessionNotificationService = new();
    private readonly VideoCaptureService _videoCaptureService = new();

    private AppSettings _settings = AppSettings.CreateDefault();
    private HotkeyService? _hotkeyService;
    private OverlayWindow? _overlayWindow;
    private MainWindow? _mainWindow;
    private SessionBrowserWindow? _sessionBrowserWindow;
    private CaptureToastWindow? _captureToastWindow;
    private MainViewModel? _mainViewModel;
    private Forms.NotifyIcon? _trayIcon;
    private SingleInstanceGate? _singleInstanceGate;
    private bool _isCapturing;
    private DateTime? _videoStartLocal;
    private bool _isSessionActive;
    private DateTime _sessionStartLocal;
    private DateTime _sessionStartUtc;
    private string _activeSessionCaptureBaseRoot = string.Empty;
    private readonly List<CaptureRecord> _sessionCaptures = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;

        _singleInstanceGate = new SingleInstanceGate(SingleInstanceMutexName);
        if (!_singleInstanceGate.IsPrimaryInstance)
        {
            Shutdown();
            return;
        }

        _settings = await _settingsService.LoadAsync();
        NormalizeSettings();
        DataBackupService.Enabled = _settings.EnableDeleteBackups;
        AppLogService.Info("App.Startup", "Revie startup completed settings load.", new
        {
            StorageRoot = _settings.StorageRoot,
            ActiveProject = _settings.ActiveProject,
            BackupEnabled = _settings.EnableDeleteBackups
        });

        if (!await EnsureStorageIsConfiguredAsync())
        {
            Shutdown();
            return;
        }

        var syncedAtStartup = await SyncTaxonomyWithStorageAsync("startup");
        if (syncedAtStartup)
        {
            await _settingsService.SaveAsync(_settings);
        }

        CleanupStaleSessionStagingDirectories();
        CleanupLegacySessionActiveRootIfEmpty();

        InitializeOverlay();
        InitializeMainWindow();
        InitializeHotkeys();
        InitializeTray();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnTaskSchedulerUnobservedTaskException;
        _hotkeyService?.Dispose();
        _singleInstanceGate?.Dispose();
        _singleInstanceGate = null;

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        if (_mainWindow is not null)
        {
            _mainWindow.AllowClose = true;
            _mainWindow.Close();
        }

        if (_sessionBrowserWindow is not null)
        {
            _sessionBrowserWindow.Close();
            _sessionBrowserWindow = null;
        }

        if (_captureToastWindow is not null)
        {
            _captureToastWindow.Close();
            _captureToastWindow = null;
        }

        _videoCaptureService.Abort();
        _overlayWindow?.Close();
        AppLogService.Info("App.Exit", "Revie exited.");

        base.OnExit(e);
    }

    private async Task<bool> EnsureStorageIsConfiguredAsync()
    {
        if (_storageService.ValidateWriteAccess(_settings.StorageRoot, out _))
        {
            return true;
        }

        return await ShowSettingsAsync(isFirstRun: true);
    }

    private void NormalizeSettings()
    {
        _settings.PenSizes ??= new PenSizeSettings();
        _settings.Hotkeys ??= new Dictionary<string, HotkeyDefinition>(StringComparer.OrdinalIgnoreCase);
        _settings.Projects ??= new List<string>();
        _settings.Users ??= new List<string>();
        _settings.Projects = _settings.Projects
            .Where(project => !string.IsNullOrWhiteSpace(project))
            .Select(project => project.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(project => project, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settings.Users = _settings.Users
            .Where(user => !string.IsNullOrWhiteSpace(user))
            .Select(user => user.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(user => user, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settings.ActiveProject = (_settings.ActiveProject ?? string.Empty).Trim();
        _settings.ActiveUser = (_settings.ActiveUser ?? string.Empty).Trim();
        _settings.FfmpegPath = string.IsNullOrWhiteSpace(_settings.FfmpegPath) ? "ffmpeg" : _settings.FfmpegPath.Trim();
        _settings.MicrophoneDevice = string.IsNullOrWhiteSpace(_settings.MicrophoneDevice) ? "default" : _settings.MicrophoneDevice.Trim();
        DataBackupService.Enabled = _settings.EnableDeleteBackups;
        if (!string.IsNullOrWhiteSpace(_settings.ActiveProject) &&
            !_settings.Projects.Contains(_settings.ActiveProject, StringComparer.OrdinalIgnoreCase))
        {
            _settings.Projects.Add(_settings.ActiveProject);
        }
        if (!string.IsNullOrWhiteSpace(_settings.ActiveUser) &&
            !_settings.Users.Contains(_settings.ActiveUser, StringComparer.OrdinalIgnoreCase))
        {
            _settings.Users.Add(_settings.ActiveUser);
        }

        foreach (var (action, hotkey) in AppSettings.CreateDefaultHotkeys())
        {
            if (!_settings.Hotkeys.ContainsKey(action))
            {
                _settings.Hotkeys[action] = hotkey;
            }
        }

        if (!_settings.Hotkeys.ContainsKey(HotkeyActions.StartVideoCapture) &&
            _settings.Hotkeys.TryGetValue(HotkeyActions.ToggleVideoCapture, out var legacyToggle) &&
            legacyToggle is not null)
        {
            _settings.Hotkeys[HotkeyActions.StartVideoCapture] = legacyToggle;
        }

        _settings.Hotkeys.Remove(HotkeyActions.ToggleVideoCapture);
    }

    private void InitializeOverlay()
    {
        _overlayWindow = new OverlayWindow(_settings.PenSizes);
        _overlayWindow.Show();
        _overlayWindow.SetDrawingMode(false);
    }

    private void InitializeMainWindow()
    {
        _mainViewModel = new MainViewModel(_captureIndexService, () => _settings.StorageRoot);
        _mainViewModel.SettingsRequested += async (_, _) => await ShowSettingsAsync(isFirstRun: false);
        _mainViewModel.StartSessionRequested += (_, _) => StartSession();
        _mainViewModel.SaveSessionRequested += async (_, _) => await SaveSessionAsync();
        _mainViewModel.CancelSessionRequested += async (_, _) => await CancelSessionAsync();
        _mainViewModel.OpenSessionsRequested += (_, _) => OpenSessionsWindow();
        _mainViewModel.StartVideoRequested += async (_, _) => await StartVideoCaptureAsync();
        _mainViewModel.StopVideoRequested += async (_, _) => await StopVideoCaptureAsync();
        _mainViewModel.ActiveCaptureProjectChanged += OnActiveCaptureProjectChanged;
        _mainViewModel.ActiveCaptureUserChanged += OnActiveCaptureUserChanged;
        _mainViewModel.ApplyTaxonomySettings(_settings.Projects, _settings.ActiveProject, _settings.Users, _settings.ActiveUser);
        _mainViewModel.SetSessionState(false, null, 0);
        _mainViewModel.SetVideoState(false, null);
        RefreshSessionNotifications();

        _mainWindow = new MainWindow
        {
            DataContext = _mainViewModel
        };
    }

    private void InitializeHotkeys()
    {
        _hotkeyService = new HotkeyService();
        _hotkeyService.HotkeyPressed += HotkeyServiceOnHotkeyPressed;
        ApplyHotkeys();
    }

    private void InitializeTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Revie", null, (_, _) => OpenMainWindow());
        menu.Items.Add("Toggle Drawing", null, (_, _) => ToggleDrawingMode());
        menu.Items.Add("Clear Overlay", null, (_, _) => ClearOverlay());
        menu.Items.Add("Capture Screenshot", null, async (_, _) => await CaptureAsync());
        menu.Items.Add("Start Video Segment", null, async (_, _) => await StartVideoCaptureAsync());
        menu.Items.Add("Stop Video Segment", null, async (_, _) => await StopVideoCaptureAsync());
        menu.Items.Add("Start Review Session", null, (_, _) => StartSession());
        menu.Items.Add("Save Review Session", null, async (_, _) => await SaveSessionAsync());
        menu.Items.Add("Cancel Review Session", null, async (_, _) => await CancelSessionAsync());
        menu.Items.Add("Open Sessions", null, (_, _) => OpenSessionsWindow());
        menu.Items.Add("Settings", null, async (_, _) => await ShowSettingsAsync(isFirstRun: false));
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        var trayIcon = string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? null
            : System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = trayIcon ?? System.Drawing.SystemIcons.Application,
            Text = "Revie",
            Visible = true,
            ContextMenuStrip = menu
        };

        _trayIcon.DoubleClick += (_, _) => OpenMainWindow();
        ShowTrayNotification("Revie is running", "Use Ctrl+Shift+D to toggle drawing mode.");
    }

    private async void HotkeyServiceOnHotkeyPressed(object? sender, string action)
    {
        switch (action)
        {
            case HotkeyActions.ToggleDrawing:
                ToggleDrawingMode();
                break;
            case HotkeyActions.ClearOverlay:
                ClearOverlay();
                break;
            case HotkeyActions.CaptureScreen:
                await CaptureAsync();
                break;
            case HotkeyActions.StartVideoCapture:
                await StartVideoCaptureAsync();
                break;
            case HotkeyActions.StopVideoCapture:
                await StopVideoCaptureAsync();
                break;
            case HotkeyActions.ToggleVideoCapture:
                if (_videoCaptureService.IsRecording)
                {
                    await StopVideoCaptureAsync();
                }
                else
                {
                    await StartVideoCaptureAsync();
                }
                break;
            case HotkeyActions.OpenMainWindow:
                OpenMainWindow();
                break;
        }
    }

    private void ToggleDrawingMode()
    {
        if (_overlayWindow is null)
        {
            return;
        }

        _overlayWindow.ToggleDrawingMode();
        var state = _overlayWindow.IsDrawingMode ? "ON" : "OFF";
        if (!_overlayWindow.IsDrawingMode)
        {
            _overlayWindow.ClearStrokes();
        }

        ShowTrayNotification("Drawing Mode", $"Drawing mode is {state}.");
        _mainViewModel?.SetStatus($"Drawing mode is {state}.");
    }

    private void ClearOverlay()
    {
        _overlayWindow?.ClearStrokes();
    }

    private async Task CaptureAsync()
    {
        if (_overlayWindow is null || _isCapturing)
        {
            return;
        }

        if (!_storageService.ValidateWriteAccess(_settings.StorageRoot, out var storageError))
        {
            ShowTrayNotification("Capture failed", $"Storage root unavailable: {storageError}", Forms.ToolTipIcon.Error);
            _mainViewModel?.SetStatus($"Capture failed: storage root unavailable ({storageError}).");
            await ShowSettingsAsync(isFirstRun: false);
            return;
        }

        _isCapturing = true;

        try
        {
            using var toolbarSuppression = _overlayWindow.SuppressToolbarForCapture();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            var overlayPng = _overlayWindow.RenderOverlayPng();
            var captureBounds = _overlayWindow.GetOverlayBoundsPixels();
            var sessionName = _isSessionActive ? BuildDefaultSessionName(_sessionStartLocal) : string.Empty;
            var sessionCaptureRoot = _isSessionActive ? _activeSessionCaptureBaseRoot : null;
            var activeProject = GetEffectiveActiveProject();
            var record = await _captureService.CaptureAsync(
                _settings.StorageRoot,
                overlayPng,
                captureBounds,
                string.Empty,
                activeProject,
                string.Empty,
                sessionName,
                sessionCaptureRoot);

            if (_settings.ClearOverlayAfterCapture)
            {
                _overlayWindow.ClearStrokes();
            }

            if (_isSessionActive)
            {
                _sessionCaptures.Add(record);
                UpdateSessionState();
                _mainViewModel?.SetStatus($"Session capture saved ({_sessionCaptures.Count} in current session).");
            }
            else
            {
                _mainViewModel?.AddCapture(record);
                _mainViewModel?.SetStatus($"Capture saved | Focused app: {BuildFocusedAppLabel(record.Metadata.ActiveWindowTitle, record.Metadata.ActiveProcessName)}");
            }
            var focusedApp = BuildFocusedAppLabel(record.Metadata.ActiveWindowTitle, record.Metadata.ActiveProcessName);
            AppLogService.Info("Capture.Screenshot.Saved", "Screenshot capture saved.", new
            {
                Project = record.Metadata.Project,
                Directory = record.Metadata.CaptureDirectory,
                Json = record.Metadata.JsonPath
            });
            ShowTrayNotification("Capture saved", focusedApp);
            ShowCaptureToast(focusedApp);
        }
        catch (Exception ex)
        {
            AppLogService.Error("Capture.Screenshot.Failed", "Screenshot capture failed.", ex);
            ShowTrayNotification("Capture failed", ex.Message, Forms.ToolTipIcon.Error);
            _mainViewModel?.SetStatus($"Capture failed: {ex.Message}");
        }
        finally
        {
            _isCapturing = false;
        }
    }

    private void OpenMainWindow()
    {
        if (_mainWindow is null || _mainViewModel is null)
        {
            return;
        }

        _mainWindow.ShowAndActivate();
        _ = _mainViewModel.RefreshAsync();
    }

    private void OpenSessionsWindow()
    {
        if (_sessionBrowserWindow is not null)
        {
            if (_sessionBrowserWindow.IsVisible)
            {
                _sessionBrowserWindow.Activate();
                return;
            }

            _sessionBrowserWindow = null;
        }

        _sessionBrowserWindow = new SessionBrowserWindow(_settings.StorageRoot, _settings.Projects, _settings.Users);
        _sessionBrowserWindow.UnviewedSessionsCountChanged += count => _mainViewModel?.SetUnviewedSessionsCount(count);
        _sessionBrowserWindow.Closed += (_, _) => _sessionBrowserWindow = null;
        _sessionBrowserWindow.Show();
        _sessionBrowserWindow.Activate();
    }

    private void StartSession()
    {
        if (_isSessionActive)
        {
            _mainViewModel?.SetStatus("A review session is already active.");
            return;
        }

        var startLocal = DateTime.Now;
        var startUtc = DateTime.UtcNow;
        try
        {
            _activeSessionCaptureBaseRoot = BuildActiveSessionCaptureBaseRoot(startLocal);
        }
        catch (Exception ex)
        {
            _mainViewModel?.SetStatus($"Could not start session storage: {ex.Message}");
            ShowTrayNotification("Session start failed", ex.Message, Forms.ToolTipIcon.Error);
            AppLogService.Error("Session.Start.Failed", "Failed to initialize active session capture storage.", ex);
            return;
        }

        _isSessionActive = true;
        _sessionStartLocal = startLocal;
        _sessionStartUtc = startUtc;
        _sessionCaptures.Clear();
        UpdateSessionState();

        var message = $"Started at {_sessionStartLocal:HH:mm:ss}. Capture screenshots and click Save Session when done.";
        _mainViewModel?.SetStatus(message);
        ShowTrayNotification("Review session started", message);
    }

    private async Task SaveSessionAsync()
    {
        if (!_isSessionActive)
        {
            _mainViewModel?.SetStatus("No active review session. Click Start Session first.");
            return;
        }

        if (!_storageService.ValidateWriteAccess(_settings.StorageRoot, out var storageError))
        {
            ShowTrayNotification("Save session failed", $"Storage root unavailable: {storageError}", Forms.ToolTipIcon.Error);
            _mainViewModel?.SetStatus($"Save session failed: storage root unavailable ({storageError}).");
            return;
        }

        var suggestedName = BuildDefaultSessionName(_sessionStartLocal);
        var nameDialog = new SessionNameWindow(suggestedName)
        {
            Owner = _mainWindow
        };

        if (nameDialog.ShowDialog() != true)
        {
            _mainViewModel?.SetStatus("Save session canceled.");
            return;
        }

        try
        {
            var snapshot = _sessionCaptures.ToArray();
            var activeProject = GetEffectiveActiveProject();
            var savedFolder = await _sessionArchiveService.SaveSessionAsync(
                _settings.StorageRoot,
                nameDialog.SessionName,
                _sessionStartLocal,
                _sessionStartUtc,
                DateTime.Now,
                DateTime.UtcNow,
                snapshot,
                activeProject);

            CleanupSessionCaptureSources(snapshot);
            CleanupActiveSessionCaptureRoot(_activeSessionCaptureBaseRoot);

            _isSessionActive = false;
            _activeSessionCaptureBaseRoot = string.Empty;
            _sessionCaptures.Clear();
            UpdateSessionState();
            RefreshSessionNotifications();
            CleanupLegacySessionActiveRootIfEmpty();

            _mainViewModel?.SetStatus($"Session saved to {savedFolder}");
            AppLogService.Info("Session.Save", "Session saved.", new
            {
                Folder = savedFolder,
                Project = activeProject,
                CaptureCount = snapshot.Length
            });
            ShowTrayNotification("Session saved", savedFolder);
        }
        catch (Exception ex)
        {
            AppLogService.Error("Session.Save.Failed", "Save session failed.", ex);
            ShowTrayNotification("Save session failed", ex.Message, Forms.ToolTipIcon.Error);
            _mainViewModel?.SetStatus($"Save session failed: {ex.Message}");
        }
    }

    private async Task CancelSessionAsync()
    {
        if (_isCapturing)
        {
            _mainViewModel?.SetStatus("Capture in progress. Try canceling again in a moment.");
            return;
        }

        if (!_isSessionActive)
        {
            _mainViewModel?.SetStatus("No active review session.");
            return;
        }

        var captureCount = _sessionCaptures.Count;
        var confirmation = System.Windows.MessageBox.Show(
            captureCount == 0
                ? "Cancel this review session and start fresh?"
                : $"Cancel this review session and delete its {captureCount} captured image(s)?",
            "Cancel Review Session",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmation != MessageBoxResult.Yes)
        {
            _mainViewModel?.SetStatus("Cancel session aborted.");
            return;
        }

        var discarded = _sessionCaptures.ToArray();
        var deleted = 0;
        var failed = 0;
        await Task.Run(() =>
        {
            foreach (var record in discarded)
            {
                try
                {
                    if (_settings.EnableDeleteBackups)
                    {
                        var backupPath = DataBackupService.TryBackupDirectory(
                            record.Metadata.CaptureDirectory,
                            "captures",
                            Path.GetFileName(record.Metadata.CaptureDirectory));
                        if (!string.IsNullOrWhiteSpace(backupPath))
                        {
                            AppLogService.Info("Capture.Backup", "Capture backup created before delete.", new
                            {
                                Source = record.Metadata.CaptureDirectory,
                                Backup = backupPath
                            });
                        }
                    }

                    DeleteCaptureRecordFiles(record);
                    deleted++;
                }
                catch (Exception ex)
                {
                    failed++;
                    AppLogService.Error("Session.Cancel.DeleteFailed", "Failed deleting capture while canceling session.", ex, new
                    {
                        record.Metadata.CaptureDirectory,
                        record.Metadata.JsonPath
                    });
                }
            }
        });

        _mainViewModel?.RemoveCaptures(discarded);
        CleanupActiveSessionCaptureRoot(_activeSessionCaptureBaseRoot);
        _isSessionActive = false;
        _activeSessionCaptureBaseRoot = string.Empty;
        _sessionCaptures.Clear();
        UpdateSessionState();
        RefreshSessionNotifications();
        CleanupLegacySessionActiveRootIfEmpty();

        if (failed > 0)
        {
            var message = $"Session canceled. Removed {deleted} capture(s), failed to remove {failed}.";
            _mainViewModel?.SetStatus(message);
            ShowTrayNotification("Session canceled", message, Forms.ToolTipIcon.Warning);
            return;
        }

        _mainViewModel?.SetStatus($"Session canceled. Removed {deleted} capture(s).");
        AppLogService.Info("Session.Canceled", "Session canceled and captures removed.", new
        {
            Deleted = deleted
        });
        ShowTrayNotification("Session canceled", "Current review session discarded.");
    }

    private async Task<bool> ShowSettingsAsync(bool isFirstRun)
    {
        var settingsWindow = new SettingsWindow(_settings, isFirstRun, _storageService);
        var result = settingsWindow.ShowDialog();
        if (result != true)
        {
            return false;
        }

        _settings = settingsWindow.ResultSettings;
        NormalizeSettings();
        var taxonomyChanged = await SyncTaxonomyWithStorageAsync("settings");
        await _settingsService.SaveAsync(_settings);
        AppLogService.Info("Settings.Updated", "Settings saved.", new
        {
            StorageRoot = _settings.StorageRoot,
            ActiveProject = _settings.ActiveProject,
            DeleteBackupsEnabled = _settings.EnableDeleteBackups
        });

        _overlayWindow?.ApplyPenSizeSettings(_settings.PenSizes);
        _mainViewModel?.ApplyTaxonomySettings(_settings.Projects, _settings.ActiveProject, _settings.Users, _settings.ActiveUser);
        if (taxonomyChanged)
        {
            AppLogService.Info("Taxonomy.Sync.Applied", "Shared taxonomy changes were applied after settings save.", new
            {
                _settings.StorageRoot,
                Projects = _settings.Projects.Count,
                Users = _settings.Users.Count
            });
        }
        UpdateSessionState();
        RefreshSessionNotifications();
        ApplyHotkeys();
        _ = _mainViewModel?.RefreshAsync();
        return true;
    }

    private async Task StartVideoCaptureAsync()
    {
        if (_videoCaptureService.IsRecording)
        {
            _mainViewModel?.SetStatus("Video recording is already active.");
            return;
        }

        if (!_storageService.ValidateWriteAccess(_settings.StorageRoot, out var storageError))
        {
            ShowTrayNotification("Video failed", $"Storage root unavailable: {storageError}", Forms.ToolTipIcon.Error);
            _mainViewModel?.SetStatus($"Video failed: storage root unavailable ({storageError}).");
            return;
        }

        var sessionName = _isSessionActive ? BuildDefaultSessionName(_sessionStartLocal) : string.Empty;
        var sessionCaptureRoot = _isSessionActive ? _activeSessionCaptureBaseRoot : null;
        var activeProject = GetEffectiveActiveProject();
        var (started, message) = await _videoCaptureService.StartAsync(
            _settings.StorageRoot,
            activeProject,
            string.Empty,
            sessionName,
            _settings.FfmpegPath,
            _settings.MicrophoneDevice,
            sessionCaptureRoot);

        if (!started)
        {
            AppLogService.Warn("Capture.Video.StartFailed", "Video start failed.", new { Message = message });
            ShowTrayNotification("Video failed", message, Forms.ToolTipIcon.Error);
            _mainViewModel?.SetStatus($"Video capture failed: {message}");
            ShowCaptureToast($"Video failed: {message}");
            return;
        }

        _videoStartLocal = DateTime.Now;
        _mainViewModel?.SetVideoState(true, _videoStartLocal);
        _mainViewModel?.SetStatus(message);
        AppLogService.Info("Capture.Video.Started", "Video recording started.", new
        {
            Project = activeProject,
            Session = sessionName
        });
        ShowTrayNotification("Video recording started", "Use your Stop Video hotkey or Stop Video button to finish.");
        ShowCaptureToast("Video recording started");
    }

    private async Task StopVideoCaptureAsync()
    {
        if (!_videoCaptureService.IsRecording)
        {
            _mainViewModel?.SetStatus("No active video segment.");
            return;
        }

        var record = await _videoCaptureService.StopAsync();
        _mainViewModel?.SetVideoState(false, null);
        _videoStartLocal = null;

        if (record is null)
        {
            AppLogService.Warn("Capture.Video.StopFailed", "Video stop failed because output record was null.");
            _mainViewModel?.SetStatus("Video capture failed: output file was not created.");
            ShowTrayNotification("Video failed", "No output video was created.", Forms.ToolTipIcon.Error);
            ShowCaptureToast("Video capture failed");
            return;
        }

        if (_isSessionActive)
        {
            _sessionCaptures.Add(record);
            UpdateSessionState();
            _mainViewModel?.SetStatus($"Session video saved ({_sessionCaptures.Count} in current session).");
        }
        else
        {
            _mainViewModel?.AddCapture(record);
        }

        var audioState = record.Metadata.HasMicrophoneAudio ? "with mic audio" : "without mic audio";
        if (!_isSessionActive)
        {
            _mainViewModel?.SetStatus($"Video saved | {record.Metadata.DurationSeconds:0.0}s | {audioState}");
        }
        AppLogService.Info("Capture.Video.Saved", "Video capture saved.", new
        {
            Project = record.Metadata.Project,
            DurationSeconds = record.Metadata.DurationSeconds,
            Audio = record.Metadata.HasMicrophoneAudio,
            Directory = record.Metadata.CaptureDirectory
        });
        ShowTrayNotification("Video saved", $"{record.Metadata.DurationSeconds:0.0}s segment ({audioState})");
        ShowCaptureToast($"Video saved ({record.Metadata.DurationSeconds:0.0}s, {audioState})");
    }

    private async void OnActiveCaptureProjectChanged(string projectName)
    {
        var normalizedProject = CaptureService.NormalizeProject(projectName);
        if (string.Equals(_settings.ActiveProject, normalizedProject, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.ActiveProject = normalizedProject;
        if (!string.IsNullOrWhiteSpace(normalizedProject) &&
            !_settings.Projects.Contains(normalizedProject, StringComparer.OrdinalIgnoreCase))
        {
            _settings.Projects.Add(normalizedProject);
            _settings.Projects = _settings.Projects
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(project => project, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        try
        {
            var taxonomyChanged = await SyncTaxonomyWithStorageAsync("active-project-change");
            await _settingsService.SaveAsync(_settings);
            if (taxonomyChanged)
            {
                _mainViewModel?.ApplyTaxonomySettings(_settings.Projects, _settings.ActiveProject, _settings.Users, _settings.ActiveUser);
            }
            _mainViewModel?.SetStatus(string.IsNullOrWhiteSpace(normalizedProject)
                ? "Active capture project set to none."
                : $"Active capture project set to '{normalizedProject}'.");
            AppLogService.Info("Project.Active.Changed", "Active capture project changed.", new
            {
                ActiveProject = normalizedProject
            });
        }
        catch (Exception ex)
        {
            _mainViewModel?.SetStatus($"Failed to save active project: {ex.Message}");
            AppLogService.Error("Project.Active.ChangeFailed", "Failed saving active project.", ex, new
            {
                ActiveProject = normalizedProject
            });
        }
    }

    private async void OnActiveCaptureUserChanged(string userName)
    {
        var normalizedUser = CaptureService.NormalizeUser(userName);
        if (string.Equals(_settings.ActiveUser, normalizedUser, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.ActiveUser = normalizedUser;
        if (!string.IsNullOrWhiteSpace(normalizedUser) &&
            !_settings.Users.Contains(normalizedUser, StringComparer.OrdinalIgnoreCase))
        {
            _settings.Users.Add(normalizedUser);
            _settings.Users = _settings.Users
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(user => user, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        try
        {
            var taxonomyChanged = await SyncTaxonomyWithStorageAsync("active-user-change");
            await _settingsService.SaveAsync(_settings);
            if (taxonomyChanged)
            {
                _mainViewModel?.ApplyTaxonomySettings(_settings.Projects, _settings.ActiveProject, _settings.Users, _settings.ActiveUser);
            }
            _mainViewModel?.SetStatus(string.IsNullOrWhiteSpace(normalizedUser)
                ? "Active capture user set to none."
                : $"Active capture user set to '{normalizedUser}'.");
            AppLogService.Info("User.Active.Changed", "Active capture user changed.", new
            {
                ActiveUser = normalizedUser
            });
        }
        catch (Exception ex)
        {
            _mainViewModel?.SetStatus($"Failed to save active user: {ex.Message}");
            AppLogService.Error("User.Active.ChangeFailed", "Failed saving active user.", ex, new
            {
                ActiveUser = normalizedUser
            });
        }
    }

    private void UpdateSessionState()
    {
        _mainViewModel?.SetSessionState(_isSessionActive, _isSessionActive ? _sessionStartLocal : null, _sessionCaptures.Count);
    }

    private string BuildActiveSessionCaptureBaseRoot(DateTime startedAtLocal)
    {
        var activeRoot = Path.Combine(
            GetSessionStagingRoot(),
            $"{startedAtLocal:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}",
            "captures");
        Directory.CreateDirectory(activeRoot);
        return activeRoot;
    }

    private static void CleanupSessionCaptureSources(IEnumerable<CaptureRecord> records)
    {
        var paths = (records ?? Array.Empty<CaptureRecord>())
            .Select(record => record.Metadata.CaptureDirectory ?? string.Empty)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => path.Length)
            .ToArray();

        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup for session staging captures.
            }
        }
    }

    private static void CleanupActiveSessionCaptureRoot(string captureBaseRoot)
    {
        if (string.IsNullOrWhiteSpace(captureBaseRoot))
        {
            return;
        }

        try
        {
            var capturesRoot = captureBaseRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var activeSessionRoot = Path.GetDirectoryName(capturesRoot);
            if (!string.IsNullOrWhiteSpace(activeSessionRoot) && Directory.Exists(activeSessionRoot))
            {
                Directory.Delete(activeSessionRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup for active session staging root.
        }
    }

    private void CleanupLegacySessionActiveRootIfEmpty()
    {
        if (string.IsNullOrWhiteSpace(_settings.StorageRoot))
        {
            return;
        }

        try
        {
            var legacyRoot = Path.Combine(_settings.StorageRoot, "Sessions", ".active");
            TryDeleteDirectoryTreeIfEmpty(legacyRoot);
        }
        catch
        {
            // Best effort cleanup for legacy storage-root session staging.
        }
    }

    private static void CleanupStaleSessionStagingDirectories()
    {
        try
        {
            var stagingRoot = GetSessionStagingRoot();
            if (!Directory.Exists(stagingRoot))
            {
                return;
            }

            var staleThresholdUtc = DateTime.UtcNow.AddDays(-2);
            foreach (var directory in Directory.EnumerateDirectories(stagingRoot))
            {
                try
                {
                    var lastWriteUtc = Directory.GetLastWriteTimeUtc(directory);
                    if (lastWriteUtc < staleThresholdUtc)
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch
                {
                    // Best effort stale cleanup only.
                }
            }

            TryDeleteDirectoryTreeIfEmpty(stagingRoot);
        }
        catch
        {
            // Best effort stale cleanup only.
        }
    }

    private static string GetSessionStagingRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Revie",
            "session-staging");
    }

    private static bool TryDeleteDirectoryTreeIfEmpty(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return true;
        }

        try
        {
            foreach (var childDirectory in Directory.EnumerateDirectories(directoryPath))
            {
                if (!TryDeleteDirectoryTreeIfEmpty(childDirectory))
                {
                    return false;
                }
            }

            if (Directory.EnumerateFiles(directoryPath).Any())
            {
                return false;
            }

            if (!Directory.EnumerateDirectories(directoryPath).Any())
            {
                Directory.Delete(directoryPath, recursive: false);
            }

            return !Directory.Exists(directoryPath);
        }
        catch
        {
            return false;
        }
    }

    private void RefreshSessionNotifications()
    {
        if (_mainViewModel is null || string.IsNullOrWhiteSpace(_settings.StorageRoot))
        {
            return;
        }

        try
        {
            var sessions = _sessionBrowseService.GetSessions(_settings.StorageRoot);
            var state = _sessionNotificationService.Load();
            var count = _sessionNotificationService.CountSessionsWithUnviewedCaptures(state, sessions);
            _mainViewModel.SetUnviewedSessionsCount(count);
        }
        catch
        {
            _mainViewModel.SetUnviewedSessionsCount(0);
        }
    }

    private static string BuildDefaultSessionName(DateTime startLocal)
    {
        return $"Review_{startLocal:yyyyMMdd_HHmm}";
    }

    private static void DeleteCaptureRecordFiles(CaptureRecord record)
    {
        var captureDirectory = record.Metadata.CaptureDirectory ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(captureDirectory) && Directory.Exists(captureDirectory))
        {
            Directory.Delete(captureDirectory, recursive: true);
            return;
        }

        var imagePath = record.Metadata.ImagePath ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            File.Delete(imagePath);
        }

        var jsonPath = record.Metadata.JsonPath ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(jsonPath) && File.Exists(jsonPath))
        {
            File.Delete(jsonPath);
        }
    }

    private static string BuildFocusedAppLabel(string windowTitle, string processName)
    {
        var normalizedWindow = (windowTitle ?? string.Empty).Trim();
        var normalizedProcess = (processName ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(normalizedWindow) && !string.IsNullOrWhiteSpace(normalizedProcess))
        {
            return $"{normalizedProcess} - {normalizedWindow}";
        }

        if (!string.IsNullOrWhiteSpace(normalizedProcess))
        {
            return normalizedProcess;
        }

        if (!string.IsNullOrWhiteSpace(normalizedWindow))
        {
            return normalizedWindow;
        }

        return "Unknown application";
    }

    private void ApplyHotkeys()
    {
        if (_hotkeyService is null)
        {
            return;
        }

        var errors = _hotkeyService.RegisterHotkeys(_settings.Hotkeys);
        if (errors.Count > 0)
        {
            var message = string.Join(Environment.NewLine, errors);
            ShowTrayNotification("Hotkey registration warning", message, Forms.ToolTipIcon.Warning);
        }
    }

    private void ShowTrayNotification(string title, string message, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info)
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.BalloonTipTitle = title;
        _trayIcon.BalloonTipText = message;
        _trayIcon.BalloonTipIcon = icon;
        _trayIcon.ShowBalloonTip(3000);
    }

    private void ShowCaptureToast(string detailText)
    {
        if (_captureToastWindow is not null)
        {
            _captureToastWindow.Close();
            _captureToastWindow = null;
        }

        _captureToastWindow = new CaptureToastWindow(detailText);
        _captureToastWindow.Closed += (_, _) =>
        {
            _captureToastWindow = null;
        };
        _captureToastWindow.Show();
    }

    private string GetEffectiveActiveProject()
    {
        if (_mainViewModel is not null)
        {
            return CaptureService.NormalizeProject(_mainViewModel.ActiveCaptureProjectName);
        }

        return CaptureService.NormalizeProject(_settings.ActiveProject);
    }

    private async Task<bool> SyncTaxonomyWithStorageAsync(string source)
    {
        if (string.IsNullOrWhiteSpace(_settings.StorageRoot))
        {
            return false;
        }

        if (!_storageService.ValidateWriteAccess(_settings.StorageRoot, out var storageError))
        {
            AppLogService.Warn("Taxonomy.Sync.Skipped", "Skipped shared taxonomy sync because storage root was not writable.", new
            {
                Source = source,
                _settings.StorageRoot,
                StorageError = storageError
            });
            return false;
        }

        var result = await _settingsService.SyncSharedTaxonomyAsync(
            _settings.StorageRoot,
            _settings.Projects,
            _settings.Users);
        var projectsChanged = !_settings.Projects.SequenceEqual(result.Projects, StringComparer.OrdinalIgnoreCase);
        var usersChanged = !_settings.Users.SequenceEqual(result.Users, StringComparer.OrdinalIgnoreCase);
        if (projectsChanged || usersChanged)
        {
            _settings.Projects = result.Projects.ToList();
            _settings.Users = result.Users.ToList();
            NormalizeSettings();
        }

        if (!string.IsNullOrWhiteSpace(result.Warning))
        {
            AppLogService.Warn("Taxonomy.Sync.Warning", "Shared taxonomy sync completed with warning.", new
            {
                Source = source,
                Warning = result.Warning,
                SharedPath = SettingsService.GetSharedTaxonomyPath(_settings.StorageRoot)
            });
        }
        else
        {
            AppLogService.Info("Taxonomy.Sync", "Shared taxonomy sync completed.", new
            {
                Source = source,
                SharedPath = SettingsService.GetSharedTaxonomyPath(_settings.StorageRoot),
                Projects = result.Projects.Count,
                Users = result.Users.Count,
                result.LoadedFromShared,
                result.SavedToShared
            });
        }

        return projectsChanged || usersChanged;
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogService.Error("Unhandled.Dispatcher", "Unhandled dispatcher exception.", e.Exception);
    }

    private void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        AppLogService.Error("Unhandled.AppDomain", "Unhandled app-domain exception.", ex, new
        {
            e.IsTerminating
        });
    }

    private void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogService.Error("Unhandled.Task", "Unobserved task exception.", e.Exception);
        e.SetObserved();
    }
}
