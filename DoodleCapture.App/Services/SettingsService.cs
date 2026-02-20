using System.Text.Json;
using DoodleCapture.App.Models;

namespace DoodleCapture.App.Services;

public sealed class SettingsService
{
    private const string SharedFolderName = "Revie";
    private const string SharedTaxonomyFileName = "taxonomy.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string SettingsFilePath { get; }

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        SettingsFilePath = Path.Combine(appData, "Revie", "settings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsFilePath))
        {
            return AppSettings.CreateDefault();
        }

        try
        {
            await using var stream = File.OpenRead(SettingsFilePath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken);
            return settings ?? AppSettings.CreateDefault();
        }
        catch
        {
            return AppSettings.CreateDefault();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await SafeFileWriteService.WriteJsonAtomicAsync(SettingsFilePath, settings, JsonOptions, cancellationToken);
    }

    public async Task<SharedTaxonomySyncResult> SyncSharedTaxonomyAsync(
        string storageRoot,
        IEnumerable<string>? localProjects,
        IEnumerable<string>? localUsers,
        CancellationToken cancellationToken = default)
    {
        var normalizedLocalProjects = NormalizeItems(localProjects);
        var normalizedLocalUsers = NormalizeItems(localUsers);
        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            return new SharedTaxonomySyncResult(
                normalizedLocalProjects,
                normalizedLocalUsers,
                LoadedFromShared: false,
                SavedToShared: false,
                Warning: "Storage root is not configured.");
        }

        var taxonomyPath = GetSharedTaxonomyPath(storageRoot);
        var sharedProjects = new List<string>();
        var sharedUsers = new List<string>();
        var loadedFromShared = false;
        string warning = string.Empty;

        try
        {
            if (File.Exists(taxonomyPath))
            {
                await using var stream = File.OpenRead(taxonomyPath);
                var file = await JsonSerializer.DeserializeAsync<SharedTaxonomyFile>(stream, JsonOptions, cancellationToken);
                if (file is not null)
                {
                    loadedFromShared = true;
                    sharedProjects = NormalizeItems(file.Projects).ToList();
                    sharedUsers = NormalizeItems(file.Users).ToList();
                }
            }
        }
        catch (Exception ex)
        {
            warning = $"Failed reading shared taxonomy: {ex.Message}";
        }

        var mergedProjects = sharedProjects
            .Concat(normalizedLocalProjects)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(project => project, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var mergedUsers = sharedUsers
            .Concat(normalizedLocalUsers)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(user => user, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sharedProjectsChanged = !sharedProjects.SequenceEqual(mergedProjects, StringComparer.OrdinalIgnoreCase);
        var sharedUsersChanged = !sharedUsers.SequenceEqual(mergedUsers, StringComparer.OrdinalIgnoreCase);
        var sharedMissing = !File.Exists(taxonomyPath);
        var shouldSaveShared = sharedMissing || sharedProjectsChanged || sharedUsersChanged;
        var savedToShared = false;

        if (shouldSaveShared)
        {
            try
            {
                var directory = Path.GetDirectoryName(taxonomyPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var payload = new SharedTaxonomyFile
                {
                    Version = 1,
                    UpdatedUtc = DateTime.UtcNow,
                    Projects = mergedProjects,
                    Users = mergedUsers
                };
                await SafeFileWriteService.WriteJsonAtomicAsync(taxonomyPath, payload, JsonOptions, cancellationToken);
                savedToShared = true;
            }
            catch (Exception ex)
            {
                warning = string.IsNullOrWhiteSpace(warning)
                    ? $"Failed writing shared taxonomy: {ex.Message}"
                    : $"{warning} | Failed writing shared taxonomy: {ex.Message}";
            }
        }

        return new SharedTaxonomySyncResult(
            mergedProjects,
            mergedUsers,
            loadedFromShared,
            savedToShared,
            warning);
    }

    public static string GetSharedTaxonomyPath(string storageRoot)
    {
        var root = (storageRoot ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(root))
        {
            return string.Empty;
        }

        return Path.Combine(root, SharedFolderName, SharedTaxonomyFileName);
    }

    private static IReadOnlyList<string> NormalizeItems(IEnumerable<string>? values)
    {
        return (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed class SharedTaxonomyFile
    {
        public int Version { get; set; } = 1;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
        public List<string> Projects { get; set; } = new();
        public List<string> Users { get; set; } = new();
    }
}

public sealed record SharedTaxonomySyncResult(
    IReadOnlyList<string> Projects,
    IReadOnlyList<string> Users,
    bool LoadedFromShared,
    bool SavedToShared,
    string Warning);
