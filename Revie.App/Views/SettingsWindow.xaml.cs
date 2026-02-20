using System.Windows;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;
using Revie.App.Models;
using Revie.App.Services;
using Forms = System.Windows.Forms;

namespace Revie.App.Views;

public partial class SettingsWindow : Window
{
    private const string NoActiveProjectLabel = "(No active project)";
    private const string NoActiveUserLabel = "(No active user)";
    private const string DefaultMicrophoneLabel = "(Default microphone)";
    private const string DisabledMicrophoneLabel = "(No microphone)";

    private readonly bool _isFirstRun;
    private readonly StorageService _storageService;
    private readonly ObservableCollection<string> _projects = new();
    private readonly ObservableCollection<string> _users = new();
    private readonly ObservableCollection<string> _microphoneOptions = new();

    public SettingsWindow(AppSettings existingSettings, bool isFirstRun, StorageService storageService)
    {
        InitializeComponent();
        _isFirstRun = isFirstRun;
        _storageService = storageService;
        ResultSettings = existingSettings.Clone();
        VersionTextBlock.Text = GetVersionDisplayText();

        StorageRootTextBox.Text = ResultSettings.StorageRoot;
        ClearOverlayAfterCaptureCheckBox.IsChecked = ResultSettings.ClearOverlayAfterCapture;

        PenSizeSmallTextBox.Text = ResultSettings.PenSizes.Small.ToString("0.##");
        PenSizeMediumTextBox.Text = ResultSettings.PenSizes.Medium.ToString("0.##");
        PenSizeLargeTextBox.Text = ResultSettings.PenSizes.Large.ToString("0.##");
        FfmpegPathTextBox.Text = string.IsNullOrWhiteSpace(ResultSettings.FfmpegPath) ? "ffmpeg" : ResultSettings.FfmpegPath;
        InitializeMicrophoneSelection(ResultSettings.MicrophoneDevice);

        ToggleDrawingHotkeyTextBox.Text = GetHotkeyDisplay(HotkeyActions.ToggleDrawing);
        ClearOverlayHotkeyTextBox.Text = GetHotkeyDisplay(HotkeyActions.ClearOverlay);
        CaptureHotkeyTextBox.Text = GetHotkeyDisplay(HotkeyActions.CaptureScreen);
        OpenMainHotkeyTextBox.Text = GetHotkeyDisplay(HotkeyActions.OpenMainWindow);
        StartVideoHotkeyTextBox.Text = GetHotkeyDisplay(HotkeyActions.StartVideoCapture, HotkeyActions.ToggleVideoCapture);
        StopVideoHotkeyTextBox.Text = GetHotkeyDisplay(HotkeyActions.StopVideoCapture);
        foreach (var project in ResultSettings.Projects
                     .Where(project => !string.IsNullOrWhiteSpace(project))
                     .Select(project => project.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(project => project, StringComparer.OrdinalIgnoreCase))
        {
            _projects.Add(project);
        }

        ProjectsListBox.ItemsSource = _projects;
        RefreshProjectSelectors(ResultSettings.ActiveProject);
        foreach (var user in ResultSettings.Users
                     .Where(user => !string.IsNullOrWhiteSpace(user))
                     .Select(user => user.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(user => user, StringComparer.OrdinalIgnoreCase))
        {
            _users.Add(user);
        }

        UsersListBox.ItemsSource = _users;
        RefreshUserSelectors(ResultSettings.ActiveUser);
        Loaded += async (_, _) => await RefreshMicrophonesAsync(false);

        if (_isFirstRun)
        {
            Title = "Revie First Run Setup";
        }
    }

    public AppSettings ResultSettings { get; private set; }

    private void BrowseStorageButton_OnClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select Revie storage root",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            StorageRootTextBox.Text = dialog.SelectedPath;
        }
    }

