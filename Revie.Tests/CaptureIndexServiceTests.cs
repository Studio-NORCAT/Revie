using System.Text.Json;
using Revie.App.Models;
using Revie.App.Services;
using Xunit;

namespace Revie.Tests;

public sealed class CaptureIndexServiceTests
{
    [Fact]
    public async Task Manifest_LoadsImmediately_AndReconcilesChangedAndDeletedCaptures()
    {
        var root = CreateTempRoot();
        try
        {
            var firstPath = CreateCaptureJson(root, Path.Combine("2026", "02", "19", "101010_000"), "Alpha");
            var service = new CaptureIndexService();
            await service.ScanAsync(root);

            Assert.Equal("Alpha", Assert.Single(await service.LoadCachedAsync(root)).Metadata.Project);

            var metadata = JsonSerializer.Deserialize<CaptureMetadata>(File.ReadAllText(firstPath))!;
            metadata.Project = "Updated";
            File.WriteAllText(firstPath, JsonSerializer.Serialize(metadata));
            File.SetLastWriteTimeUtc(firstPath, DateTime.UtcNow.AddSeconds(2));
            var secondPath = CreateCaptureJson(root, Path.Combine("2026", "02", "19", "101011_000"), "New");

            var changed = await service.ScanAsync(root);
            Assert.Equal(2, changed.Count);
            Assert.Contains(changed, record => record.Metadata.Project == "Updated");
            Assert.Contains(changed, record => record.Metadata.JsonPath == secondPath);

            File.Delete(firstPath);
            var remaining = await service.ScanAsync(root);
            Assert.Equal(secondPath, Assert.Single(remaining).Metadata.JsonPath);
            Assert.Equal(secondPath, Assert.Single(await service.LoadCachedAsync(root)).Metadata.JsonPath);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ScanAsync_StreamsMoreThanChannelCapacity()
    {
        var root = CreateTempRoot();
        try
        {
            for (var i = 0; i < 160; i++)
                CreateCaptureJson(root, Path.Combine("2026", "02", "19", $"{i:000000}_000"), "Alpha");

            var service = new CaptureIndexService();
            var records = await service.ScanAsync(root).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(160, records.Count);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ScanAsync_ExcludesSessionAndDeleteQueueCaptures()
    {
        var root = CreateTempRoot();
        try
        {
            var activeCapturePath = CreateCaptureJson(root, Path.Combine("2026", "02", "19", "101010_000"), "Alpha");
            _ = CreateCaptureJson(root, Path.Combine("Sessions", "20260219_MySession", "captures", "001_20260219_101011_000"), "Beta");
            _ = CreateCaptureJson(root, Path.Combine(".revie-delete-queue", "pending", "20260219_101012_000"), "Gamma");
            _ = CreateCaptureJson(root, Path.Combine("Projects", "Alpha", ".revie-delete-queue", "pending"), "Queued");

            var service = new CaptureIndexService();
            var records = await service.ScanAsync(root);

            var recordList = records.ToList();
            Assert.Single(recordList);
            Assert.Equal("Alpha", recordList[0].Metadata.Project);
            Assert.Equal(Path.GetFullPath(activeCapturePath), Path.GetFullPath(recordList[0].Metadata.JsonPath));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void BuildProjectRoot_UsesProjectsDirectory_WhenProjectProvided()
    {
        var storageRoot = @"C:\Temp\RevieRoot";

        var noProjectRoot = CaptureService.BuildProjectRoot(storageRoot, string.Empty);
        var projectRoot = CaptureService.BuildProjectRoot(storageRoot, "Alpha");

        Assert.Equal(storageRoot, noProjectRoot);
        Assert.Equal(Path.Combine(storageRoot, "Projects", "Alpha"), projectRoot);
    }

    [Fact]
    public async Task ScanAsync_NormalizesMetadataVersion_AndReviewState()
    {
        var root = CreateTempRoot();
        try
        {
            var captureDir = Path.Combine(root, "2026", "02", "19", "101015_000");
            Directory.CreateDirectory(captureDir);
            var jsonPath = Path.Combine(captureDir, "capture.json");
            File.WriteAllText(
                jsonPath,
                """
                {
                  "CaptureType": "image",
                  "Project": "Alpha",
                  "ReviewState": "unexpected",
                  "Note": "hello",
                  "LocalTimestamp": "2026-02-19T10:10:15",
                  "UtcTimestamp": "2026-02-19T15:10:15Z",
                  "CaptureDirectory": "",
                  "ImagePath": "",
                  "JsonPath": "",
                  "SomeFutureField": "preserve-me"
                }
                """);

            var service = new CaptureIndexService();
            var records = await service.ScanAsync(root);
            var record = Assert.Single(records);

            Assert.Equal(CaptureMetadata.CurrentMetadataVersion, record.Metadata.MetadataVersion);
            Assert.Equal(ReviewState.None, record.Metadata.ReviewState);
            Assert.NotNull(record.Metadata.ExtensionData);
            Assert.True(record.Metadata.ExtensionData!.ContainsKey("SomeFutureField"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static string CreateCaptureJson(string storageRoot, string relativeCaptureDir, string project)
    {
        var captureDir = Path.Combine(storageRoot, relativeCaptureDir);
        Directory.CreateDirectory(captureDir);
        var jsonPath = Path.Combine(captureDir, "capture.json");

        var metadata = new CaptureMetadata
        {
            CaptureType = "image",
            Project = project,
            LocalTimestamp = DateTime.Now,
            UtcTimestamp = DateTime.UtcNow,
            CaptureDirectory = captureDir,
            ImagePath = Path.Combine(captureDir, "capture.png"),
            ThumbnailPath = Path.Combine(captureDir, "capture.png")
        };

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(metadata));
        return jsonPath;
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "RevieTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup in tests.
        }
    }
}
