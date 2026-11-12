using System.Text.Json;
using System.Drawing;
using System.Globalization;
using System.Windows.Media;
using Revie.App.Infrastructure;
using Revie.App.Models;
using Revie.App.Services;

namespace Revie.Tests;

public sealed class SessionBrowseServiceTests
{
    [Fact]
    public void GetSessions_FlagsPendingRevisionReviewerWithExistingUpload()
    {
        using var temp = new TempDirectory();
        var sessionFolder = Path.Combine(temp.Path, "Sessions", "Asset Review");
        var captureFolder = Path.Combine(sessionFolder, "captures", "asset-01");
        var uploadsFolder = Path.Combine(captureFolder, "user-uploads");
        Directory.CreateDirectory(uploadsFolder);
        File.WriteAllText(Path.Combine(captureFolder, "capture.png"), "original");
        File.WriteAllText(Path.Combine(uploadsFolder, "fix.png"), "revision");
        var metadata = JsonSerializer.Deserialize<CaptureMetadata>(BuildCaptureJson(captureFolder, "Assets"))!;
        metadata.UploadedImagePaths = new List<string> { "user-uploads/fix.png" };
        metadata.RevisionReviewRequestedForUser = "Lead";
        metadata.RevisionReviewRequestedUtc = new DateTime(2026, 11, 12, 15, 0, 0, DateTimeKind.Utc);
        var jsonPath = Path.Combine(captureFolder, "capture.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(metadata));

        var service = new SessionBrowseService();
        var pending = Assert.Single(service.GetSessions(temp.Path));
        pending.ActiveUser = "lead";
        Assert.True(pending.HasRevisedUploads);
        Assert.True(pending.HasPendingRevisionReviewForActiveUser);
        pending.ActiveUser = "Artist";
        Assert.False(pending.HasPendingRevisionReviewForActiveUser);

        metadata.RevisionReviewCompletedUtc = metadata.RevisionReviewRequestedUtc.AddMinutes(1);
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(metadata));
        var reviewed = Assert.Single(service.GetSessions(temp.Path));
        reviewed.ActiveUser = "Lead";
        Assert.False(reviewed.HasPendingRevisionReviewForActiveUser);
    }

    [Fact]
    public void GetSessionCaptures_ReadsLegacyFlatCapturesWithoutManifest()
    {
        using var temp = new TempDirectory();
        var sessionFolder = Path.Combine(temp.Path, "Sessions", "20260220_Flat");
        var capturesFolder = Path.Combine(sessionFolder, "captures");
        Directory.CreateDirectory(capturesFolder);
        var imagePath = Path.Combine(capturesFolder, "001_capture.png");
        File.WriteAllText(imagePath, "placeholder");
        var metadata = JsonSerializer.Deserialize<CaptureMetadata>(BuildCaptureJson(capturesFolder, "Legacy"))!;
        metadata.ImagePath = imagePath;
        metadata.ThumbnailPath = imagePath;
        File.WriteAllText(Path.Combine(capturesFolder, "001_capture.json"), JsonSerializer.Serialize(metadata));

        var capture = Assert.Single(new SessionBrowseService().GetSessionCaptures(sessionFolder));
        Assert.Equal(imagePath, capture.ImagePath);
        Assert.Equal("Legacy", capture.Metadata.Project);
        Assert.Equal(1, Assert.Single(new SessionBrowseService().GetSessions(temp.Path)).CaptureCount);
    }