    private void BrowseFfmpegButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select ffmpeg executable",
            Filter = "FFmpeg executable|ffmpeg.exe|Executable files|*.exe|All files|*.*",
            CheckFileExists = true,
            CheckPathExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        FfmpegPathTextBox.Text = dialog.FileName;
        _ = RefreshMicrophonesAsync(false);
    }

    private void AutoDetectFfmpegButton_OnClick(object sender, RoutedEventArgs e)
    {
        var candidates = FindFfmpegCandidates().ToList();
        if (candidates.Count == 0)
        {
            ErrorTextBlock.Text = "FFmpeg was not auto-detected. Revie will still use bundled ffmpeg if available.";
            return;
        }

        FfmpegPathTextBox.Text = candidates[0];
        ErrorTextBlock.Text = $"FFmpeg detected: {candidates[0]}";
        _ = RefreshMicrophonesAsync(false);
    }

    private async void RefreshMicrophonesButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshMicrophonesAsync(true);
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        ErrorTextBlock.Text = string.Empty;

        if (!_storageService.ValidateWriteAccess(StorageRootTextBox.Text.Trim(), out var storageError))
        {
            ErrorTextBlock.Text = $"Storage root is not writable: {storageError}";
            return;
        }

        if (!TryBuildHotkey(ToggleDrawingHotkeyTextBox.Text, HotkeyActions.ToggleDrawing, out var toggleDrawing, out var hotkeyError) ||
            !TryBuildHotkey(ClearOverlayHotkeyTextBox.Text, HotkeyActions.ClearOverlay, out var clearOverlay, out hotkeyError) ||
            !TryBuildHotkey(CaptureHotkeyTextBox.Text, HotkeyActions.CaptureScreen, out var capture, out hotkeyError) ||
            !TryBuildHotkey(OpenMainHotkeyTextBox.Text, HotkeyActions.OpenMainWindow, out var openMain, out hotkeyError) ||
            !TryBuildHotkey(StartVideoHotkeyTextBox.Text, HotkeyActions.StartVideoCapture, out var startVideo, out hotkeyError) ||
            !TryBuildHotkey(StopVideoHotkeyTextBox.Text, HotkeyActions.StopVideoCapture, out var stopVideo, out hotkeyError))
        {
            ErrorTextBlock.Text = hotkeyError;
            return;
        }

        if (!double.TryParse(PenSizeSmallTextBox.Text.Trim(), out var small) ||
            !double.TryParse(PenSizeMediumTextBox.Text.Trim(), out var medium) ||
            !double.TryParse(PenSizeLargeTextBox.Text.Trim(), out var large) ||
            small <= 0 || medium <= 0 || large <= 0)
        {
            ErrorTextBlock.Text = "Pen sizes must be positive numbers.";
            return;
        }

        ResultSettings.StorageRoot = StorageRootTextBox.Text.Trim();
        ResultSettings.ClearOverlayAfterCapture = ClearOverlayAfterCaptureCheckBox.IsChecked == true;
        ResultSettings.PenSizes = new PenSizeSettings { Small = small, Medium = medium, Large = large };
        ResultSettings.FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPathTextBox.Text) ? "ffmpeg" : FfmpegPathTextBox.Text.Trim();
        ResultSettings.MicrophoneDevice = ToStoredMicrophoneValue(MicrophoneDeviceComboBox.Text);
        ResultSettings.Projects = _projects
            .Where(project => !string.IsNullOrWhiteSpace(project))
            .Select(project => project.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(project => project, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ResultSettings.Users = _users
            .Where(user => !string.IsNullOrWhiteSpace(user))
            .Select(user => user.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(user => user, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var activeProject = (ActiveProjectComboBox.SelectedItem as string ?? string.Empty).Trim();
        ResultSettings.ActiveProject = string.Equals(activeProject, NoActiveProjectLabel, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : activeProject;
        ResultSettings.ActiveUser = string.Empty;

        if (!string.IsNullOrWhiteSpace(ResultSettings.ActiveProject) &&
            !ResultSettings.Projects.Contains(ResultSettings.ActiveProject, StringComparer.OrdinalIgnoreCase))
        {
            ResultSettings.Projects.Add(ResultSettings.ActiveProject);
        }
        ResultSettings.Hotkeys = new Dictionary<string, HotkeyDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [HotkeyActions.ToggleDrawing] = toggleDrawing!,
            [HotkeyActions.ClearOverlay] = clearOverlay!,
            [HotkeyActions.CaptureScreen] = capture!,
            [HotkeyActions.OpenMainWindow] = openMain!,
            [HotkeyActions.StartVideoCapture] = startVideo!,
            [HotkeyActions.StopVideoCapture] = stopVideo!
        };

        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isFirstRun)
        {
            DialogResult = false;
            return;
        }

        Close();
    }

    private static bool TryBuildHotkey(string text, string actionName, out HotkeyDefinition? definition, out string error)
    {
        if (!HotkeyParser.TryParse(text, out var parsed, out var parseError))
        {
            definition = null;
            error = $"{actionName}: {parseError}";
            return false;
        }

        definition = parsed;
        error = string.Empty;
        return true;
    }

    private string GetHotkeyDisplay(string action, string? legacyFallbackAction = null)
    {
        if (ResultSettings.Hotkeys.TryGetValue(action, out var hotkey) && hotkey is not null)
        {
            return hotkey.ToDisplayString();
        }

        if (!string.IsNullOrWhiteSpace(legacyFallbackAction) &&
            ResultSettings.Hotkeys.TryGetValue(legacyFallbackAction, out var legacyHotkey) &&
            legacyHotkey is not null)
        {
            return legacyHotkey.ToDisplayString();
        }

        return AppSettings.CreateDefaultHotkeys()[action].ToDisplayString();
    }

    private void AddProjectButton_OnClick(object sender, RoutedEventArgs e)
    {
        ErrorTextBlock.Text = string.Empty;

        var projectName = NewProjectTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(projectName))
        {
            ErrorTextBlock.Text = "Project name cannot be empty.";
            return;
        }

        if (_projects.Contains(projectName, StringComparer.OrdinalIgnoreCase))
        {
            ErrorTextBlock.Text = "Project already exists.";
            return;
        }

        _projects.Add(projectName);
        SortProjects();
        RefreshProjectSelectors(projectName);
        NewProjectTextBox.Text = string.Empty;
    }

    private void RemoveProjectButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ProjectsListBox.SelectedItem is not string selectedProject)
        {
            ErrorTextBlock.Text = "Select a project to remove.";
            return;
        }

        _projects.Remove(selectedProject);
        RefreshProjectSelectors(string.Empty);
    }

    private void AddUserButton_OnClick(object sender, RoutedEventArgs e)
    {
        ErrorTextBlock.Text = string.Empty;

        var userName = NewUserTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(userName))
        {
            ErrorTextBlock.Text = "User name cannot be empty.";
            return;
        }

        if (_users.Contains(userName, StringComparer.OrdinalIgnoreCase))
        {
            ErrorTextBlock.Text = "User already exists.";
            return;
        }

        _users.Add(userName);
        SortUsers();
        RefreshUserSelectors(userName);
        NewUserTextBox.Text = string.Empty;
    }

    private void RemoveUserButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (UsersListBox.SelectedItem is not string selectedUser)
        {
            ErrorTextBlock.Text = "Select a user to remove.";
            return;
        }

        _users.Remove(selectedUser);
        RefreshUserSelectors(string.Empty);
    }

    private void SortProjects()
    {
        var ordered = _projects
            .OrderBy(project => project, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _projects.Clear();
        foreach (var project in ordered)
        {
            _projects.Add(project);
        }
    }

    private void SortUsers()
    {
        var ordered = _users
            .OrderBy(user => user, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _users.Clear();
        foreach (var user in ordered)
        {
            _users.Add(user);
        }
    }

    private void RefreshProjectSelectors(string preferredSelection)
    {
        var options = new List<string> { NoActiveProjectLabel };
        options.AddRange(_projects);
        ActiveProjectComboBox.ItemsSource = options;

        var preferred = preferredSelection.Trim();
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var match = options.FirstOrDefault(option => string.Equals(option, preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                ActiveProjectComboBox.SelectedItem = match;
                return;
            }
        }

        ActiveProjectComboBox.SelectedIndex = 0;
    }

    private void RefreshUserSelectors(string preferredSelection)
    {
        var options = new List<string> { NoActiveUserLabel };
        options.AddRange(_users);
        ActiveUserComboBox.ItemsSource = options;

        var preferred = preferredSelection.Trim();
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var match = options.FirstOrDefault(option => string.Equals(option, preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                ActiveUserComboBox.SelectedItem = match;
                return;
            }
        }

        ActiveUserComboBox.SelectedIndex = 0;
    }

    private void InitializeMicrophoneSelection(string storedValue)
    {
        _microphoneOptions.Clear();
        _microphoneOptions.Add(DefaultMicrophoneLabel);
        _microphoneOptions.Add(DisabledMicrophoneLabel);
        MicrophoneDeviceComboBox.ItemsSource = _microphoneOptions;
        MicrophoneDeviceComboBox.Text = FromStoredMicrophoneValue(storedValue);
    }

    private async Task RefreshMicrophonesAsync(bool reportResult)
    {
        var currentSelection = MicrophoneDeviceComboBox.Text.Trim();
        var result = await VideoCaptureService.EnumerateMicrophoneDevicesAsync(FfmpegPathTextBox.Text.Trim());

        _microphoneOptions.Clear();
        _microphoneOptions.Add(DefaultMicrophoneLabel);
        _microphoneOptions.Add(DisabledMicrophoneLabel);
        foreach (var device in result.devices)
        {
            _microphoneOptions.Add(device);
        }

        if (string.IsNullOrWhiteSpace(currentSelection))
        {
            MicrophoneDeviceComboBox.Text = DefaultMicrophoneLabel;
        }
        else
        {
            MicrophoneDeviceComboBox.Text = currentSelection;
        }

        if (!reportResult)
        {
            return;
        }

        ErrorTextBlock.Text = result.success
            ? result.devices.Count == 0
                ? "No microphone devices were detected. Revie will still try the default input while recording."
                : $"Detected {result.devices.Count} microphone device(s)."
            : $"Microphone scan failed: {result.error}";
    }

    private static string ToStoredMicrophoneValue(string? selection)
    {
        var normalized = (selection ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(normalized, DefaultMicrophoneLabel, StringComparison.OrdinalIgnoreCase))
        {
            return "default";
        }

        if (string.Equals(normalized, DisabledMicrophoneLabel, StringComparison.OrdinalIgnoreCase))
        {
            return "none";
        }

        return normalized;
    }

    private static string FromStoredMicrophoneValue(string? storedValue)
    {
        var normalized = (storedValue ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(normalized, "default", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultMicrophoneLabel;
        }

        if (string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "off", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            return DisabledMicrophoneLabel;
        }

        return normalized;
    }

    private static string GetVersionDisplayText()
    {
        var assembly = typeof(SettingsWindow).Assembly;
        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            return $"Version {infoVersion}";
        }

        var version = assembly.GetName().Version;
        if (version is not null)
        {
            return $"Version {version}";
        }

        return "Version (unknown)";
    }

    private static IEnumerable<string> FindFfmpegCandidates()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(bundledPath))
        {
            candidates.Add(bundledPath);
        }

        try
        {
            using var whereProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "ffmpeg",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            if (whereProcess is not null)
            {
                var output = whereProcess.StandardOutput.ReadToEnd();
                whereProcess.WaitForExit(1500);
                foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (File.Exists(line))
                    {
                        candidates.Add(line);
                    }
                }
            }
        }
        catch
        {
            // best-effort
        }

        foreach (var path in GetCommonFfmpegPaths())
        {
            if (File.Exists(path))
            {
                candidates.Add(path);
            }
        }

        return candidates
            .OrderByDescending(path =>
            {
                try
                {
                    return File.GetLastWriteTimeUtc(path);
                }
                catch
                {
                    return DateTime.MinValue;
                }
            })
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetCommonFfmpegPaths()
    {
        var paths = new List<string>
        {
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe"
        };

        var chocoRoot = Environment.GetEnvironmentVariable("ChocolateyInstall");
        if (!string.IsNullOrWhiteSpace(chocoRoot))
        {
            paths.Add(Path.Combine(chocoRoot, "bin", "ffmpeg.exe"));
        }

        var wingetRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WinGet",
            "Packages");
        if (!Directory.Exists(wingetRoot))
        {
            return paths;
        }

        try
        {
            foreach (var packageDir in Directory.EnumerateDirectories(wingetRoot))
            {
                if (Path.GetFileName(packageDir).IndexOf("ffmpeg", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                foreach (var ffmpegPath in Directory.EnumerateFiles(packageDir, "ffmpeg.exe", SearchOption.AllDirectories))
                {
                    paths.Add(ffmpegPath);
                }
            }
        }
        catch
        {
            // best-effort
        }

        return paths;
    }
}
