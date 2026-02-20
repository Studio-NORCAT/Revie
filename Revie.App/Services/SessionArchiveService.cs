using System.Text.Json;
using Revie.App.Models;

namespace Revie.App.Services;

public sealed class SessionArchiveService
{
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
            activeProject), cancellationToken);
    }

    private static string SaveSessionCore(
        string storageRoot,
        string sessionName,
        DateTime sessionStartLocal,
        DateTime sessionStartUtc,
        DateTime sessionEndLocal,
        DateTime sessionEndUtc,
        IReadOnlyList<CaptureRecord> captures,
        string activeProject)
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

        var initialDirectory = Path.Combine(sessionsRoot, $"{sessionStartLocal:yyyyMMdd_HHmmss}_{safeName}");
        var sessionDirectory = EnsureUniqueDirectory(initialDirectory);
        var capturesDirectory = Path.Combine(sessionDirectory, "captures");
        Directory.CreateDirectory(capturesDirectory);

        var orderedCaptures = captures
            .OrderBy(record => record.LocalTimestamp)
            .ToArray();

        var exported = new List<SessionCaptureEntry>(orderedCaptures.Length);
        for (var i = 0; i < orderedCaptures.Length; i++)
        {
            var record = orderedCaptures[i];
            var sourceDirectory = record.Metadata.CaptureDirectory ?? string.Empty;
            var localTime = record.Metadata.LocalTimestamp == default ? DateTime.Now : record.Metadata.LocalTimestamp;
            var targetCaptureDirectory = Path.Combine(capturesDirectory, $"{i + 1:000}_{localTime:yyyyMMdd_HHmmss_fff}");
            var targetImagePath = Path.Combine(targetCaptureDirectory, "capture.png");
            var targetJsonPath = Path.Combine(targetCaptureDirectory, "capture.json");

            if (!string.IsNullOrWhiteSpace(sourceDirectory) && Directory.Exists(sourceDirectory))
            {
                CopyDirectory(sourceDirectory, targetCaptureDirectory);
            }
            else
            {
                Directory.CreateDirectory(targetCaptureDirectory);
                if (File.Exists(record.Metadata.ImagePath))
                {
                    File.Copy(record.Metadata.ImagePath, targetImagePath, overwrite: true);
                }

                if (File.Exists(record.Metadata.JsonPath))
                {
                    File.Copy(record.Metadata.JsonPath, targetJsonPath, overwrite: true);
                }
            }

            WriteSessionCaptureMetadata(
                record.Metadata,
                normalizedName,
                targetCaptureDirectory,
                targetImagePath,
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
                ExportedCaptureDirectory = targetCaptureDirectory
            });
        }

        var manifest = new SessionManifest
        {
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
        return sessionDirectory;
    }

    private static string EnsureUniqueDirectory(string desiredPath)
    {
        if (!Directory.Exists(desiredPath))
        {
            Directory.CreateDirectory(desiredPath);
            return desiredPath;
        }

        var suffix = 1;
        string candidate;
        do
        {
            candidate = $"{desiredPath}_{suffix++:00}";
        } while (Directory.Exists(candidate));

        Directory.CreateDirectory(candidate);
        return candidate;
    }

    private static void CopyDirectory(string sourcePath, string targetPath)
    {
        var sourceDirectory = new DirectoryInfo(sourcePath);
        if (!sourceDirectory.Exists)
        {
            return;
        }

        Directory.CreateDirectory(targetPath);
        foreach (var file in sourceDirectory.GetFiles())
        {
            var destination = Path.Combine(targetPath, file.Name);
            file.CopyTo(destination, overwrite: true);
        }

        foreach (var subDirectory in sourceDirectory.GetDirectories())
        {
            var destination = Path.Combine(targetPath, subDirectory.Name);
            CopyDirectory(subDirectory.FullName, destination);
        }
    }

    private static string ToPathSafeName(string input)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var safe = new string(input.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "Session" : safe;
    }

    private static void WriteSessionCaptureMetadata(
        CaptureMetadata sourceMetadata,
        string sessionName,
        string targetCaptureDirectory,
        string targetImagePath,
        string targetJsonPath)
    {
        CaptureMetadata metadata;
        if (File.Exists(targetJsonPath))
        {
            try
            {
                metadata = JsonSerializer.Deserialize<CaptureMetadata>(File.ReadAllText(targetJsonPath), JsonOptions) ?? new CaptureMetadata();
            }
            catch
            {
                metadata = new CaptureMetadata();
            }
        }
        else
        {
            metadata = new CaptureMetadata();
        }

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
        metadata.CaptureDirectory = targetCaptureDirectory;
        metadata.ImagePath = captureType == "image" ? targetImagePath : string.Empty;
        metadata.VideoPath = captureType == "video" ? Path.Combine(targetCaptureDirectory, "capture.mp4") : string.Empty;
        metadata.ThumbnailPath = captureType == "video"
            ? (File.Exists(Path.Combine(targetCaptureDirectory, "thumbnail.png"))
                ? Path.Combine(targetCaptureDirectory, "thumbnail.png")
                : (File.Exists(targetImagePath) ? targetImagePath : string.Empty))
            : targetImagePath;
        metadata.DurationSeconds = sourceMetadata.DurationSeconds;
        metadata.HasMicrophoneAudio = sourceMetadata.HasMicrophoneAudio;
        metadata.JsonPath = targetJsonPath;
        metadata.VirtualScreenLeft = sourceMetadata.VirtualScreenLeft;
        metadata.VirtualScreenTop = sourceMetadata.VirtualScreenTop;
        metadata.VirtualScreenWidth = sourceMetadata.VirtualScreenWidth;
        metadata.VirtualScreenHeight = sourceMetadata.VirtualScreenHeight;
        metadata.ActiveWindowTitle = sourceMetadata.ActiveWindowTitle ?? string.Empty;
        metadata.ActiveProcessName = sourceMetadata.ActiveProcessName ?? string.Empty;
        metadata.Note = sourceMetadata.Note ?? string.Empty;
        metadata.SourceJsonPath = sourceMetadata.JsonPath ?? string.Empty;
        metadata.Monitors = sourceMetadata.Monitors ?? new List<MonitorSnapshot>();

        SafeFileWriteService.WriteJsonAtomic(targetJsonPath, metadata, JsonOptions);
    }

    private sealed class SessionManifest
    {
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
    }
}
