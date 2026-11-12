using System.Text.Json;
using System.Reflection;
using Revie.App.Models;
using Revie.App.Services;
using Revie.App.ViewModels;

namespace Revie.Tests;

public sealed class MainViewModelMetadataSaveTests
{
    [StaFact]
    public async Task AssigningProjectMovesCaptureAndPreservesNote()
    {
        var root = Path.Combine(Path.GetTempPath(), "RevieTests", Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "2026", "02", "19", "101010_000");
        Directory.CreateDirectory(directory);
        try
        {
            var jsonPath = Path.Combine(directory, "capture.json");
            var imagePath = Path.Combine(directory, "capture.png");
            File.WriteAllText(imagePath, "image");
            var metadata = new CaptureMetadata
            {
                CaptureDirectory = directory,
                JsonPath = jsonPath,
                ImagePath = imagePath,
                Project = "Alpha",
                Note = "Keep this note",
                LocalTimestamp = new DateTime(2026, 2, 19, 10, 10, 10),
                UtcTimestamp = new DateTime(2026, 2, 19, 15, 10, 10, DateTimeKind.Utc)
            };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(metadata));
            var viewModel = new MainViewModel(new CaptureIndexService(), () => root);
            viewModel.ApplyTaxonomySettings(new[] { "Alpha", "Beta" }, "Alpha", Array.Empty<string>(), string.Empty);
            viewModel.AddCapture(new CaptureRecord { Metadata = metadata });
            viewModel.SelectedProjectChoice = "Beta";

            var method = typeof(MainViewModel).GetMethod("AssignSelectedCaptureToProjectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)method.Invoke(viewModel, null)!;

            var movedJsonPath = viewModel.SelectedCapture!.Record.Metadata.JsonPath;
            Assert.False(Directory.Exists(directory));
            Assert.Contains(Path.Combine("Projects", "Beta"), movedJsonPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(movedJsonPath));
            Assert.Equal("Keep this note", JsonSerializer.Deserialize<CaptureMetadata>(File.ReadAllText(movedJsonPath))!.Note);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [StaFact]
    public async Task ConcurrentNoteSavesKeepLatestValue_AndDeletedCaptureIsNotRecreated()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RevieTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var jsonPath = Path.Combine(directory, "capture.json");
            var metadata = new CaptureMetadata
            {
                CaptureDirectory = directory,
                JsonPath = jsonPath,
                LocalTimestamp = DateTime.Now,
                UtcTimestamp = DateTime.UtcNow
            };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(metadata));
            var capture = new CaptureListItemViewModel(new CaptureRecord { Metadata = metadata });
            var viewModel = new MainViewModel(new CaptureIndexService(), () => directory);

            capture.Note = "first";
            var firstSave = viewModel.PersistCaptureMetadataAsync(capture);
            capture.Note = "second";
            var secondSave = viewModel.PersistCaptureMetadataAsync(capture);
            Assert.True(await firstSave);
            Assert.True(await secondSave);
            Assert.Equal("second", JsonSerializer.Deserialize<CaptureMetadata>(File.ReadAllText(jsonPath))!.Note);

            File.Delete(jsonPath);
            capture.Note = "after removal";
            Assert.False(await viewModel.PersistCaptureMetadataAsync(capture));
            Assert.False(File.Exists(jsonPath));

            capture.MarkDeleted();
            Assert.True(await viewModel.PersistCaptureMetadataAsync(capture));
            Assert.False(File.Exists(jsonPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
