using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Revie.App.Models;
using Revie.App.Native;
using Screen = System.Windows.Forms.Screen;

namespace Revie.App.Services;

public sealed class VideoCaptureService
{
    private static readonly string[] EncoderFallbackOrder = ["libx264", "mpeg4"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private ActiveRecordingState? _activeRecording;

    public bool IsRecording => _activeRecording is not null;

    public static async Task<(bool success, IReadOnlyList<string> devices, string error)> EnumerateMicrophoneDevicesAsync(
        string ffmpegPath,
        CancellationToken cancellationToken = default)
    {
        var ffmpegLaunch = ResolveFfmpegLaunch(ffmpegPath);
        if (!ffmpegLaunch.Success)
        {
            return (false, Array.Empty<string>(), ffmpegLaunch.ErrorMessage);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegLaunch.ExecutablePath,
            Arguments = "-hide_banner -list_devices true -f dshow -i dummy",
            WorkingDirectory = GetSafeWorkingDirectory(ffmpegLaunch.WorkingDirectory),
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, Array.Empty<string>(), "Failed to start ffmpeg device scan.");
            }

            var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var waitTask = process.WaitForExitAsync(cancellationToken);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
            var completed = await Task.WhenAny(waitTask, timeoutTask);
            if (completed != waitTask && !process.HasExited)
            {
                process.Kill(true);
            }

            var stdout = await stdOutTask;
            var stderr = await stdErrTask;
            var directShowDevices = ParseDirectShowAudioDevices($"{stderr}{Environment.NewLine}{stdout}");
            var waveInDevices = EnumerateWaveInMicrophoneDevices();
            var devices = directShowDevices
                .Concat(waveInDevices)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (devices.Length > 0)
            {
                return (true, devices, string.Empty);
            }

            var fallbackError = NormalizeFfmpegError(stderr, "No microphone devices found.");
            return (true, Array.Empty<string>(), fallbackError);
        }
        catch (Exception ex)
        {
            return (false, Array.Empty<string>(), ex.Message);
        }
    }

    public async Task<(bool started, string message)> StartAsync(
        string storageRoot,
        string project,
        string assignedUser,
        string sessionName,
        string ffmpegPath,
        string microphoneDevice,
        string? overrideCaptureBaseRoot = null,
        CancellationToken cancellationToken = default)
    {
        if (_activeRecording is not null)
        {
            return (false, "A video recording is already in progress.");
        }

        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            return (false, "Storage root is not configured.");
        }

        var ffmpegLaunch = ResolveFfmpegLaunch(ffmpegPath);
        if (!ffmpegLaunch.Success)
        {
            return (false, ffmpegLaunch.ErrorMessage);
        }

        var ffmpegExecutable = ffmpegLaunch.ExecutablePath;
        var ffmpegWorkingDirectory = ffmpegLaunch.WorkingDirectory;
        var startLocal = DateTime.Now;
        var startUtc = DateTime.UtcNow;
        var normalizedProject = CaptureService.NormalizeProject(project);
        var normalizedUser = CaptureService.NormalizeUser(assignedUser);
        var baseRoot = string.IsNullOrWhiteSpace(overrideCaptureBaseRoot)
            ? CaptureService.BuildProjectRoot(storageRoot, normalizedProject)
            : overrideCaptureBaseRoot.Trim();
        Directory.CreateDirectory(baseRoot);
        var captureDirectory = Path.Combine(baseRoot, startLocal.ToString("yyyy"), startLocal.ToString("MM"), startLocal.ToString("dd"), startLocal.ToString("HHmmss_fff"));
        Directory.CreateDirectory(captureDirectory);

        var videoPath = Path.Combine(captureDirectory, "capture.mp4");
        var thumbnailPath = Path.Combine(captureDirectory, "thumbnail.png");
        var jsonPath = Path.Combine(captureDirectory, "capture.json");
        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        var microphone = string.IsNullOrWhiteSpace(microphoneDevice) ? "default" : microphoneDevice.Trim();
        var requestedMicrophoneAudio = ShouldCaptureMicrophone(microphone);
        var hasMicrophoneAudio = requestedMicrophoneAudio;
        var microphoneSetupMessage = string.Empty;
        if (requestedMicrophoneAudio)
        {
            var resolvedMicrophone = await ResolveMicrophoneDeviceForRecordingAsync(ffmpegExecutable, microphone, cancellationToken);
            if (!resolvedMicrophone.CanRecordAudio)
            {
                requestedMicrophoneAudio = false;
                hasMicrophoneAudio = false;
                microphone = string.Empty;
            }
            else
            {
                microphone = resolvedMicrophone.DeviceName;
            }

            if (!string.IsNullOrWhiteSpace(resolvedMicrophone.Message))
            {
                microphoneSetupMessage = resolvedMicrophone.Message;
            }
        }

        var startupMessage = string.Empty;
        var selectedEncoder = string.Empty;
        var errorMessages = new List<string>();
        var launch = LaunchResult.Failure("Video capture could not be started.");

        foreach (var encoder in EncoderFallbackOrder)
        {
            launch = await LaunchFfmpegAsync(
                ffmpegExecutable,
                ffmpegWorkingDirectory,
                BuildRecordArguments(virtualScreen, videoPath, requestedMicrophoneAudio, microphone, encoder),
                cancellationToken);
            if (launch.Started)
            {
                selectedEncoder = encoder;
                break;
            }

            errorMessages.Add($"{encoder}: {launch.ErrorMessage}");
            if (!requestedMicrophoneAudio)
            {
                continue;
            }

            var noAudioLaunch = await LaunchFfmpegAsync(
                ffmpegExecutable,
                ffmpegWorkingDirectory,
                BuildRecordArguments(virtualScreen, videoPath, includeMicrophoneAudio: false, microphone, encoder),
                cancellationToken);
            if (noAudioLaunch.Started)
            {
                launch = noAudioLaunch;
                hasMicrophoneAudio = false;
                selectedEncoder = encoder;
                startupMessage = string.IsNullOrWhiteSpace(microphoneSetupMessage)
                    ? "Microphone capture was unavailable, recording video without audio."
                    : $"{microphoneSetupMessage} Recording video without microphone audio.";
                break;
            }

            errorMessages.Add($"{encoder}/no-audio: {noAudioLaunch.ErrorMessage}");
        }

        if (!launch.Started || launch.Process is null)
        {
            TryDeleteFile(videoPath);
            TryDeleteFile(thumbnailPath);
            TryDeleteFile(jsonPath);
            TryDeleteDirectoryIfEmpty(captureDirectory);
            var aggregateError = errorMessages.Count == 0
                ? launch.ErrorMessage
                : string.Join(" | ", errorMessages.Distinct(StringComparer.OrdinalIgnoreCase));
            return (false, aggregateError);
        }

        _activeRecording = new ActiveRecordingState
        {
            Process = launch.Process,
            StdoutPumpTask = launch.StdoutPumpTask ?? Task.CompletedTask,
            StderrPumpTask = launch.StderrPumpTask ?? Task.CompletedTask,
            StorageRoot = storageRoot,
            CaptureDirectory = captureDirectory,
            VideoPath = videoPath,
            ThumbnailPath = thumbnailPath,
            JsonPath = jsonPath,
            Project = normalizedProject,
            AssignedUser = normalizedUser,
            SessionName = (sessionName ?? string.Empty).Trim(),
            StartLocal = startLocal,
            StartUtc = startUtc,
            ScreenBounds = virtualScreen,
            FfmpegPath = ffmpegExecutable,
            FfmpegWorkingDirectory = ffmpegWorkingDirectory,
            VideoEncoder = selectedEncoder,
            MicrophoneDevice = microphone,
            HasMicrophoneAudio = hasMicrophoneAudio
        };

        if (string.IsNullOrWhiteSpace(startupMessage))
        {
            var codecLabel = GetEncoderDisplayName(selectedEncoder);
            startupMessage = hasMicrophoneAudio
                ? $"Video recording started ({virtualScreen.Width}x{virtualScreen.Height}) with microphone audio ({codecLabel})."
                : $"Video recording started ({virtualScreen.Width}x{virtualScreen.Height}) without microphone audio ({codecLabel}).";

            if (!string.IsNullOrWhiteSpace(microphoneSetupMessage) &&
                !startupMessage.Contains(microphoneSetupMessage, StringComparison.OrdinalIgnoreCase))
            {
                startupMessage = $"{startupMessage} {microphoneSetupMessage}";
            }
        }

        return (true, startupMessage);
    }

    public async Task<CaptureRecord?> StopAsync(CancellationToken cancellationToken = default)
    {
        var state = _activeRecording;
        if (state is null)
        {
            return null;
        }

        _activeRecording = null;

        var process = state.Process;
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("q");
                await process.StandardInput.FlushAsync();
            }
        }
        catch
        {
            // Ignore write failures and fall through.
        }

        try
        {
            if (!process.HasExited)
            {
                var exitTask = process.WaitForExitAsync(cancellationToken);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                var completed = await Task.WhenAny(exitTask, timeoutTask);
                if (completed != exitTask && !process.HasExited)
                {
                    process.Kill(true);
                }
            }
        }
        catch
        {
            // Ignore process shutdown exceptions.
        }

        await DrainPumpTasksAsync(state, cancellationToken);

        if (!File.Exists(state.VideoPath))
        {
            return null;
        }

        await CreateVideoThumbnailAsync(state.FfmpegPath, state.FfmpegWorkingDirectory, state.VideoPath, state.ThumbnailPath, cancellationToken);
        var durationSeconds = Math.Max(0, (DateTime.UtcNow - state.StartUtc).TotalSeconds);
        var activeWindow = TryGetActiveWindowInfo();

        var metadata = new CaptureMetadata
        {
            MetadataVersion = CaptureMetadata.CurrentMetadataVersion,
            CaptureType = "video",
            ReviewState = ReviewState.None,
            Project = state.Project,
            AssignedUser = state.AssignedUser,
            AssignedUsers = string.IsNullOrWhiteSpace(state.AssignedUser) ? new List<string>() : new List<string> { state.AssignedUser },
            SessionName = state.SessionName,
            LocalTimestamp = state.StartLocal,
            UtcTimestamp = state.StartUtc,
            StorageRoot = state.StorageRoot,
            CaptureDirectory = state.CaptureDirectory,
            ImagePath = string.Empty,
            VideoPath = state.VideoPath,
            ThumbnailPath = File.Exists(state.ThumbnailPath) ? state.ThumbnailPath : string.Empty,
            DurationSeconds = durationSeconds,
            HasMicrophoneAudio = state.HasMicrophoneAudio,
            JsonPath = state.JsonPath,
            VirtualScreenLeft = state.ScreenBounds.Left,
            VirtualScreenTop = state.ScreenBounds.Top,
            VirtualScreenWidth = state.ScreenBounds.Width,
            VirtualScreenHeight = state.ScreenBounds.Height,
            ActiveWindowTitle = activeWindow.Title,
            ActiveProcessName = activeWindow.ProcessName,
            Note = string.Empty,
            SourceJsonPath = string.Empty,
            Monitors = Screen.AllScreens.Select(screen => new MonitorSnapshot
            {
                DeviceName = screen.DeviceName,
                IsPrimary = screen.Primary,
                Left = screen.Bounds.Left,
                Top = screen.Bounds.Top,
                Width = screen.Bounds.Width,
                Height = screen.Bounds.Height
            }).ToList()
        };

        await SafeFileWriteService.WriteJsonAtomicAsync(state.JsonPath, metadata, JsonOptions, cancellationToken);
        return new CaptureRecord { Metadata = metadata };
    }

    public void Abort()
    {
        var state = _activeRecording;
        _activeRecording = null;
        if (state is null)
        {
            return;
        }

        try
        {
            if (!state.Process.HasExited)
            {
                state.Process.Kill(true);
            }
        }
        catch
        {
            // Ignore abort errors.
        }
    }

    private static bool ShouldCaptureMicrophone(string microphoneDevice)
    {
        if (string.IsNullOrWhiteSpace(microphoneDevice))
        {
            return false;
        }

        return !string.Equals(microphoneDevice, "none", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(microphoneDevice, "off", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(microphoneDevice, "disabled", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ResolvedMicrophoneSelection> ResolveMicrophoneDeviceForRecordingAsync(
        string ffmpegPath,
        string configuredDevice,
        CancellationToken cancellationToken)
    {
        var normalizedConfigured = (configuredDevice ?? string.Empty).Trim();
        if (!ShouldCaptureMicrophone(normalizedConfigured))
        {
            return ResolvedMicrophoneSelection.DisableAudio("Microphone recording disabled.");
        }

        var scan = await EnumerateMicrophoneDevicesAsync(ffmpegPath, cancellationToken);
        if (!scan.success)
        {
            return ResolvedMicrophoneSelection.UseDevice(
                normalizedConfigured,
                $"Microphone detection failed ({scan.error}). Trying configured input.");
        }

        var devices = scan.devices
            .Where(device => !string.IsNullOrWhiteSpace(device))
            .Select(device => device.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (devices.Length == 0)
        {
            if (IsDefaultMicrophoneSelection(normalizedConfigured))
            {
                return ResolvedMicrophoneSelection.UseDevice(
                    "default",
                    "No microphone devices were listed. Trying the Windows default microphone.");
            }

            return ResolvedMicrophoneSelection.UseDevice(
                normalizedConfigured,
                $"No microphone devices were listed. Trying configured microphone '{normalizedConfigured}'.");
        }

        if (IsDefaultMicrophoneSelection(normalizedConfigured))
        {
            return ResolvedMicrophoneSelection.UseDevice(devices[0], $"Using microphone '{devices[0]}'.");
        }

        var exactMatch = devices.FirstOrDefault(device =>
            string.Equals(device, normalizedConfigured, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(exactMatch))
        {
            return ResolvedMicrophoneSelection.UseDevice(exactMatch, string.Empty);
        }

        return ResolvedMicrophoneSelection.UseDevice(
            normalizedConfigured,
            $"Configured microphone '{normalizedConfigured}' was not detected. Trying it directly.");
    }

    private static bool IsDefaultMicrophoneSelection(string selection)
    {
        var normalized = (selection ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ||
               string.Equals(normalized, "default", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "(Default microphone)", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<LaunchResult> LaunchFfmpegAsync(string ffmpegPath, string workingDirectory, string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            WorkingDirectory = GetSafeWorkingDirectory(workingDirectory),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        try
        {
            var process = Process.Start(startInfo);
            if (process is null)
            {
                return LaunchResult.Failure("Failed to start ffmpeg.");
            }

            await Task.Delay(650, cancellationToken);
            if (process.HasExited)
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken);
                process.Dispose();
                return LaunchResult.Failure(NormalizeFfmpegError(error, "ffmpeg exited immediately."));
            }

            return LaunchResult.Success(
                process,
                PumpStreamAsync(process.StandardOutput),
                PumpStreamAsync(process.StandardError));
        }
        catch (Win32Exception ex)
        {
            var message = ex.NativeErrorCode switch
            {
                2 => $"FFmpeg was not found ('{ffmpegPath}'). Revie could not locate a bundled ffmpeg.exe. Set a valid FFmpeg Path in Settings.",
                3 => $"Invalid FFmpeg working directory ('{GetSafeWorkingDirectory(workingDirectory)}').",
                _ => $"Failed to start ffmpeg: {ex.Message}"
            };
            return LaunchResult.Failure(message);
        }
        catch (Exception ex)
        {
            return LaunchResult.Failure($"Failed to start ffmpeg: {ex.Message}");
        }
    }

    private static async Task DrainPumpTasksAsync(ActiveRecordingState state, CancellationToken cancellationToken)
    {
        try
        {
            var allTasks = Task.WhenAll(state.StdoutPumpTask, state.StderrPumpTask);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            await Task.WhenAny(allTasks, timeoutTask);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static async Task PumpStreamAsync(StreamReader reader)
    {
        try
        {
            await reader.ReadToEndAsync();
        }
        catch
        {
            // Ignore stream pump failures.
        }
    }

    private static string BuildRecordArguments(Rectangle bounds, string outputVideoPath, bool includeMicrophoneAudio, string microphone, string videoEncoder)
    {
        var args = new StringBuilder();
        args.Append("-y ");
        args.Append($"-f gdigrab -framerate 30 -offset_x {bounds.Left} -offset_y {bounds.Top} ");
        args.Append($"-video_size {bounds.Width}x{bounds.Height} -i desktop ");
        if (includeMicrophoneAudio && !string.IsNullOrWhiteSpace(microphone))
        {
            var escapedMic = (microphone ?? string.Empty).Replace("\"", "\\\"");
            args.Append($"-f dshow -i audio=\"{escapedMic}\" ");
        }

        AppendVideoEncoderArguments(args, videoEncoder);
        if (includeMicrophoneAudio && !string.IsNullOrWhiteSpace(microphone))
        {
            args.Append("-c:a aac -b:a 128k ");
        }
        else
        {
            args.Append("-an ");
        }

        args.Append("-movflags +faststart ");
        args.Append($"\"{outputVideoPath}\"");
        return args.ToString();
    }

    private static void AppendVideoEncoderArguments(StringBuilder args, string videoEncoder)
    {
        var normalized = (videoEncoder ?? string.Empty).Trim();
        if (string.Equals(normalized, "mpeg4", StringComparison.OrdinalIgnoreCase))
        {
            args.Append("-c:v mpeg4 -q:v 4 -pix_fmt yuv420p ");
            return;
        }

        args.Append("-c:v libx264 -preset veryfast -pix_fmt yuv420p ");
    }

    private static string GetEncoderDisplayName(string videoEncoder)
    {
        if (string.Equals(videoEncoder, "mpeg4", StringComparison.OrdinalIgnoreCase))
        {
            return "MPEG-4";
        }

        return "H.264";
    }

    private static async Task CreateVideoThumbnailAsync(string ffmpegPath, string workingDirectory, string videoPath, string thumbnailPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-y -ss 00:00:00.500 -i \"{videoPath}\" -vframes 1 \"{thumbnailPath}\"",
            WorkingDirectory = GetSafeWorkingDirectory(workingDirectory),
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return;
            }

            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            // Thumbnail generation is best effort.
        }
    }

    private static FfmpegLaunchInfo ResolveFfmpegLaunch(string configuredPath)
    {
        var bundledFfmpeg = FindBundledFfmpegPath();
        var hasBundledFfmpeg = !string.IsNullOrWhiteSpace(bundledFfmpeg) && File.Exists(bundledFfmpeg);

        var input = (configuredPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            if (hasBundledFfmpeg)
            {
                return FfmpegLaunchInfo.CreateSuccess(bundledFfmpeg!, GetSafeWorkingDirectory(Path.GetDirectoryName(bundledFfmpeg)));
            }

            return FfmpegLaunchInfo.CreateSuccess("ffmpeg", GetSafeWorkingDirectory(AppContext.BaseDirectory));
        }

        var trimmed = input.Trim('"');
        if (string.Equals(trimmed, "ffmpeg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (hasBundledFfmpeg)
            {
                return FfmpegLaunchInfo.CreateSuccess(bundledFfmpeg!, GetSafeWorkingDirectory(Path.GetDirectoryName(bundledFfmpeg)));
            }
        }

        if (Directory.Exists(trimmed))
        {
            trimmed = Path.Combine(trimmed, "ffmpeg.exe");
        }

        if (Path.IsPathRooted(trimmed))
        {
            if (!File.Exists(trimmed))
            {
                if (hasBundledFfmpeg)
                {
                    return FfmpegLaunchInfo.CreateSuccess(bundledFfmpeg!, GetSafeWorkingDirectory(Path.GetDirectoryName(bundledFfmpeg)));
                }

                return FfmpegLaunchInfo.CreateFailure($"FFmpeg executable was not found at '{trimmed}'. Update Settings -> FFmpeg Path.");
            }

            var workingDirectory = Path.GetDirectoryName(trimmed);
            return FfmpegLaunchInfo.CreateSuccess(trimmed, GetSafeWorkingDirectory(workingDirectory));
        }

        if (trimmed.Contains(Path.DirectorySeparatorChar) || trimmed.Contains(Path.AltDirectorySeparatorChar))
        {
            var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, trimmed));
            if (File.Exists(candidate))
            {
                return FfmpegLaunchInfo.CreateSuccess(candidate, GetSafeWorkingDirectory(Path.GetDirectoryName(candidate)));
            }

            if (hasBundledFfmpeg)
            {
                return FfmpegLaunchInfo.CreateSuccess(bundledFfmpeg!, GetSafeWorkingDirectory(Path.GetDirectoryName(bundledFfmpeg)));
            }
        }

        return FfmpegLaunchInfo.CreateSuccess(trimmed, GetSafeWorkingDirectory(AppContext.BaseDirectory));
    }

    private static string? FindBundledFfmpegPath()
    {
        var candidates = new List<string>();

        void AddCandidate(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                candidates.Add(path);
            }
        }

        AddCandidate(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"));
        AddCandidate(Path.Combine(Environment.CurrentDirectory, "ffmpeg.exe"));
        AddCandidate(Path.Combine(AppContext.BaseDirectory, "ThirdParty", "ffmpeg", "win-x64", "ffmpeg.exe"));

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && current is not null; i++)
        {
            AddCandidate(Path.Combine(current.FullName, "ffmpeg.exe"));
            AddCandidate(Path.Combine(current.FullName, "ThirdParty", "ffmpeg", "win-x64", "ffmpeg.exe"));
            AddCandidate(Path.Combine(current.FullName, "Revie.App", "ThirdParty", "ffmpeg", "win-x64", "ffmpeg.exe"));
            current = current.Parent;
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }

    private static string GetSafeWorkingDirectory(string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred) && Directory.Exists(preferred))
        {
            return preferred;
        }

        if (Directory.Exists(AppContext.BaseDirectory))
        {
            return AppContext.BaseDirectory;
        }

        if (Directory.Exists(Environment.CurrentDirectory))
        {
            return Environment.CurrentDirectory;
        }

        return Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static void TryDeleteDirectoryIfEmpty(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return;
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(directoryPath).Any())
            {
                Directory.Delete(directoryPath, recursive: false);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static IReadOnlyList<string> ParseDirectShowAudioDevices(string output)
    {
        var devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inAudioSection = false;
        foreach (var raw in (output ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.IndexOf("DirectShow audio devices", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                inAudioSection = true;
                continue;
            }

            if (line.IndexOf("DirectShow video devices", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                inAudioSection = false;
            }

            if (line.IndexOf("Alternative name", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                continue;
            }

            var firstQuote = line.IndexOf('"');
            var lastQuote = line.LastIndexOf('"');
            if (firstQuote < 0 || lastQuote <= firstQuote)
            {
                continue;
            }

            var name = line.Substring(firstQuote + 1, lastQuote - firstQuote - 1).Trim();
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("@device_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hasAudioTypeMarker = line.IndexOf("(audio)", StringComparison.OrdinalIgnoreCase) >= 0;
            var hasVideoTypeMarker = line.IndexOf("(video)", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!inAudioSection && !hasAudioTypeMarker)
            {
                continue;
            }

            if (hasVideoTypeMarker && !hasAudioTypeMarker)
            {
                continue;
            }

            devices.Add(name);
        }

        return devices
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> EnumerateWaveInMicrophoneDevices()
    {
        var devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var count = waveInGetNumDevs();
            var capSize = (uint)Marshal.SizeOf<WAVEINCAPS>();
            for (uint index = 0; index < count; index++)
            {
                var result = waveInGetDevCaps(index, out var caps, capSize);
                if (result != 0)
                {
                    continue;
                }

                var name = (caps.szPname ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    devices.Add(name);
                }
            }
        }
        catch
        {
            // Best-effort fallback only.
        }

        return devices
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeFfmpegError(string error, string fallback)
    {
        var trimmed = (error ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return fallback;
        }

        var lines = trimmed
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return fallback;
        }

        var meaningful = lines
            .LastOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase))
            ?? lines.Last();
        return meaningful;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveInGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint waveInGetDevCaps(uint uDeviceID, out WAVEINCAPS pwic, uint cbwic);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WAVEINCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
    }

    private static ActiveWindowInfo TryGetActiveWindowInfo()
    {
        var handle = NativeMethods.GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return new ActiveWindowInfo();
        }

        var titleBuilder = new StringBuilder(1024);
        NativeMethods.GetWindowText(handle, titleBuilder, titleBuilder.Capacity);

        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        var processName = string.Empty;
        if (processId > 0)
        {
            try
            {
                processName = Process.GetProcessById((int)processId).ProcessName;
            }
            catch
            {
                processName = string.Empty;
            }
        }

        return new ActiveWindowInfo
        {
            Title = titleBuilder.ToString(),
            ProcessName = processName
        };
    }

    private sealed class ActiveRecordingState
    {
        public required Process Process { get; init; }
        public required Task StdoutPumpTask { get; init; }
        public required Task StderrPumpTask { get; init; }
        public required string StorageRoot { get; init; }
        public required string CaptureDirectory { get; init; }
        public required string VideoPath { get; init; }
        public required string ThumbnailPath { get; init; }
        public required string JsonPath { get; init; }
        public required string Project { get; init; }
        public required string AssignedUser { get; init; }
        public required string SessionName { get; init; }
        public required DateTime StartLocal { get; init; }
        public required DateTime StartUtc { get; init; }
        public required Rectangle ScreenBounds { get; init; }
        public required string FfmpegPath { get; init; }
        public required string FfmpegWorkingDirectory { get; init; }
        public required string VideoEncoder { get; init; }
        public required string MicrophoneDevice { get; init; }
        public required bool HasMicrophoneAudio { get; init; }
    }

    private sealed class FfmpegLaunchInfo
    {
        public bool Success { get; private init; }
        public string ExecutablePath { get; private init; } = string.Empty;
        public string WorkingDirectory { get; private init; } = string.Empty;
        public string ErrorMessage { get; private init; } = string.Empty;

        public static FfmpegLaunchInfo CreateSuccess(string executablePath, string workingDirectory)
        {
            return new FfmpegLaunchInfo
            {
                Success = true,
                ExecutablePath = executablePath,
                WorkingDirectory = workingDirectory
            };
        }

        public static FfmpegLaunchInfo CreateFailure(string errorMessage)
        {
            return new FfmpegLaunchInfo
            {
                Success = false,
                ErrorMessage = errorMessage
            };
        }
    }

    private sealed class ResolvedMicrophoneSelection
    {
        public bool CanRecordAudio { get; private init; }
        public string DeviceName { get; private init; } = string.Empty;
        public string Message { get; private init; } = string.Empty;

        public static ResolvedMicrophoneSelection UseDevice(string deviceName, string message)
        {
            return new ResolvedMicrophoneSelection
            {
                CanRecordAudio = true,
                DeviceName = deviceName,
                Message = message
            };
        }

        public static ResolvedMicrophoneSelection DisableAudio(string message)
        {
            return new ResolvedMicrophoneSelection
            {
                CanRecordAudio = false,
                DeviceName = string.Empty,
                Message = message
            };
        }
    }

    private sealed class LaunchResult
    {
        public bool Started { get; private init; }
        public string ErrorMessage { get; private init; } = string.Empty;
        public Process? Process { get; private init; }
        public Task? StdoutPumpTask { get; private init; }
        public Task? StderrPumpTask { get; private init; }

        public static LaunchResult Success(Process process, Task stdoutPumpTask, Task stderrPumpTask)
        {
            return new LaunchResult
            {
                Started = true,
                Process = process,
                StdoutPumpTask = stdoutPumpTask,
                StderrPumpTask = stderrPumpTask
            };
        }

        public static LaunchResult Failure(string errorMessage)
        {
            return new LaunchResult
            {
                Started = false,
                ErrorMessage = errorMessage
            };
        }
    }
}
