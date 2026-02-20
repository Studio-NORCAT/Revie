using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using Revie.App.Models;
using Revie.App.Native;
using Screen = System.Windows.Forms.Screen;

namespace Revie.App.Services;

public sealed class CaptureService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public Task<CaptureRecord> CaptureAsync(
        string storageRoot,
        byte[]? overlayPng,
        Rectangle? captureBounds,
        string note,
        string project,
        string assignedUser,
        string sessionName,
        string? overrideCaptureBaseRoot = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => CaptureCore(storageRoot, overlayPng, captureBounds, note, project, assignedUser, sessionName, overrideCaptureBaseRoot), cancellationToken);
    }

    private static CaptureRecord CaptureCore(
        string storageRoot,
        byte[]? overlayPng,
        Rectangle? captureBounds,
        string note,
        string project,
        string assignedUser,
        string sessionName,
        string? overrideCaptureBaseRoot)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            throw new InvalidOperationException("Storage root is not configured.");
        }

        var virtualScreen = captureBounds ?? System.Windows.Forms.SystemInformation.VirtualScreen;
        if (virtualScreen.Width <= 0 || virtualScreen.Height <= 0)
        {
            virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        }

        using var bitmap = new Bitmap(virtualScreen.Width, virtualScreen.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(virtualScreen.Left, virtualScreen.Top, 0, 0, virtualScreen.Size, CopyPixelOperation.SourceCopy);

            if (overlayPng is { Length: > 0 })
            {
                using var stream = new MemoryStream(overlayPng);
                using var overlayImage = Image.FromStream(stream);
                graphics.DrawImage(overlayImage, 0, 0, virtualScreen.Width, virtualScreen.Height);
            }
        }

        var localTime = DateTime.Now;
        var utcTime = DateTime.UtcNow;
        var normalizedProject = NormalizeProject(project);
        var normalizedUser = NormalizeUser(assignedUser);
        var baseRoot = string.IsNullOrWhiteSpace(overrideCaptureBaseRoot)
            ? BuildProjectRoot(storageRoot, normalizedProject)
            : overrideCaptureBaseRoot.Trim();
        Directory.CreateDirectory(baseRoot);
        var captureDirectory = Path.Combine(baseRoot, localTime.ToString("yyyy"), localTime.ToString("MM"), localTime.ToString("dd"), localTime.ToString("HHmmss_fff"));
        Directory.CreateDirectory(captureDirectory);

        if (!Directory.Exists(captureDirectory))
        {
            throw new IOException($"Capture directory was not created: {captureDirectory}");
        }

        var imagePath = Path.Combine(captureDirectory, "capture.png");
        var jsonPath = Path.Combine(captureDirectory, "capture.json");
        SaveBitmap(bitmap, imagePath);

        var activeWindow = TryGetActiveWindowInfo();
        var metadata = new CaptureMetadata
        {
            MetadataVersion = CaptureMetadata.CurrentMetadataVersion,
            CaptureType = "image",
            ReviewState = ReviewState.None,
            Project = normalizedProject,
            AssignedUser = normalizedUser,
            AssignedUsers = string.IsNullOrWhiteSpace(normalizedUser) ? new List<string>() : new List<string> { normalizedUser },
            SessionName = (sessionName ?? string.Empty).Trim(),
            LocalTimestamp = localTime,
            UtcTimestamp = utcTime,
            StorageRoot = storageRoot,
            CaptureDirectory = captureDirectory,
            ImagePath = imagePath,
            VideoPath = string.Empty,
            ThumbnailPath = imagePath,
            DurationSeconds = 0,
            HasMicrophoneAudio = false,
            JsonPath = jsonPath,
            VirtualScreenLeft = virtualScreen.Left,
            VirtualScreenTop = virtualScreen.Top,
            VirtualScreenWidth = virtualScreen.Width,
            VirtualScreenHeight = virtualScreen.Height,
            ActiveWindowTitle = activeWindow.Title,
            ActiveProcessName = activeWindow.ProcessName,
            Note = note,
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

        SafeFileWriteService.WriteJsonAtomic(jsonPath, metadata, JsonOptions);

        return new CaptureRecord { Metadata = metadata };
    }

    public static string BuildProjectRoot(string storageRoot, string project)
    {
        var normalizedProject = NormalizeProject(project);
        if (string.IsNullOrWhiteSpace(normalizedProject))
        {
            return storageRoot;
        }

        return Path.Combine(storageRoot, "Projects", ToPathSafeProjectName(normalizedProject));
    }

    public static string NormalizeProject(string? project)
    {
        return (project ?? string.Empty).Trim();
    }

    public static string NormalizeUser(string? user)
    {
        return (user ?? string.Empty).Trim();
    }

    public static IReadOnlyList<string> NormalizeUsers(IEnumerable<string>? users)
    {
        if (users is null)
        {
            return Array.Empty<string>();
        }

        return users
            .Where(user => !string.IsNullOrWhiteSpace(user))
            .Select(NormalizeUser)
            .Where(user => !string.IsNullOrWhiteSpace(user))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(user => user, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> ParseUserTags(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Array.Empty<string>();
        }

        return NormalizeUsers(input
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public static string ToPathSafeProjectName(string project)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(project.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "Project" : cleaned;
    }

    private static void SaveBitmap(Bitmap bitmap, string imagePath)
    {
        // Retry briefly to handle transient file lock/network latency on shared roots.
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                bitmap.Save(imagePath, ImageFormat.Png);
                return;
            }
            catch when (attempt < maxAttempts)
            {
                Thread.Sleep(120);
            }
        }

        bitmap.Save(imagePath, ImageFormat.Png);
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
}
