using System.Text.Json;
using System.Drawing;
using Revie.App.Models;
using Revie.App.Services;

namespace Revie.Tests;

public sealed class SessionArchiveServiceTests
{
    [Fact]
    public async Task SaveSessionAsync_WritesNamedFolderWithSharedScreenshotsAndManifestPaths()
    {
        using var temp = new TempDirectory();
        var sourceCaptureDirectory = Path.Combine(temp.Path, "staging", "2026", "05", "11", "101010_000");
        Directory.CreateDirectory(sourceCaptureDirectory);

        var sourceImagePath = Path.Combine(sourceCaptureDirectory, "capture.png");
        var sourceJsonPath = Path.Combine(sourceCaptureDirectory, "capture.json");
        using (var bitmap = new Bitmap(1920, 1080)) bitmap.Save(sourceImagePath);

        var sourceMetadata = new CaptureMetadata
        {
            MetadataVersion = CaptureMetadata.CurrentMetadataVersion,
            CaptureType = "image",
            Project = "Alpha",
            SessionName = "Review_20260511_1010",
            LocalTimestamp = new DateTime(2026, 5, 11, 10, 10, 10, 123),
            UtcTimestamp = new DateTime(2026, 5, 11, 14, 10, 10, 123, DateTimeKind.Utc),
            StorageRoot = temp.Path,
            CaptureDirectory = sourceCaptureDirectory,
            ImagePath = sourceImagePath,
            ThumbnailPath = sourceImagePath,
            JsonPath = sourceJsonPath
        };
        File.WriteAllText(sourceJsonPath, JsonSerializer.Serialize(sourceMetadata));

        var service = new SessionArchiveService();
        var sessionDirectory = await service.SaveSessionAsync(
            temp.Path,
            "My Review",
            new DateTime(2026, 5, 11, 10, 0, 0),
            new DateTime(2026, 5, 11, 14, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 11, 10, 30, 0),
            new DateTime(2026, 5, 11, 14, 30, 0, DateTimeKind.Utc),
            new[] { new CaptureRecord { Metadata = sourceMetadata }, new CaptureRecord { Metadata = sourceMetadata } },
            "Alpha");

        var capturesRoot = Path.Combine(sessionDirectory, "captures");
        var screenshotsRoot = Path.Combine(sessionDirectory, "screenshots");
        var flatImagePath = Path.Combine(screenshotsRoot, "001_20260511_101010_123.png");
        var flatJsonPath = Path.Combine(capturesRoot, "001_20260511_101010_123.json");

        Assert.Equal("My Review", Path.GetFileName(sessionDirectory));
        Assert.True(File.Exists(flatImagePath));
        Assert.Equal(2, Directory.GetFiles(screenshotsRoot, "*.png").Length);
        Assert.True(File.Exists(flatJsonPath));
        var flatThumbnailPath = Path.Combine(sessionDirectory, "thumbnails", "001_20260511_101010_123.jpg");
        Assert.True(File.Exists(flatThumbnailPath));
        using (var thumbnail = Image.FromFile(flatThumbnailPath)) Assert.True(thumbnail.Width <= 320);
        Assert.Empty(Directory.EnumerateDirectories(screenshotsRoot));
        Assert.Empty(Directory.EnumerateDirectories(capturesRoot));

        using var manifestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(sessionDirectory, "session.json")));
        Assert.Equal(2, manifestDocument.RootElement.GetProperty("LayoutVersion").GetInt32());
        var manifestCapture = manifestDocument.RootElement.GetProperty("Captures")[0];
        Assert.Equal("captures/001_20260511_101010_123.json", manifestCapture.GetProperty("ExportedMetadataPath").GetString());
        Assert.Equal("screenshots/001_20260511_101010_123.png", manifestCapture.GetProperty("ExportedMediaPath").GetString());
        Assert.Equal("thumbnails/001_20260511_101010_123.jpg", manifestCapture.GetProperty("ExportedThumbnailPath").GetString());

        using var metadataDocument = JsonDocument.Parse(await File.ReadAllTextAsync(flatJsonPath));
        Assert.Equal("../screenshots/001_20260511_101010_123.png", metadataDocument.RootElement.GetProperty("ImagePath").GetString());
    }

    [Fact]
    public async Task SaveSessionAsync_ReusesNameWithSuffix_AndDoesNotPublishMissingMedia()
    {
        using var temp = new TempDirectory();
        var service = new SessionArchiveService();
        var start = new DateTime(2026, 5, 11, 10, 0, 0);
        var first = await service.SaveSessionAsync(temp.Path, "Review", start, start.ToUniversalTime(), start, start.ToUniversalTime(), Array.Empty<CaptureRecord>(), "Alpha");
        var second = await service.SaveSessionAsync(temp.Path, "Review", start, start.ToUniversalTime(), start, start.ToUniversalTime(), Array.Empty<CaptureRecord>(), "Alpha");
        Assert.Equal("Review", Path.GetFileName(first));
        Assert.Equal("Review_01", Path.GetFileName(second));

        var missingMediaDirectory = Path.Combine(temp.Path, "staging", "missing");
        Directory.CreateDirectory(missingMediaDirectory);
        var missingMedia = new CaptureRecord { Metadata = new CaptureMetadata
        {
            CaptureType = "image",
            CaptureDirectory = missingMediaDirectory,
            ImagePath = Path.Combine(missingMediaDirectory, "capture.png")
        } };
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.SaveSessionAsync(
            temp.Path, "Incomplete", start, start.ToUniversalTime(), start, start.ToUniversalTime(), new[] { missingMedia }, "Alpha"));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "Sessions", "Incomplete")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(temp.Path, "Sessions"), ".revie-saving-*"));
    }

    [Fact]
    public void CleanupStaleTemporarySessions_OnlyRemovesOldIncompleteArchives()
    {
        using var temp = new TempDirectory();
        var sessionsRoot = Path.Combine(temp.Path, "Sessions");
        var stale = Path.Combine(sessionsRoot, ".revie-saving-stale");
        var current = Path.Combine(sessionsRoot, ".revie-saving-current");
        var named = Path.Combine(sessionsRoot, "Review");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(named);
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-8));
        Directory.SetLastWriteTimeUtc(named, DateTime.UtcNow.AddDays(-8));

        SessionArchiveService.CleanupStaleTemporarySessions(temp.Path);

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(current));
        Assert.True(Directory.Exists(named));
    }

    [Fact]
    public async Task SaveSessionAsync_PlacesVideoAndThumbnailOutsideScreenshots()
    {
        using var temp = new TempDirectory();
        var sourceFolder = Path.Combine(temp.Path, "staging", "video");
        Directory.CreateDirectory(sourceFolder);
        var videoPath = Path.Combine(sourceFolder, "capture.mp4");
        var thumbnailPath = Path.Combine(sourceFolder, "thumbnail.png");
        File.WriteAllText(videoPath, "video placeholder");
        File.WriteAllText(thumbnailPath, "thumbnail placeholder");
        var timestamp = new DateTime(2026, 5, 11, 10, 10, 10, 123);
        var record = new CaptureRecord { Metadata = new CaptureMetadata
        {
            CaptureType = "video",
            CaptureDirectory = sourceFolder,
            VideoPath = videoPath,
            ThumbnailPath = thumbnailPath,
            LocalTimestamp = timestamp
        } };

        var folder = await new SessionArchiveService().SaveSessionAsync(
            temp.Path, "Video Review", timestamp, timestamp.ToUniversalTime(), timestamp, timestamp.ToUniversalTime(), new[] { record }, "Alpha");

        Assert.True(File.Exists(Path.Combine(folder, "videos", "001_20260511_101010_123.mp4")));
        Assert.True(File.Exists(Path.Combine(folder, "thumbnails", "001_20260511_101010_123.png")));
        Assert.False(Directory.Exists(Path.Combine(folder, "screenshots")));
        var savedRecord = Assert.Single(new SessionBrowseService().GetSessionCaptures(folder));
        Assert.True(savedRecord.IsVideo);
        Assert.True(File.Exists(savedRecord.VideoPath));
    }

    [Fact]
    public async Task SessionBrowseService_ReadsFlatSessionCaptureFiles()
    {
        using var temp = new TempDirectory();
        var sourceCaptureDirectory = Path.Combine(temp.Path, "staging", "2026", "05", "11", "101010_000");
        Directory.CreateDirectory(sourceCaptureDirectory);

        var sourceImagePath = Path.Combine(sourceCaptureDirectory, "capture.png");
        var sourceJsonPath = Path.Combine(sourceCaptureDirectory, "capture.json");
        File.WriteAllText(sourceImagePath, "image");

        var sourceMetadata = new CaptureMetadata
        {
            MetadataVersion = CaptureMetadata.CurrentMetadataVersion,
            CaptureType = "image",
            Project = "Bravo",
            AssignedUser = "Casey",
            AssignedUsers = new List<string> { "Casey" },
            SessionName = "Review_20260511_1010",
            LocalTimestamp = new DateTime(2026, 5, 11, 10, 10, 10, 123),
            UtcTimestamp = new DateTime(2026, 5, 11, 14, 10, 10, 123, DateTimeKind.Utc),
            StorageRoot = temp.Path,
            CaptureDirectory = sourceCaptureDirectory,
            ImagePath = sourceImagePath,
            ThumbnailPath = sourceImagePath,
            JsonPath = sourceJsonPath
        };
        File.WriteAllText(sourceJsonPath, JsonSerializer.Serialize(sourceMetadata));

        var archiveService = new SessionArchiveService();
        var sessionDirectory = await archiveService.SaveSessionAsync(
            temp.Path,
            "My Review",
            new DateTime(2026, 5, 11, 10, 0, 0),
            new DateTime(2026, 5, 11, 14, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 11, 10, 30, 0),
            new DateTime(2026, 5, 11, 14, 30, 0, DateTimeKind.Utc),
            new[] { new CaptureRecord { Metadata = sourceMetadata } },
            "Bravo");

        var browseService = new SessionBrowseService();
        var captures = browseService.GetSessionCaptures(sessionDirectory);
        var capture = Assert.Single(captures);

        Assert.Equal("Bravo", capture.Metadata.Project);
        Assert.Equal("Casey", capture.Metadata.AssignedUser);
        Assert.True(File.Exists(capture.ImagePath));
        Assert.EndsWith("001_20260511_101010_123.json", capture.Metadata.JsonPath, StringComparison.OrdinalIgnoreCase);

        var uploadsFolder = Path.Combine(capture.CaptureDirectory, "user-uploads");
        Directory.CreateDirectory(uploadsFolder);
        File.WriteAllText(Path.Combine(uploadsFolder, "fix.png"), "revision");
        var storedMetadata = JsonSerializer.Deserialize<CaptureMetadata>(await File.ReadAllTextAsync(capture.Metadata.JsonPath))!;
        storedMetadata.CaptureDirectory = capture.CaptureDirectory;
        storedMetadata.ImagePath = capture.ImagePath;
        storedMetadata.UploadedImagePaths = new List<string> { "user-uploads/fix.png" };
        storedMetadata.RevisionReviewRequestedForUser = "Lead";
        storedMetadata.RevisionReviewRequestedUtc = new DateTime(2026, 11, 12, 15, 0, 0, DateTimeKind.Utc);
        await File.WriteAllTextAsync(capture.Metadata.JsonPath, JsonSerializer.Serialize(storedMetadata));
        var movedDirectory = Path.Combine(temp.Path, "Sessions", "Renamed Review");
        Directory.Move(sessionDirectory, movedDirectory);
        var movedCapture = Assert.Single(browseService.GetSessionCaptures(movedDirectory));
        Assert.Equal(Path.Combine(movedDirectory, "screenshots", "001_20260511_101010_123.png"), movedCapture.ImagePath);
        Assert.True(File.Exists(Path.Combine(movedCapture.CaptureDirectory, "user-uploads", "fix.png")));
        Assert.True(RevisionReviewState.IsPendingForUser(movedCapture.Metadata, "Lead"));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RevieTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup for temp paths.
            }
        }
    }
}
