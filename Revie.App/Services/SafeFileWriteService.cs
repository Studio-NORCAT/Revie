using System.Text;
using System.Text.Json;

namespace Revie.App.Services;

public static class SafeFileWriteService
{
    public static void WriteAllTextAtomic(string path, string content)
    {
        WriteAllTextAtomicCoreAsync(path, content, isAsync: false, CancellationToken.None).GetAwaiter().GetResult();
    }

    public static Task WriteAllTextAtomicAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        return WriteAllTextAtomicCoreAsync(path, content, isAsync: true, cancellationToken);
    }

    public static void WriteJsonAtomic<T>(string path, T value, JsonSerializerOptions options)
    {
        var json = JsonSerializer.Serialize(value, options);
        WriteAllTextAtomic(path, json);
    }

    public static Task WriteJsonAtomicAsync<T>(string path, T value, JsonSerializerOptions options, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(value, options);
        return WriteAllTextAtomicAsync(path, json, cancellationToken);
    }

    private static async Task WriteAllTextAtomicCoreAsync(string path, string content, bool isAsync, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        const int maxAttempts = 5;
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tempPath = $"{path}.tmp-{Guid.NewGuid():N}";

            try
            {
                if (isAsync)
                {
                    await File.WriteAllTextAsync(tempPath, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    File.WriteAllText(tempPath, content, Encoding.UTF8);
                }

                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, path, overwrite: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                lastError = ex;
                TryDeleteFile(tempPath);
                if (attempt == maxAttempts)
                {
                    break;
                }

                if (isAsync)
                {
                    await Task.Delay(30 * attempt, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Thread.Sleep(30 * attempt);
                }
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }
        }

        throw new IOException($"Failed to write file '{path}' atomically after retries.", lastError);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort.
        }
    }
}
