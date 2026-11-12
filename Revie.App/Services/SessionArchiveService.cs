using System.Text.Json;
using Revie.App.Models;

namespace Revie.App.Services;

public sealed class SessionArchiveService
{
    private const string TemporarySessionPrefix = ".revie-saving-";
    private const string AttachmentsFolderName = "attachments";
    private const string ScreenshotsFolderName = "screenshots";
    private const string VideosFolderName = "videos";
    private const string ThumbnailsFolderName = "thumbnails";

    public readonly record struct SessionSaveProgress(int ProcessedCaptureCount, int TotalCaptureCount);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public Task<string> SaveSessionAsync(
        string storageRoot,
        string sessionName,
        DateTime sessionStartLocal,
        DateTime sessionStartUtc,
        DateTime sessionEndLocal,
        DateTime sessionEndUtc,
        IReadOnlyList<CaptureRecord> captures,
        string activeProject,
        IProgress<SessionSaveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => SaveSessionCore(
            storageRoot,
            sessionName,
            sessionStartLocal,
            sessionStartUtc,
            sessionEndLocal,
            sessionEndUtc,
            captures,
            activeProject,
            progress), cancellationToken);
    }

    private static string SaveSessionCore(
        string storageRoot,
        string sessionName,
        DateTime sessionStartLocal,
        DateTime sessionStartUtc,
        DateTime sessionEndLocal,
        DateTime sessionEndUtc,
        IReadOnlyList<CaptureRecord> captures,
        string activeProject,
        IProgress<SessionSaveProgress>? progress)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            throw new InvalidOperationException("Storage root is not configured.");
        }

        var normalizedName = string.IsNullOrWhiteSpace(sessionName)
            ? "ReviewSession"
            : sessionName.Trim();
        var safeName = ToPathSafeName(normalizedName);

        var sessionsRoot = Path.Combine(storageRoot, "Sessions");
        Directory.CreateDirectory(sessionsRoot);

        var targetSessionDirectory = Path.Combine(sessionsRoot, safeName);
        var sessionDirectory = Path.Combine(sessionsRoot, $"{TemporarySessionPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(sessionDirectory);
        try
        {
            var capturesDirectory = Path.Combine(sessionDirectory, "captures");
            var screenshotsDirectory = Path.Combine(sessionDirectory, ScreenshotsFolderName);
            var videosDirectory = Path.Combine(sessionDirectory, VideosFolderName);
            var thumbnailsDirectory = Path.Combine(sessionDirectory, ThumbnailsFolderName);
            Directory.CreateDirectory(capturesDirectory);

            var orderedCaptures = captures
                .OrderBy(record => record.LocalTimestamp)
                .ToArray();

            var exported = new List<SessionCaptureEntry>(orderedCaptures.Length);
            progress?.Report(new SessionSaveProgress(0, orderedCaptures.Length));
            for (var i = 0; i < orderedCaptures.Length; i++)
            {
                var record = orderedCaptures[i];
                var sourceDirectory = record.Metadata.CaptureDirectory ?? string.Empty;
                var localTime = record.Metadata.LocalTimestamp == default ? DateTime.Now : record.Metadata.LocalTimestamp;
                var captureType = NormalizeCaptureType(record.Metadata.CaptureType);
                var captureBaseName = $"{i + 1:000}_{localTime:yyyyMMdd_HHmmss_fff}";
                var targetCaptureDirectory = Path.Combine(capturesDirectory, AttachmentsFolderName, captureBaseName);
                var targetImagePath = captureType == "image"
                    ? Path.Combine(screenshotsDirectory, $"{captureBaseName}.png")
                    : string.Empty;
                var targetVideoPath = captureType == "video"
                    ? Path.Combine(videosDirectory, $"{captureBaseName}.mp4")
                    : string.Empty;
                var targetThumbnailPath = captureType == "video"
                    ? Path.Combine(thumbnailsDirectory, $"{captureBaseName}.png")
                    : Path.Combine(thumbnailsDirectory, $"{captureBaseName}.jpg");
                var targetJsonPath = Path.Combine(capturesDirectory, $"{captureBaseName}.json");

                ExportCaptureFiles(
                    record.Metadata,
                    sourceDirectory,
                    targetCaptureDirectory,
                    targetImagePath,
                    targetVideoPath,
                    targetThumbnailPath);
                var targetMediaPath = captureType == "video" ? targetVideoPath : targetImagePath;
                if (!File.Exists(targetMediaPath))
                    throw new FileNotFoundException($"Session capture media is missing: {sourceDirectory}", targetMediaPath);
                if (captureType == "image" && File.Exists(targetImagePath))
                    SidebarThumbnailService.EnsureThumbnailAtPath(targetImagePath, targetThumbnailPath);

                WriteSessionCaptureMetadata(
                    record.Metadata,
                    normalizedName,
                    sessionDirectory,
                    targetCaptureDirectory,
                    targetImagePath,
                    targetVideoPath,
                    targetThumbnailPath,
                    targetJsonPath);

                exported.Add(new SessionCaptureEntry
                {
                    LocalTimestamp = record.Metadata.LocalTimestamp,
                    UtcTimestamp = record.Metadata.UtcTimestamp,
                    Project = record.Metadata.Project ?? string.Empty,
                    AssignedUser = record.Metadata.AssignedUser ?? string.Empty,
                    SessionName = record.Metadata.SessionName ?? string.Empty,
                    WindowTitle = record.Metadata.ActiveWindowTitle ?? string.Empty,
                    ProcessName = record.Metadata.ActiveProcessName ?? string.Empty,
                    OriginalCaptureDirectory = record.Metadata.CaptureDirectory ?? string.Empty,
                    OriginalJsonPath = record.Metadata.JsonPath ?? string.Empty,
                    ExportedCaptureDirectory = GetRelativePathOrAbsolute(sessionDirectory, targetCaptureDirectory),
                    ExportedMetadataPath = GetRelativePathOrAbsolute(sessionDirectory, targetJsonPath),
                    ExportedMediaPath = GetRelativePathOrAbsolute(sessionDirectory, targetMediaPath),
                    ExportedThumbnailPath = File.Exists(targetThumbnailPath)
                        ? GetRelativePathOrAbsolute(sessionDirectory, targetThumbnailPath)
                        : string.Empty
                });

                progress?.Report(new SessionSaveProgress(i + 1, orderedCaptures.Length));
            }

            var manifest = new SessionManifest
            {
                LayoutVersion = 2,
                SessionName = normalizedName,
                SessionStartLocal = sessionStartLocal,
                SessionStartUtc = sessionStartUtc,
                SessionEndLocal = sessionEndLocal,
                SessionEndUtc = sessionEndUtc,
                CaptureCount = exported.Count,
                ActiveProjectAtSave = CaptureService.NormalizeProject(activeProject),
                Captures = exported
            };

            var manifestPath = Path.Combine(sessionDirectory, "session.json");
            SafeFileWriteService.WriteJsonAtomic(manifestPath, manifest, JsonOptions);
            return MoveToUniqueDirectory(sessionDirectory, targetSessionDirectory);
        }
        catch
        {
            TryDeleteTemporarySessionDirectory(sessionDirectory, sessionsRoot);
            throw;
        }
    }

    private static void ExportCaptureFiles(
        CaptureMetadata sourceMetadata,
        string sourceDirectory,
        string targetCaptureDirectory,
        string targetImagePath,
        string targetVideoPath,
        string targetThumbnailPath)
    {
        var sourceJsonPath = ResolveSourcePath(sourceMetadata.JsonPath, sourceDirectory, "capture.json");
        var sourceImagePath = ResolveSourcePath(sourceMetadata.ImagePath, sourceDirectory, "capture.png");
        var sourceVideoPath = ResolveSourcePath(sourceMetadata.VideoPath, sourceDirectory, "capture.mp4");
        var sourceThumbnailPath = ResolveSourcePath(sourceMetadata.ThumbnailPath, sourceDirectory, "thumbnail.png");

        if (!string.IsNullOrWhiteSpace(targetImagePath))
        {
            CopyFileIfExists(sourceImagePath, targetImagePath);
        }

        if (!string.IsNullOrWhiteSpace(targetVideoPath))
        {
            CopyFileIfExists(sourceVideoPath, targetVideoPath);
        }

        if (!string.IsNullOrWhiteSpace(targetThumbnailPath) && !string.Equals(targetThumbnailPath, targetImagePath, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetExtension(targetThumbnailPath), ".jpg", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(sourceThumbnailPath))
            {
                CopyFileIfExists(sourceThumbnailPath, targetThumbnailPath);
            }
            else if (File.Exists(sourceImagePath))
            {
                CopyFileIfExists(sourceImagePath, targetThumbnailPath);
            }
        }

        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            return;
        }

        var excludedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddExcludedPath(excludedPaths, sourceJsonPath);
        AddExcludedPath(excludedPaths, sourceImagePath);
        AddExcludedPath(excludedPaths, sourceVideoPath);
        AddExcludedPath(excludedPaths, sourceThumbnailPath);
        AddExcludedPath(excludedPaths, Path.Combine(sourceDirectory, SidebarThumbnailService.FileName));

        CopyAuxiliaryContent(sourceDirectory, targetCaptureDirectory, excludedPaths);
    }

    private static void AddExcludedPath(HashSet<string> excludedPaths, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        excludedPaths.Add(Path.GetFullPath(path));
    }

    private static string MoveToUniqueDirectory(string sourcePath, string desiredPath)
    {
        for (var suffix = 0; ; suffix++)
        {
            var candidate = suffix == 0 ? desiredPath : $"{desiredPath}_{suffix:00}";
            if (Directory.Exists(candidate) || File.Exists(candidate)) continue;
            try
            {
                Directory.Move(sourcePath, candidate);
                return candidate;
            }
            catch (IOException) when (Directory.Exists(candidate) || File.Exists(candidate))
            {
                // Another save took this name; try the next suffix.
            }
        }
    }

    private static void TryDeleteTemporarySessionDirectory(string path, string sessionsRoot)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var parent = Path.GetDirectoryName(fullPath);
            if (string.Equals(parent, Path.GetFullPath(sessionsRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(fullPath).StartsWith(TemporarySessionPrefix, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(fullPath))
                Directory.Delete(fullPath, recursive: true);
        }
        catch
        {
            // Source captures remain staged so the session can be saved again.
        }
    }

    public static void CleanupStaleTemporarySessions(string storageRoot)
    {
        if (string.IsNullOrWhiteSpace(storageRoot)) return;
        var sessionsRoot = Path.Combine(storageRoot, "Sessions");
        if (!Directory.Exists(sessionsRoot)) return;

        try
        {
            var threshold = DateTime.UtcNow.AddDays(-7);
            foreach (var directory in Directory.EnumerateDirectories(sessionsRoot, $"{TemporarySessionPrefix}*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < threshold)
                        TryDeleteTemporarySessionDirectory(directory, sessionsRoot);
                }
                catch
                {
                    // Leave unreadable or active directories alone.
                }
            }
        }
        catch
        {
            // Session storage may be offline during startup.
        }
    }

    private static void CopyAuxiliaryContent(string sourcePath, string targetPath, HashSet<string> excludedPaths)
    {
        var sourceDirectory = new DirectoryInfo(sourcePath);
        if (!sourceDirectory.Exists)
        {
            return;
        }

        foreach (var file in sourceDirectory.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var sourceFilePath = file.FullName;
            if (excludedPaths.Contains(Path.GetFullPath(sourceFilePath)))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(sourceDirectory.FullName, sourceFilePath);
            var destination = Path.Combine(targetPath, relativePath);
            var destinationDirectory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            file.CopyTo(destination, overwrite: true);
        }
    }

    private static void CopyFileIfExists(string sourcePath, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            string.IsNullOrWhiteSpace(destinationPath) ||
            !File.Exists(sourcePath))
        {
            return;
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    private static string ResolveSourcePath(string path, string sourceDirectory, string fallbackName)
    {
        var normalized = (path ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.IsNullOrWhiteSpace(sourceDirectory)
                ? string.Empty
                : Path.Combine(sourceDirectory, fallbackName);
        }

        if (!Path.IsPathRooted(normalized) && !string.IsNullOrWhiteSpace(sourceDirectory))
        {
            normalized = Path.Combine(sourceDirectory, normalized.Replace('/', Path.DirectorySeparatorChar));
        }

        return normalized;
    }

    private static string NormalizeCaptureType(string captureType)
    {
        return string.Equals(captureType, "video", StringComparison.OrdinalIgnoreCase) ? "video" : "image";
    }

    private static string GetRelativePathOrAbsolute(string rootPath, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(targetPath))
        {
            return targetPath ?? string.Empty;
        }

        try
        {
            return Path.GetRelativePath(rootPath, targetPath).Replace('\\', '/');
        }
        catch
        {
            return targetPath;
        }
    }

    private static string ToPathSafeName(string input)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var safe = new string(input.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray())
            .Trim().TrimEnd('.', ' ');
        if (safe.Length > 80) safe = safe[..80].TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(safe)) return "Session";
        var stem = safe.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                    "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
            .Contains(stem, StringComparer.OrdinalIgnoreCase))
            safe = $"_{safe}";
        return safe;
    }

    private static void WriteSessionCaptureMetadata(
        CaptureMetadata sourceMetadata,
        string sessionName,
        string sessionDirectory,
        string targetCaptureDirectory,
        string targetImagePath,
        string targetVideoPath,
        string targetThumbnailPath,
        string targetJsonPath)
    {
        var metadata = LoadSourceMetadata(sourceMetadata);

        var captureType = string.Equals(sourceMetadata.CaptureType, "video", StringComparison.OrdinalIgnoreCase) ? "video" : "image";
        var assignedUsers = sourceMetadata.AssignedUsers is { Count: > 0 }
            ? CaptureService.NormalizeUsers(sourceMetadata.AssignedUsers).ToList()
            : CaptureService.NormalizeUsers(new[] { sourceMetadata.AssignedUser }).ToList();

        metadata.CaptureType = captureType;
        metadata.MetadataVersion = CaptureMetadata.CurrentMetadataVersion;
        metadata.ReviewState = ReviewState.Normalize(sourceMetadata.ReviewState);
        metadata.Project = CaptureService.NormalizeProject(sourceMetadata.Project);
        metadata.AssignedUsers = assignedUsers;
        metadata.AssignedUser = assignedUsers.FirstOrDefault() ?? string.Empty;
        metadata.SessionName = sessionName;
        metadata.LocalTimestamp = sourceMetadata.LocalTimestamp;
        metadata.UtcTimestamp = sourceMetadata.UtcTimestamp;
        metadata.StorageRoot = sourceMetadata.StorageRoot ?? string.Empty;
        metadata.CaptureDirectory = GetRelativePathOrAbsolute(sessionDirectory, targetCaptureDirectory);
        var metadataDirectory = Path.GetDirectoryName(targetJsonPath) ?? sessionDirectory;
        metadata.ImagePath = captureType == "image" ? GetRelativePathOrAbsolute(metadataDirectory, targetImagePath) : string.Empty;
        metadata.VideoPath = captureType == "video" ? GetRelativePathOrAbsolute(metadataDirectory, targetVideoPath) : string.Empty;
        metadata.ThumbnailPath = File.Exists(targetThumbnailPath)
            ? GetRelativePathOrAbsolute(metadataDirectory, targetThumbnailPath)
            : (captureType == "image" ? metadata.ImagePath : string.Empty);
        metadata.DurationSeconds = sourceMetadata.DurationSeconds;
        metadata.HasMicrophoneAudio = sourceMetadata.HasMicrophoneAudio;
        metadata.JsonPath = Path.GetFileName(targetJsonPath);
        metadata.VirtualScreenLeft = sourceMetadata.VirtualScreenLeft;
        metadata.VirtualScreenTop = sourceMetadata.VirtualScreenTop;
        metadata.VirtualScreenWidth = sourceMetadata.VirtualScreenWidth;
        metadata.VirtualScreenHeight = sourceMetadata.VirtualScreenHeight;
        metadata.ActiveWindowTitle = sourceMetadata.ActiveWindowTitle ?? string.Empty;
        metadata.ActiveProcessName = sourceMetadata.ActiveProcessName ?? string.Empty;
        metadata.Note = sourceMetadata.Note ?? string.Empty;
        metadata.SourceJsonPath = sourceMetadata.SourceJsonPath ?? string.Empty;
        metadata.UploadedImagePaths = NormalizeUploadedImagePaths(sourceMetadata, targetCaptureDirectory);
        metadata.RevisionReviewRequestedForUser = sourceMetadata.RevisionReviewRequestedForUser ?? string.Empty;
        metadata.RevisionReviewRequestedUtc = sourceMetadata.RevisionReviewRequestedUtc;
        metadata.RevisionReviewCompletedUtc = sourceMetadata.RevisionReviewCompletedUtc;
        metadata.Monitors = sourceMetadata.Monitors ?? new List<MonitorSnapshot>();

        SafeFileWriteService.WriteJsonAtomic(targetJsonPath, metadata, JsonOptions);
    }

    private static CaptureMetadata LoadSourceMetadata(CaptureMetadata sourceMetadata)
    {
        var sourceJsonPath = sourceMetadata.JsonPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceJsonPath) || !File.Exists(sourceJsonPath))
        {
            return new CaptureMetadata();
        }

        try
        {
            return JsonSerializer.Deserialize<CaptureMetadata>(File.ReadAllText(sourceJsonPath), JsonOptions) ?? new CaptureMetadata();
        }
        catch
        {
            return new CaptureMetadata();
        }
    }

    private static List<string> NormalizeUploadedImagePaths(CaptureMetadata sourceMetadata, string targetCaptureDirectory)
    {
        var normalizedPaths = new List<string>();
        var sourceCaptureDirectory = sourceMetadata.CaptureDirectory?.Trim() ?? string.Empty;
        foreach (var rawPath in sourceMetadata.UploadedImagePaths ?? new List<string>())
        {
            var trimmed = (rawPath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (Path.IsPathRooted(trimmed) && !string.IsNullOrWhiteSpace(sourceCaptureDirectory))
            {
                try
                {
                    var relativePath = Path.GetRelativePath(sourceCaptureDirectory, trimmed);
                    if (!relativePath.StartsWith("..", StringComparison.Ordinal))
                    {
                        normalizedPaths.Add(relativePath.Replace('\\', '/'));
                        continue;
                    }
                }
                catch
                {
                    // Fall back to the original stored path.
                }
            }

            normalizedPaths.Add(trimmed.Replace('\\', '/'));
        }

        return normalizedPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed class SessionManifest
    {
        public int LayoutVersion { get; set; }
        public string SessionName { get; set; } = string.Empty;
        public DateTime SessionStartLocal { get; set; }
        public DateTime SessionStartUtc { get; set; }
        public DateTime SessionEndLocal { get; set; }
        public DateTime SessionEndUtc { get; set; }
        public int CaptureCount { get; set; }
        public string ActiveProjectAtSave { get; set; } = string.Empty;
        public List<SessionCaptureEntry> Captures { get; set; } = new();
    }

    private sealed class SessionCaptureEntry
    {
        public DateTime LocalTimestamp { get; set; }
        public DateTime UtcTimestamp { get; set; }
        public string Project { get; set; } = string.Empty;
        public string AssignedUser { get; set; } = string.Empty;
        public string SessionName { get; set; } = string.Empty;
        public string WindowTitle { get; set; } = string.Empty;
        public string ProcessName { get; set; } = string.Empty;
        public string OriginalCaptureDirectory { get; set; } = string.Empty;
        public string OriginalJsonPath { get; set; } = string.Empty;
        public string ExportedCaptureDirectory { get; set; } = string.Empty;
        public string ExportedMetadataPath { get; set; } = string.Empty;
        public string ExportedMediaPath { get; set; } = string.Empty;
        public string ExportedThumbnailPath { get; set; } = string.Empty;
    }
}
