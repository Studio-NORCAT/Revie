using System.Text.Json;
using Revie.App.Models;
using Revie.App.Services;

namespace Revie.Tests;

public sealed class SessionArchiveServiceTests
{
    [Fact]
    public async Task SaveSessionAsync_WritesFlatCaptureFiles_AndManifestPaths()
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
            new[] { new CaptureRecord { Metadata = sourceMetadata } },
            "Alpha");

        var capturesRoot = Path.Combine(sessionDirectory, "captures");
        var flatImagePath = Path.Combine(capturesRoot, "001_20260511_101010_123.png");
        var flatJsonPath = Path.Combine(capturesRoot, "001_20260511_101010_123.json");

        Assert.True(File.Exists(flatImagePath));
        Assert.True(File.Exists(flatJsonPath));
        Assert.Empty(Directory.EnumerateDirectories(capturesRoot));

        using var manifestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(sessionDirectory, "session.json")));
        var manifestCapture = manifestDocument.RootElement.GetProperty("Captures")[0];
        Assert.Equal("captures/001_20260511_101010_123.json", manifestCapture.GetProperty("ExportedMetadataPath").GetString());
        Assert.Equal("captures/001_20260511_101010_123.png", manifestCapture.GetProperty("ExportedMediaPath").GetString());
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
