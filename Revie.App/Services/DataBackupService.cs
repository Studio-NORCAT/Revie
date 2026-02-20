using System.IO.Compression;

namespace Revie.App.Services;

public static class DataBackupService
{
    private static readonly object Sync = new();
    public static bool Enabled { get; set; } = true;

    public static string BackupRootPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Revie", "backups");

    public static string? TryBackupDirectory(string sourceDirectory, string category, string? nameHint = null)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            return null;
        }

        try
        {
            var categorySafe = SanitizePathPart(category, "general");
            var nameSafe = SanitizePathPart(nameHint, Path.GetFileName(sourceDirectory));
            var targetDirectory = Path.Combine(BackupRootPath, categorySafe);
            Directory.CreateDirectory(targetDirectory);

            var archivePath = Path.Combine(
                targetDirectory,
                $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{nameSafe}.zip");

            lock (Sync)
            {
                ZipFile.CreateFromDirectory(sourceDirectory, archivePath, CompressionLevel.Fastest, includeBaseDirectory: true);
            }

            return archivePath;
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizePathPart(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(normalized.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }
}