    [Fact]
    public void GetSessions_LegacyFolderUsesLoadedCaptureForUsersAndPreview()
    {
        using var temp = new TempDirectory();
        var sessionFolder = Path.Combine(temp.Path, "Sessions", "20260220_Legacy");
        var captureFolder = Path.Combine(sessionFolder, "captures", "20260220_101010_000");
        Directory.CreateDirectory(captureFolder);
        File.WriteAllText(Path.Combine(captureFolder, "capture.png"), "placeholder");
        var previewPath = Path.Combine(captureFolder, "sidebar-preview.jpg");
        File.WriteAllText(previewPath, "preview");
        var metadata = JsonSerializer.Deserialize<CaptureMetadata>(BuildCaptureJson(captureFolder, "Alpha"))!;
        metadata.AssignedUser = "Casey";
        metadata.AssignedUsers = new List<string> { "Casey" };
        metadata.ThumbnailPath = previewPath;
        File.WriteAllText(Path.Combine(captureFolder, "capture.json"), JsonSerializer.Serialize(metadata));

        var session = Assert.Single(new SessionBrowseService().GetSessions(temp.Path));
        Assert.Contains("Casey", session.Users);
        Assert.Equal(previewPath, session.PreviewImagePath);
    }

    [Fact]
    public void GetSessions_LegacyManifestPrefersExistingSidebarPreviewOverFullScreenshot()
    {
        using var temp = new TempDirectory();
        var sessionFolder = Path.Combine(temp.Path, "Sessions", "Legacy Review");
        var captureFolder = Path.Combine(sessionFolder, "captures", "001_capture");
        Directory.CreateDirectory(captureFolder);
        var screenshotPath = Path.Combine(captureFolder, "capture.png");
        var previewPath = Path.Combine(captureFolder, SidebarThumbnailService.FileName);
        using (var screenshot = new Bitmap(640, 360)) screenshot.Save(screenshotPath);
        SidebarThumbnailService.EnsureThumbnail(screenshotPath, captureFolder);
        var metadata = JsonSerializer.Deserialize<CaptureMetadata>(BuildCaptureJson(captureFolder, "Legacy"))!;
        metadata.ThumbnailPath = screenshotPath;
        File.WriteAllText(Path.Combine(captureFolder, "capture.json"), JsonSerializer.Serialize(metadata));
        File.WriteAllText(Path.Combine(sessionFolder, "session.json"), JsonSerializer.Serialize(new
        {
            SessionName = "Legacy Review",
            Captures = new[] { new { ExportedCaptureDirectory = "captures/001_capture", ExportedThumbnailPath = "old/missing.jpg" } }
        }));

        var session = Assert.Single(new SessionBrowseService().GetSessions(temp.Path));
        Assert.Equal(previewPath, session.PreviewImagePath);
        Assert.Equal(screenshotPath, session.FallbackPreviewImagePath);
        var decoded = new SafeImagePathConverter().Convert(session.PreviewImagePath, typeof(ImageSource), 320, CultureInfo.InvariantCulture);
        Assert.True(SafeImagePathConverter.IsUsableImage(decoded as ImageSource));
    }

    [Fact]
    public void GetSessions_CachesSummaries_AndReconcilesRemovedSessions()
    {
        using var temp = new TempDirectory();
        var sessionFolder = Path.Combine(temp.Path, "Sessions", "20260220_Cached");
        var captureFolder = Path.Combine(sessionFolder, "captures", "20260220_101010_000");
        Directory.CreateDirectory(captureFolder);
        File.WriteAllText(Path.Combine(captureFolder, "capture.png"), "placeholder");
        File.WriteAllText(Path.Combine(captureFolder, "capture.json"), BuildCaptureJson(captureFolder, "Alpha"));

        var service = new SessionBrowseService();
        Assert.Single(service.GetSessions(temp.Path));
        Assert.Equal(sessionFolder, Assert.Single(service.LoadCachedSessions(temp.Path)).FolderPath);

        var disconnectedPath = temp.Path + "-disconnected";
        Directory.Move(temp.Path, disconnectedPath);
        try
        {
            Assert.Empty(service.GetSessions(temp.Path));
            Assert.Single(service.LoadCachedSessions(temp.Path));
        }
        finally
        {
            Directory.Move(disconnectedPath, temp.Path);
        }

        Directory.Delete(sessionFolder, recursive: true);
        Assert.Empty(service.GetSessions(temp.Path));
        Assert.Empty(service.LoadCachedSessions(temp.Path));
    }

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
