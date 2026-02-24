using System.Text.Json;
using Revie.App.Models;
using Revie.App.Services;

namespace Revie.Tests;

public sealed class SessionBrowseServiceTests
{
    [Fact]
    public void GetSessions_ReturnsFallbackSummary_WhenManifestIsMalformed()
    {
        using var temp = new TempDirectory();
        var sessionFolder = Path.Combine(temp.Path, "Sessions", "20260220_BrokenSession");
        var captureFolder = Path.Combine(sessionFolder, "captures", "20260220_101010_000");
        Directory.CreateDirectory(captureFolder);

        File.WriteAllText(Path.Combine(sessionFolder, "session.json"), "{not valid json");
        File.WriteAllText(Path.Combine(captureFolder, "capture.png"), "placeholder");
        File.WriteAllText(Path.Combine(captureFolder, "capture.json"), BuildCaptureJson(captureFolder, "Alpha"));

        var service = new SessionBrowseService();
        var sessions = service.GetSessions(temp.Path);

        var session = Assert.Single(sessions);
        Assert.Equal("20260220_BrokenSession", session.SessionName);
        Assert.Equal(1, session.CaptureCount);
        Assert.Equal(sessionFolder, session.FolderPath);
    }

    [Fact]
    public void GetSessionPreviewCaptures_SkipsMalformedCaptureFiles()
    {
        using var temp = new TempDirectory();
        var sessionFolder = Path.Combine(temp.Path, "Sessions", "20260220_SessionA");
        var capturesRoot = Path.Combine(sessionFolder, "captures");
        var goodCaptureFolder = Path.Combine(capturesRoot, "20260220_101010_000");
        var badCaptureFolder = Path.Combine(capturesRoot, "20260220_101011_000");
        Directory.CreateDirectory(goodCaptureFolder);
        Directory.CreateDirectory(badCaptureFolder);

        File.WriteAllText(Path.Combine(goodCaptureFolder, "capture.png"), "placeholder");
        File.WriteAllText(Path.Combine(goodCaptureFolder, "capture.json"), BuildCaptureJson(goodCaptureFolder, "Bravo"));
        File.WriteAllText(Path.Combine(badCaptureFolder, "capture.json"), "{bad");

        var service = new SessionBrowseService();
        var captures = service.GetSessionPreviewCaptures(sessionFolder, 12);

        var capture = Assert.Single(captures);
        Assert.Equal("Bravo", capture.Metadata.Project);
    }

    [Fact]
    public void GetSessions_SkipsOrphanedEmptySessionFolders()
    {
        using var temp = new TempDirectory();
        var sessionsRoot = Path.Combine(temp.Path, "Sessions");
        var emptySessionFolder = Path.Combine(sessionsRoot, "20260220_Empty");
        var validSessionFolder = Path.Combine(sessionsRoot, "20260220_Valid");
        var validCaptureFolder = Path.Combine(validSessionFolder, "captures", "20260220_101010_000");

        Directory.CreateDirectory(emptySessionFolder);
        Directory.CreateDirectory(validCaptureFolder);
        File.WriteAllText(Path.Combine(validCaptureFolder, "capture.png"), "placeholder");
        File.WriteAllText(Path.Combine(validCaptureFolder, "capture.json"), BuildCaptureJson(validCaptureFolder, "Bravo"));

        var service = new SessionBrowseService();
        var sessions = service.GetSessions(temp.Path);

        var session = Assert.Single(sessions);
        Assert.Equal("20260220_Valid", session.SessionName);
        Assert.Equal(1, session.CaptureCount);
    }

    [Fact]
    public void GetSessions_SkipsSessionsWithOnlyEmptyCaptureDirectories()
    {
        using var temp = new TempDirectory();
        var sessionFolder = Path.Combine(temp.Path, "Sessions", "20260220_StaleDelete");
        var emptyCaptureFolder = Path.Combine(sessionFolder, "captures", "20260220_101010_000");
        Directory.CreateDirectory(emptyCaptureFolder);

        var service = new SessionBrowseService();
        var sessions = service.GetSessions(temp.Path);

        Assert.Empty(sessions);
    }

    private static string BuildCaptureJson(string captureFolder, string project)
    {
        var metadata = new CaptureMetadata
        {
            MetadataVersion = CaptureMetadata.CurrentMetadataVersion,
            CaptureType = "image",
            Project = project,
            LocalTimestamp = new DateTime(2026, 2, 20, 10, 10, 10),
            UtcTimestamp = new DateTime(2026, 2, 20, 15, 10, 10, DateTimeKind.Utc),
            CaptureDirectory = captureFolder,
            ImagePath = Path.Combine(captureFolder, "capture.png"),
            ThumbnailPath = Path.Combine(captureFolder, "capture.png"),
            JsonPath = Path.Combine(captureFolder, "capture.json")
        };

        return JsonSerializer.Serialize(metadata);
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
