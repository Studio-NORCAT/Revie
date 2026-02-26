using System.Text;
using System.Text.Json;
using Revie.App.Models;

namespace Revie.App.Services;

public sealed class SessionBrowseService
{
    private const string DeleteQueueDirectoryName = ".revie-delete-queue";
    private const string DeletePendingMarkerFileName = ".revie-delete-pending";
    private static readonly string[] PreferredPreviewFileNames =
    {
        "thumbnail.png",
        "thumbnail.jpg",
        "thumbnail.jpeg",
        "capture.png",
        "capture.jpg",
        "capture.jpeg",
        "capture.webp",
        "capture.bmp"
    };
    private static readonly HashSet<string> ImageFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp",
        ".webp"
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public IReadOnlyList<SessionFolderSummary> GetSessions(string storageRoot)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            return Array.Empty<SessionFolderSummary>();
        }

        var sessionsRoot = Path.Combine(storageRoot, "Sessions");
        if (!Directory.Exists(sessionsRoot))
        {
            return Array.Empty<SessionFolderSummary>();
        }

        string[] sessionFolders;
        try
        {
            sessionFolders = Directory.EnumerateDirectories(sessionsRoot)
                .Where(folder => !IsPendingDeletePath(folder))
                .Where(folder => !IsDeletePendingFolder(folder))
                .ToArray();
        }
        catch (Exception ex)
        {
            AppLogService.Error("Session.Scan.EnumerateFailed", "Failed enumerating session folders.", ex, new
            {
                sessionsRoot
            });
            return Array.Empty<SessionFolderSummary>();
        }

        var sessions = new List<SessionFolderSummary>();
        foreach (var folder in sessionFolders)
        {
            try
            {
                var manifestPath = Path.Combine(folder, "session.json");
                var defaultStart = Directory.GetCreationTime(folder);
                var defaultEnd = Directory.GetLastWriteTime(folder);
                var defaultName = Path.GetFileName(folder);
                var sessionRecords = GetSessionCaptures(folder)
                    .Where(HasValidMediaFile)
                    .ToArray();
                var defaultCount = sessionRecords.Length;
                var sessionReviewState = DetermineSessionReviewState(sessionRecords);
                var hasRevisedUploads = sessionRecords.Any(record => HasUploadedRevisions(record.Metadata));
                var sessionUsers = new List<string>();
                var previewImagePath = string.Empty;

                if (!File.Exists(manifestPath))
                {
                    if (defaultCount <= 0)
                    {
                        continue;
                    }

                    sessionUsers = CollectSessionUsers(folder);
                    previewImagePath = FindSessionPreviewImagePath(folder);
                    sessions.Add(new SessionFolderSummary
                    {
                        SessionName = defaultName,
                        Project = string.Empty,
                        Users = sessionUsers,
                        StartLocal = defaultStart,
                        EndLocal = defaultEnd,
                        FolderPath = folder,
                        CaptureCount = defaultCount,
                        PreviewImagePath = previewImagePath,
                        ReviewState = sessionReviewState,
                        HasRevisedUploads = hasRevisedUploads
                    });
                    continue;
                }

                var manifestJson = ReadAllTextAllowShared(manifestPath);
                if (string.IsNullOrWhiteSpace(manifestJson))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(manifestJson);
                var root = document.RootElement;

                var name = TryReadString(root, "SessionName");
                var project = TryReadString(root, "ActiveProjectAtSave");
                var start = TryReadDateTime(root, "SessionStartLocal") ?? defaultStart;
                var end = TryReadDateTime(root, "SessionEndLocal") ?? defaultEnd;
                var captureCount = defaultCount;
                if (root.TryGetProperty("Captures", out var capturesElement) &&
                    capturesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var capture in capturesElement.EnumerateArray())
                    {
                        var assignedUser = CaptureService.NormalizeUser(TryReadString(capture, "AssignedUser"));
                        if (!string.IsNullOrWhiteSpace(assignedUser) &&
                            !sessionUsers.Contains(assignedUser, StringComparer.OrdinalIgnoreCase))
                        {
                            sessionUsers.Add(assignedUser);
                        }

                        var exportedCaptureDirectory = TryReadString(capture, "ExportedCaptureDirectory");
                        if (string.IsNullOrWhiteSpace(exportedCaptureDirectory))
                        {
                            continue;
                        }

                        var manifestPreviewPath = ResolvePreviewImageFromCaptureDirectory(exportedCaptureDirectory, folder);
                        if (!string.IsNullOrWhiteSpace(manifestPreviewPath))
                        {
                            previewImagePath = manifestPreviewPath;
                        }
                    }
                }

                if (sessionUsers.Count == 0)
                {
                    sessionUsers = sessionRecords
                        .SelectMany(record => record.Metadata.AssignedUsers)
                        .Where(user => !string.IsNullOrWhiteSpace(user))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(user => user, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (sessionUsers.Count == 0)
                    {
                        sessionUsers = CollectSessionUsers(folder);
                    }
                }

                if (string.IsNullOrWhiteSpace(previewImagePath))
                {
                    previewImagePath = FindSessionPreviewImagePath(folder);
                }

                // Ignore orphaned/partially deleted session folders that no longer contain captures.
                if (defaultCount <= 0 && string.IsNullOrWhiteSpace(previewImagePath))
                {
                    continue;
                }

                sessions.Add(new SessionFolderSummary
                {
                    SessionName = string.IsNullOrWhiteSpace(name) ? defaultName : name,
                    Project = project ?? string.Empty,
                    Users = sessionUsers,
                    StartLocal = start,
                    EndLocal = end,
                    FolderPath = folder,
                    CaptureCount = captureCount,
                    PreviewImagePath = previewImagePath,
                    ReviewState = sessionReviewState,
                    HasRevisedUploads = hasRevisedUploads
                });
            }
            catch (Exception ex)
            {
                AppLogService.Error("Session.Scan.FolderFailed", "Failed reading session folder.", ex, new
                {
                    folder
                });

                var fallback = TryBuildFallbackSummary(folder);
                if (fallback is not null)
                {
                    sessions.Add(fallback);
                }
            }
        }

        return sessions
            .OrderByDescending(session => session.StartLocal)
            .ToArray();
    }

    public IReadOnlyList<CaptureRecord> GetSessionCaptures(string sessionFolderPath)
    {
        if (string.IsNullOrWhiteSpace(sessionFolderPath))
        {
            return Array.Empty<CaptureRecord>();
        }

        var capturesRoot = Path.Combine(sessionFolderPath, "captures");
        if (!Directory.Exists(capturesRoot))
        {
            return Array.Empty<CaptureRecord>();
        }

        var records = new List<CaptureRecord>();
        foreach (var jsonPath in SafeEnumerateCaptureJsonFiles(capturesRoot))
        {
            var record = TryLoadRecordFromJsonPath(jsonPath, sessionFolderPath, applyLinkedAnnotationOverrides: true);
            if (record is null)
            {
                continue;
            }

            records.Add(record);
        }

        return records
            .OrderBy(record => record.LocalTimestamp)
            .ToArray();
    }

    public IReadOnlyList<CaptureRecord> GetSessionPreviewCaptures(string sessionFolderPath, int maxCount = 12)
    {
        if (string.IsNullOrWhiteSpace(sessionFolderPath) || maxCount <= 0)
        {
            return Array.Empty<CaptureRecord>();
        }

        var capturesRoot = Path.Combine(sessionFolderPath, "captures");
        if (!Directory.Exists(capturesRoot))
        {
            return Array.Empty<CaptureRecord>();
        }

        var records = new List<CaptureRecord>(Math.Min(maxCount, 24));
        var captureDirectories = Directory.EnumerateDirectories(capturesRoot)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var captureDirectory in captureDirectories)
        {
            if (records.Count >= maxCount)
            {
                break;
            }

            var jsonPath = Path.Combine(captureDirectory, "capture.json");
            if (!File.Exists(jsonPath))
            {
                continue;
            }

            var record = TryLoadRecordFromJsonPath(jsonPath, sessionFolderPath, applyLinkedAnnotationOverrides: false);
            if (record is null || !HasValidMediaFile(record))
            {
                continue;
            }

            records.Add(record);
        }

        return records
            .OrderBy(record => record.LocalTimestamp)
            .ToArray();
    }

    private int CountCaptureFolders(string sessionFolder)
    {
        try
        {
            return GetSessionCaptures(sessionFolder).Count(HasValidMediaFile);
        }
        catch
        {
            return 0;
        }
    }

    private CaptureRecord? TryLoadRecordFromJsonPath(string jsonPath, string sessionFolderPath, bool applyLinkedAnnotationOverrides)
    {
        try
        {
            var json = ReadAllTextAllowShared(jsonPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var metadata = JsonSerializer.Deserialize<CaptureMetadata>(json, JsonOptions);
            if (metadata is null)
            {
                return null;
            }

            var folder = Path.GetDirectoryName(jsonPath) ?? string.Empty;
            metadata.CaptureDirectory = folder;
            metadata.JsonPath = jsonPath;
            metadata.CaptureType = NormalizeCaptureType(metadata.CaptureType, folder, metadata);
            metadata.ImagePath = ResolvePath(metadata.ImagePath, folder, "capture.png");
            metadata.VideoPath = ResolvePath(metadata.VideoPath, folder, "capture.mp4");
            metadata.ThumbnailPath = ResolvePath(metadata.ThumbnailPath, folder, "thumbnail.png");
            if (string.Equals(metadata.CaptureType, "video", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(metadata.VideoPath))
                {
                    metadata.VideoPath = Path.Combine(folder, "capture.mp4");
                }

                if (!File.Exists(metadata.ThumbnailPath))
                {
                    var fallbackThumb = Path.Combine(folder, "thumbnail.png");
                    var fallbackPng = Path.Combine(folder, "capture.png");
                    metadata.ThumbnailPath = File.Exists(fallbackThumb)
                        ? fallbackThumb
                        : (File.Exists(fallbackPng) ? fallbackPng : string.Empty);
                }
            }
            else if (!File.Exists(metadata.ImagePath))
            {
                metadata.ImagePath = Path.Combine(folder, "capture.png");
            }

            if (metadata.LocalTimestamp == default)
            {
                metadata.LocalTimestamp = File.GetCreationTime(jsonPath);
            }

            if (metadata.UtcTimestamp == default)
            {
                metadata.UtcTimestamp = File.GetCreationTimeUtc(jsonPath);
            }

            metadata.Note ??= string.Empty;
            metadata.MetadataVersion = metadata.MetadataVersion <= 0 ? CaptureMetadata.CurrentMetadataVersion : metadata.MetadataVersion;
            metadata.ReviewState = ReviewState.Normalize(metadata.ReviewState);
            metadata.ActiveWindowTitle ??= string.Empty;
            metadata.ActiveProcessName ??= string.Empty;
            metadata.Project ??= string.Empty;
            metadata.AssignedUsers ??= new List<string>();
            if (metadata.AssignedUsers.Count == 0 && !string.IsNullOrWhiteSpace(metadata.AssignedUser))
            {
                metadata.AssignedUsers.Add(metadata.AssignedUser);
            }
            metadata.AssignedUsers = CaptureService.NormalizeUsers(metadata.AssignedUsers).ToList();
            metadata.AssignedUser = metadata.AssignedUsers.FirstOrDefault() ?? string.Empty;
            metadata.SessionName ??= string.Empty;
            metadata.SourceJsonPath ??= string.Empty;
            if (!string.IsNullOrWhiteSpace(metadata.SourceJsonPath) && !Path.IsPathRooted(metadata.SourceJsonPath))
            {
                metadata.SourceJsonPath = Path.GetFullPath(Path.Combine(sessionFolderPath, metadata.SourceJsonPath));
            }

            if (applyLinkedAnnotationOverrides)
            {
                ApplyLinkedAnnotationOverrides(metadata);
            }

            return new CaptureRecord { Metadata = metadata };
        }
        catch (Exception ex)
        {
            AppLogService.Warn("Session.Capture.LoadFailed", "Skipping malformed capture metadata.", new
            {
                jsonPath,
                Error = ex.Message
            });
            return null;
        }
    }

    private SessionFolderSummary? TryBuildFallbackSummary(string folder)
    {
        try
        {
            return new SessionFolderSummary
            {
                SessionName = Path.GetFileName(folder),
                Project = string.Empty,
                Users = CollectSessionUsers(folder),
                StartLocal = Directory.GetCreationTime(folder),
                EndLocal = Directory.GetLastWriteTime(folder),
                FolderPath = folder,
                CaptureCount = CountCaptureFolders(folder),
                PreviewImagePath = FindSessionPreviewImagePath(folder),
                ReviewState = ReviewState.None,
                HasRevisedUploads = false
            };
        }
        catch
        {
            return null;
        }
    }

    private static string DetermineSessionReviewState(IReadOnlyCollection<CaptureRecord> records)
    {
        if (records.Count == 0)
        {
            return ReviewState.None;
        }

        var completedCount = records.Count(record =>
            string.Equals(
                ReviewState.Normalize(record.Metadata.ReviewState),
                ReviewState.Completed,
                StringComparison.OrdinalIgnoreCase));
        if (completedCount == records.Count)
        {
            return ReviewState.Completed;
        }

        var inReviewCount = records.Count(record =>
            string.Equals(
                ReviewState.Normalize(record.Metadata.ReviewState),
                ReviewState.InReview,
                StringComparison.OrdinalIgnoreCase));
        return inReviewCount > 0 || completedCount > 0
            ? ReviewState.InReview
            : ReviewState.None;
    }

    private static bool HasUploadedRevisions(CaptureMetadata metadata)
    {
        var uploadedPaths = metadata.UploadedImagePaths ?? new List<string>();
        if (uploadedPaths.Count == 0)
        {
            return false;
        }

        foreach (var uploadedPath in uploadedPaths)
        {
            var normalized = (uploadedPath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            try
            {
                var absolutePath = Path.IsPathRooted(normalized)
                    ? Path.GetFullPath(normalized)
                    : Path.GetFullPath(Path.Combine(metadata.CaptureDirectory ?? string.Empty, normalized.Replace('/', Path.DirectorySeparatorChar)));
                if (File.Exists(absolutePath))
                {
                    return true;
                }
            }
            catch
            {
                // Ignore malformed paths.
            }
        }

        return false;
    }

    private static bool HasValidMediaFile(CaptureRecord record)
    {
        if (record.IsVideo)
        {
            return !string.IsNullOrWhiteSpace(record.VideoPath) && File.Exists(record.VideoPath);
        }

        return !string.IsNullOrWhiteSpace(record.ImagePath) && File.Exists(record.ImagePath);
    }

    private static string ResolvePreviewImageFromCaptureDirectory(string captureDirectory, string sessionFolder)
    {
        if (string.IsNullOrWhiteSpace(captureDirectory))
        {
            return string.Empty;
        }

        var normalized = captureDirectory.Trim();
        if (!Path.IsPathRooted(normalized))
        {
            normalized = Path.GetFullPath(Path.Combine(sessionFolder, normalized));
        }

        if (!Directory.Exists(normalized))
        {
            return string.Empty;
        }

        return FindBestPreviewFileInDirectory(normalized);
    }

    private static string FindSessionPreviewImagePath(string sessionFolder)
    {
        try
        {
            var capturesRoot = Path.Combine(sessionFolder, "captures");
            if (!Directory.Exists(capturesRoot))
            {
                return string.Empty;
            }

            var captureDirectories = Directory.EnumerateDirectories(capturesRoot)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var captureDirectory in captureDirectories)
            {
                var previewFromDirectory = FindBestPreviewFileInDirectory(captureDirectory);
                if (!string.IsNullOrWhiteSpace(previewFromDirectory))
                {
                    return previewFromDirectory;
                }
            }

            string? latestImagePath = null;
            DateTime latestWriteTimeUtc = DateTime.MinValue;
            foreach (var imagePath in Directory.EnumerateFiles(capturesRoot, "*.*", SearchOption.AllDirectories)
                         .Where(path => ImageFileExtensions.Contains(Path.GetExtension(path))))
            {
                try
                {
                    var writeTime = File.GetLastWriteTimeUtc(imagePath);
                    if (writeTime > latestWriteTimeUtc)
                    {
                        latestWriteTimeUtc = writeTime;
                        latestImagePath = imagePath;
                    }
                }
                catch
                {
                    // Ignore inaccessible files.
                }
            }

            return latestImagePath ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FindBestPreviewFileInDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return string.Empty;
        }

        foreach (var fileName in PreferredPreviewFileNames)
        {
            var candidate = Path.Combine(directoryPath, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        try
        {
            return Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path => ImageFileExtensions.Contains(Path.GetExtension(path)))
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .FirstOrDefault() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static List<string> CollectSessionUsers(string sessionFolder)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var capturesRoot = Path.Combine(sessionFolder, "captures");
        if (!Directory.Exists(capturesRoot))
        {
            return new List<string>();
        }

        foreach (var jsonPath in SafeEnumerateCaptureJsonFiles(capturesRoot))
        {
            try
            {
                var json = ReadAllTextAllowShared(jsonPath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    continue;
                }

                var metadata = JsonSerializer.Deserialize<CaptureMetadata>(json, JsonOptions);
                var users = metadata?.AssignedUsers is { Count: > 0 }
                    ? CaptureService.NormalizeUsers(metadata.AssignedUsers)
                    : CaptureService.NormalizeUsers(new[] { metadata?.AssignedUser ?? string.Empty });
                foreach (var user in users)
                {
                    result.Add(user);
                }
            }
            catch
            {
                // Ignore malformed metadata.
            }
        }

        return result.OrderBy(user => user, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? TryReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static int? TryReadInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetInt32(out var number) ? number : null;
    }

    private static DateTime? TryReadDateTime(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTime.TryParse(value.GetString(), out var dateTime) ? dateTime : null;
    }

    private static string ResolvePath(string path, string folder, string fallbackName)
    {
        var normalized = (path ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Path.Combine(folder, fallbackName);
        }

        if (!Path.IsPathRooted(normalized))
        {
            normalized = Path.Combine(folder, normalized);
        }

        return normalized;
    }

    private static string NormalizeCaptureType(string captureType, string folder, CaptureMetadata metadata)
    {
        var normalized = (captureType ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized == "image" || normalized == "video")
        {
            return normalized;
        }

        if (!string.IsNullOrWhiteSpace(metadata.VideoPath) || File.Exists(Path.Combine(folder, "capture.mp4")))
        {
            return "video";
        }

        return "image";
    }

    private static void ApplyLinkedAnnotationOverrides(CaptureMetadata metadata)
    {
        var sourceJsonPath = metadata.SourceJsonPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceJsonPath) || !File.Exists(sourceJsonPath) || IsPendingDeletePath(sourceJsonPath))
        {
            return;
        }

        try
        {
            var sourceJson = ReadAllTextAllowShared(sourceJsonPath);
            if (string.IsNullOrWhiteSpace(sourceJson))
            {
                return;
            }

            var sourceMetadata = JsonSerializer.Deserialize<CaptureMetadata>(sourceJson, JsonOptions);
            if (sourceMetadata is null)
            {
                return;
            }

            metadata.Note = sourceMetadata.Note ?? metadata.Note;
            var assignedUsers = sourceMetadata.AssignedUsers is { Count: > 0 }
                ? CaptureService.NormalizeUsers(sourceMetadata.AssignedUsers).ToList()
                : CaptureService.NormalizeUsers(new[] { sourceMetadata.AssignedUser }).ToList();
            metadata.AssignedUsers = assignedUsers;
            metadata.AssignedUser = assignedUsers.FirstOrDefault() ?? string.Empty;
        }
        catch
        {
            // Keep exported metadata as fallback.
        }
    }

    private static IEnumerable<string> SafeEnumerateCaptureJsonFiles(string capturesRoot)
    {
        if (string.IsNullOrWhiteSpace(capturesRoot) || !Directory.Exists(capturesRoot))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.EnumerateFiles(capturesRoot, "capture.json", SearchOption.AllDirectories)
                .Where(path => !IsPendingDeletePath(path))
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string? ReadAllTextAllowShared(string path)
    {
        const int maxAttempts = 4;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, true);
                return reader.ReadToEnd();
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }

        return null;
    }

    private static bool IsPendingDeletePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalizedPath = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var marker = $"{Path.DirectorySeparatorChar}{DeleteQueueDirectoryName}{Path.DirectorySeparatorChar}";
        return normalizedPath.Contains(marker, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeletePendingFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return false;
        }

        try
        {
            var markerPath = Path.Combine(folderPath, DeletePendingMarkerFileName);
            return File.Exists(markerPath);
        }
        catch
        {
            return false;
        }
    }
}
