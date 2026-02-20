using System.Text;
using System.Text.Json;

namespace Revie.App.Services;

public static class AppLogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private static readonly SemaphoreSlim LogGate = new(1, 1);

    public static string LogDirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Revie", "logs");

    public static void Info(string eventName, string message, object? data = null)
    {
        _ = WriteAsync("INFO", eventName, message, data, null);
    }

    public static void Warn(string eventName, string message, object? data = null)
    {
        _ = WriteAsync("WARN", eventName, message, data, null);
    }

    public static void Error(string eventName, string message, Exception? exception = null, object? data = null)
    {
        _ = WriteAsync("ERROR", eventName, message, data, exception);
    }

    private static async Task WriteAsync(string level, string eventName, string message, object? data, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectoryPath);
            var logPath = Path.Combine(LogDirectoryPath, $"revie-{DateTime.Now:yyyyMMdd}.log");

            var entry = new
            {
                TimestampLocal = DateTime.Now.ToString("O"),
                TimestampUtc = DateTime.UtcNow.ToString("O"),
                Level = level,
                Event = eventName,
                Message = message,
                Data = data,
                Exception = exception?.ToString()
            };

            var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;

            await LogGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await File.AppendAllTextAsync(logPath, line, Encoding.UTF8).ConfigureAwait(false);
            }
            finally
            {
                LogGate.Release();
            }
        }
        catch
        {
            // Logging must never throw back into app flow.
        }
    }
}
