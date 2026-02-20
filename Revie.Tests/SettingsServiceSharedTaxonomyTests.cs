using System.Text.Json;
using DoodleCapture.App.Services;
using Xunit;

namespace Revie.Tests;

public sealed class SettingsServiceSharedTaxonomyTests
{
    [Fact]
    public async Task SyncSharedTaxonomyAsync_CreatesSharedFile_WhenMissing()
    {
        using var temp = new TempDirectory();
        var storageRoot = temp.Path;
        var service = new SettingsService();

        var result = await service.SyncSharedTaxonomyAsync(
            storageRoot,
            new[] { "Project Alpha" },
            new[] { "User One" });

        Assert.Single(result.Projects);
        Assert.Single(result.Users);
        Assert.Equal("Project Alpha", result.Projects[0]);
        Assert.Equal("User One", result.Users[0]);
        Assert.True(result.SavedToShared);
        Assert.True(string.IsNullOrWhiteSpace(result.Warning));

        var sharedPath = SettingsService.GetSharedTaxonomyPath(storageRoot);
        Assert.True(File.Exists(sharedPath));
    }

    [Fact]
    public async Task SyncSharedTaxonomyAsync_MergesLocalAndSharedWithoutDuplicates()
    {
        using var temp = new TempDirectory();
        var storageRoot = temp.Path;
        var sharedPath = SettingsService.GetSharedTaxonomyPath(storageRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(sharedPath)!);

        var initialShared = """
            {
              "version": 1,
              "updatedUtc": "2026-01-01T00:00:00Z",
              "projects": [ "Shared A", "Shared B" ],
              "users": [ "Shared User" ]
            }
            """;
        await File.WriteAllTextAsync(sharedPath, initialShared);

        var service = new SettingsService();
        var result = await service.SyncSharedTaxonomyAsync(
            storageRoot,
            new[] { "Local C", "Shared A" },
            new[] { "Local User", "Shared User" });

        Assert.Equal(3, result.Projects.Count);
        Assert.Contains("Shared A", result.Projects);
        Assert.Contains("Shared B", result.Projects);
        Assert.Contains("Local C", result.Projects);
        Assert.Equal(2, result.Users.Count);
        Assert.Contains("Shared User", result.Users);
        Assert.Contains("Local User", result.Users);
        Assert.True(result.LoadedFromShared);
        Assert.True(result.SavedToShared);
        Assert.True(string.IsNullOrWhiteSpace(result.Warning));

        var persistedJson = await File.ReadAllTextAsync(sharedPath);
        using var document = JsonDocument.Parse(persistedJson);
        var persistedProjects = document.RootElement.GetProperty("Projects").EnumerateArray().Select(element => element.GetString()).ToList();
        var persistedUsers = document.RootElement.GetProperty("Users").EnumerateArray().Select(element => element.GetString()).ToList();

        Assert.Contains("Local C", persistedProjects);
        Assert.Contains("Shared B", persistedProjects);
        Assert.Contains("Local User", persistedUsers);
        Assert.Contains("Shared User", persistedUsers);
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
                // Best-effort cleanup for test temp paths.
            }
        }
    }
}
