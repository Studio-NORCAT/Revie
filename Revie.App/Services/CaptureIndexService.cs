using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Revie.App.Models;

namespace Revie.App.Services;

public sealed class CaptureIndexService
{
    private const string DeleteQueueDirectoryName = ".revie-delete-queue";
    private const string SessionsDirectoryName = "Sessions";
    private static readonly SemaphoreSlim ScanSemaphore = new(1, 1);
    private static readonly EnumerationOptions DirectoryEnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class CaptureIndexManifest
    {
        public int Version { get; set; } = 1;
        public string StorageRoot { get; set; } = string.Empty;
        public List<CaptureIndexEntry> Entries { get; set; } = new();
    }

    private sealed class CaptureIndexEntry
    {
        public string JsonPath { get; set; } = string.Empty;
        public long LastWriteTicks { get; set; }
        public long Length { get; set; }
        public long SourceLastWriteTicks { get; set; }
        public CaptureMetadata Metadata { get; set; } = new();
    }

    public Task<IReadOnlyList<CaptureRecord>> LoadCachedAsync(string storageRoot, CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<CaptureRecord>>(() =>
        {
            var manifest = ReadManifest(storageRoot);
            return (manifest?.Entries ?? new List<CaptureIndexEntry>())
                .Where(entry => entry is not null && entry.Metadata is not null && !string.IsNullOrWhiteSpace(entry.JsonPath) &&
                                !IsPendingDeletePath(entry.JsonPath) && !IsSessionArchivePath(entry.JsonPath, storageRoot))
                .GroupBy(entry => entry.JsonPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .Select(entry => new CaptureRecord
                {
                    Metadata = entry.Metadata,
                    IndexMetadataWriteTicks = entry.LastWriteTicks,
                    IndexSourceWriteTicks = entry.SourceLastWriteTicks
                })
                .OrderByDescending(record => record.LocalTimestamp)
                .ToArray();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<CaptureRecord>> ScanAsync(string storageRoot, CancellationToken cancellationToken = default)
    {
        var records = new List<CaptureRecord>();
        await foreach (var record in ScanStreamAsync(storageRoot, cancellationToken).ConfigureAwait(false))
        {
            records.Add(record);
        }

        return records
            .OrderByDescending(record => record.LocalTimestamp)
            .ToArray();
    }

    public async IAsyncEnumerable<CaptureRecord> ScanStreamAsync(
        string storageRoot,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            yield break;
        }

        var channel = Channel.CreateBounded<CaptureRecord>(new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        _ = Task.Run(async () =>
        {
            var acquired = false;
            try
            {
                await ScanSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired = true;
                if (!Directory.Exists(storageRoot))
                {
                    channel.Writer.TryComplete();
                    return;
                }
                List<string> jsonPaths;
                try
                {
                    jsonPaths = EnumerateCaptureMetadataPaths(storageRoot, cancellationToken)
                        .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                    return;
                }

                var cachedEntries = (ReadManifest(storageRoot)?.Entries ?? new List<CaptureIndexEntry>())
                    .Where(entry => entry is not null && entry.Metadata is not null && !string.IsNullOrWhiteSpace(entry.JsonPath))
                    .GroupBy(entry => entry.JsonPath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
                var newEntries = new List<CaptureIndexEntry>(jsonPaths.Count);
                var linkedSourceCache = new Dictionary<string, LinkedAnnotationOverrides>(StringComparer.OrdinalIgnoreCase);
                foreach (var jsonPath in jsonPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long ticks;
                    long length;
                    try
                    {
                        var file = new FileInfo(jsonPath);
                        ticks = file.LastWriteTimeUtc.Ticks;
                        length = file.Length;
                    }
                    catch { continue; }
                    cachedEntries.TryGetValue(jsonPath, out var cached);
                    var sourceTicks = cached is null ? 0 : GetLinkedSourceTicks(cached.Metadata.SourceJsonPath);
                    var record = cached is not null && cached.LastWriteTicks == ticks && cached.Length == length && cached.SourceLastWriteTicks == sourceTicks
                        ? new CaptureRecord { Metadata = cached.Metadata }
                        : TryBuildRecord(jsonPath, storageRoot, linkedSourceCache, cancellationToken);
                    if (record is null)
                    {
                        continue;
                    }

                    record.IndexMetadataWriteTicks = ticks;
                    record.IndexSourceWriteTicks = GetLinkedSourceTicks(record.Metadata.SourceJsonPath);
                    newEntries.Add(new CaptureIndexEntry
                    {
                        JsonPath = jsonPath,
                        LastWriteTicks = ticks,
                        Length = length,
                        SourceLastWriteTicks = record.IndexSourceWriteTicks,
                        Metadata = record.Metadata
                    });
                    await channel.Writer.WriteAsync(record, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    await SafeFileWriteService.WriteJsonAtomicAsync(GetManifestPath(storageRoot), new CaptureIndexManifest
                    {
                        StorageRoot = Path.GetFullPath(storageRoot),
                        Entries = newEntries
                    }, JsonOptions, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLogService.Warn("Capture.Index.SaveFailed", "Capture index cache could not be saved.", new { ex.Message });
                }
                channel.Writer.TryComplete();
            }
            catch (OperationCanceledException ex)
            {
                channel.Writer.TryComplete(ex);
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
            finally
            {
                if (acquired) ScanSemaphore.Release();
            }
        });

        await foreach (var record in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return record;
        }
    }

    private static IEnumerable<string> EnumerateCaptureMetadataPaths(string storageRoot, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Path, bool IsRoot)>();
        pending.Push((storageRoot, true));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, isRoot) = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> children;
            try
            {
                files = Directory.EnumerateFiles(directory, "capture.json", SearchOption.TopDirectoryOnly).ToArray();
                children = Directory.EnumerateDirectories(directory, "*", DirectoryEnumerationOptions).ToArray();
            }
            catch (DirectoryNotFoundException)
            {
                // A capture may have been moved or deleted during the scan.
                continue;
            }

            foreach (var file in files) yield return file;
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (string.Equals(name, DeleteQueueDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                    (isRoot && string.Equals(name, SessionsDirectoryName, StringComparison.OrdinalIgnoreCase)))
                    continue;
                pending.Push((child, false));
            }
        }
    }

    private static long GetLinkedSourceTicks(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
        }
        catch { return 0; }
    }

    private static CaptureIndexManifest? ReadManifest(string storageRoot)
    {
        try
        {
            var path = GetManifestPath(storageRoot);
            if (!File.Exists(path)) return null;
            var manifest = JsonSerializer.Deserialize<CaptureIndexManifest>(File.ReadAllText(path), JsonOptions);
            return manifest?.Version == 1 &&
                   string.Equals(manifest.StorageRoot, Path.GetFullPath(storageRoot), StringComparison.OrdinalIgnoreCase)
                ? manifest : null;
        }
        catch
        {
            return null;
        }
    }

    private static string GetManifestPath(string storageRoot)
    {
        var normalized = Path.GetFullPath(storageRoot.Trim()).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Revie", "indexes", $"{hash}.json");
    }

    private static CaptureRecord? TryBuildRecord(
        string jsonPath,
        string storageRoot,
        Dictionary<string, LinkedAnnotationOverrides> linkedSourceCache,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = ReadAllTextAllowShared(jsonPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var metadata = JsonSerializer.Deserialize<CaptureMetadata>(json, JsonOptions);
            if (metadata is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(metadata.CaptureDirectory))
            {
                metadata.CaptureDirectory = Path.GetDirectoryName(jsonPath) ?? string.Empty;
            }
            else
            {
                metadata.CaptureDirectory = metadata.CaptureDirectory.Trim();
                if (!Path.IsPathRooted(metadata.CaptureDirectory))
                {
                    metadata.CaptureDirectory = Path.GetFullPath(Path.Combine(storageRoot, metadata.CaptureDirectory));
                }
            }

            var captureDirectory = Path.GetDirectoryName(jsonPath) ?? string.Empty;
            metadata.CaptureType = NormalizeCaptureType(metadata.CaptureType, metadata, captureDirectory);
            metadata.ImagePath = ResolvePath(metadata.ImagePath, metadata.CaptureDirectory, "capture.png");
            metadata.VideoPath = ResolvePath(metadata.VideoPath, metadata.CaptureDirectory, "capture.mp4");
            metadata.ThumbnailPath = ResolvePath(metadata.ThumbnailPath, metadata.CaptureDirectory, "thumbnail.png");
            if (string.Equals(metadata.CaptureType, "video", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(metadata.VideoPath))
                {
                    var fallbackVideoPath = Path.Combine(captureDirectory, "capture.mp4");
                    metadata.VideoPath = File.Exists(fallbackVideoPath) ? fallbackVideoPath : metadata.VideoPath;
                }

                if (!File.Exists(metadata.ThumbnailPath))
                {
                    var fallbackThumb = Path.Combine(captureDirectory, "thumbnail.png");
                    var fallbackPng = Path.Combine(captureDirectory, "capture.png");
                    metadata.ThumbnailPath = File.Exists(fallbackThumb)
                        ? fallbackThumb
                        : (File.Exists(fallbackPng) ? fallbackPng : string.Empty);
                }
            }
            else
            {
                if (!File.Exists(metadata.ImagePath))
                {
                    var fallbackImagePath = Path.Combine(captureDirectory, "capture.png");
                    metadata.ImagePath = File.Exists(fallbackImagePath) ? fallbackImagePath : metadata.ImagePath;
                }

                var sidebarPreviewPath = Path.Combine(captureDirectory, SidebarThumbnailService.FileName);
                metadata.ThumbnailPath = File.Exists(sidebarPreviewPath) ? sidebarPreviewPath : metadata.ImagePath;
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
            metadata.AssignedUser ??= string.Empty;
            metadata.AssignedUsers ??= new List<string>();
            if (metadata.AssignedUsers.Count == 0 && !string.IsNullOrWhiteSpace(metadata.AssignedUser))
            {
                metadata.AssignedUsers.Add(metadata.AssignedUser);
            }

            metadata.AssignedUsers = CaptureService.NormalizeUsers(metadata.AssignedUsers).ToList();
            metadata.AssignedUser = metadata.AssignedUsers.FirstOrDefault() ?? string.Empty;
            metadata.SessionName ??= string.Empty;
            metadata.StorageRoot ??= storageRoot;
            metadata.SourceJsonPath ??= string.Empty;
            metadata.AssignedUser = CaptureService.NormalizeUser(metadata.AssignedUser);
            if (!string.IsNullOrWhiteSpace(metadata.SourceJsonPath) && !Path.IsPathRooted(metadata.SourceJsonPath))
            {
                metadata.SourceJsonPath = Path.GetFullPath(Path.Combine(storageRoot, metadata.SourceJsonPath));
            }

            ApplyLinkedAnnotationOverrides(metadata, linkedSourceCache, cancellationToken);

            metadata.JsonPath = jsonPath;
            return new CaptureRecord { Metadata = metadata };
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyLinkedAnnotationOverrides(
        CaptureMetadata metadata,
        Dictionary<string, LinkedAnnotationOverrides> linkedSourceCache,
        CancellationToken cancellationToken)
    {
        var sourceJsonPath = metadata.SourceJsonPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceJsonPath) || !File.Exists(sourceJsonPath) || IsPendingDeletePath(sourceJsonPath))
        {
            return;
        }

        try
        {
            if (!linkedSourceCache.TryGetValue(sourceJsonPath, out var linked))
            {
                var sourceJson = ReadAllTextAllowShared(sourceJsonPath, cancellationToken);
                if (string.IsNullOrWhiteSpace(sourceJson))
                {
                    return;
                }

                var sourceMetadata = JsonSerializer.Deserialize<CaptureMetadata>(sourceJson, JsonOptions);
                if (sourceMetadata is null)
                {
                    return;
                }

                var sourceUsers = sourceMetadata.AssignedUsers is { Count: > 0 }
                    ? CaptureService.NormalizeUsers(sourceMetadata.AssignedUsers).ToList()
                    : CaptureService.NormalizeUsers(new[] { sourceMetadata.AssignedUser }).ToList();
                linked = new LinkedAnnotationOverrides
                {
                    Note = sourceMetadata.Note ?? string.Empty,
                    AssignedUsers = sourceUsers
                };
                linkedSourceCache[sourceJsonPath] = linked;
            }

            metadata.Note = linked.Note;
            metadata.AssignedUsers = linked.AssignedUsers.ToList();
            metadata.AssignedUser = metadata.AssignedUsers.FirstOrDefault() ?? string.Empty;
        }
        catch
        {
            // Keep scanned metadata if source cannot be read.
        }
    }

    private static string? ReadAllTextAllowShared(string path, CancellationToken cancellationToken)
    {
        const int maxAttempts = 4;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private static bool IsSessionArchivePath(string path, string storageRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(storageRoot))
        {
            return false;
        }

        try
        {
            var normalizedStorageRoot = Path.GetFullPath(storageRoot.Trim());
            var sessionRoot = Path.Combine(normalizedStorageRoot, SessionsDirectoryName);
            var normalizedPath = Path.GetFullPath(path.Trim());
            var marker = sessionRoot.EndsWith(Path.DirectorySeparatorChar)
                ? sessionRoot
                : $"{sessionRoot}{Path.DirectorySeparatorChar}";
            return normalizedPath.StartsWith(marker, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string ResolvePath(string path, string captureDirectory, string fallbackFileName)
    {
        var normalized = (path ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Path.Combine(captureDirectory, fallbackFileName);
        }

        if (!Path.IsPathRooted(normalized))
        {
            normalized = Path.Combine(captureDirectory, normalized);
        }

        return normalized;
    }

    private static string NormalizeCaptureType(string captureType, CaptureMetadata metadata, string captureDirectory)
    {
        var normalizedType = (captureType ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedType == "video" || normalizedType == "image")
        {
            return normalizedType;
        }

        if (!string.IsNullOrWhiteSpace(metadata.VideoPath) || File.Exists(Path.Combine(captureDirectory, "capture.mp4")))
        {
            return "video";
        }

        return "image";
    }

    private sealed class LinkedAnnotationOverrides
    {
        public string Note { get; init; } = string.Empty;
        public List<string> AssignedUsers { get; init; } = new();
    }
}
