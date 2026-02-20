using System.Text.Json;

namespace Revie.App.Services;

public sealed class SessionNotificationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string NotificationStatePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Revie", "session-notifications.json");

    public SessionNotificationState Load()
    {
        try
        {
            if (!File.Exists(NotificationStatePath))
            {
                return new SessionNotificationState();
            }

            var json = File.ReadAllText(NotificationStatePath);
            var state = JsonSerializer.Deserialize<SessionNotificationState>(json, JsonOptions);
            return state ?? new SessionNotificationState();
        }
        catch
        {
            return new SessionNotificationState();
        }
    }

    public void Save(SessionNotificationState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(NotificationStatePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            SafeFileWriteService.WriteJsonAtomic(NotificationStatePath, state ?? new SessionNotificationState(), JsonOptions);
        }
        catch
        {
            // Notification state should never block app flow.
        }
    }

    public int GetUnviewedCaptureCount(SessionNotificationState state, string sessionFolderPath, int currentCaptureCount)
    {
        var normalizedPath = NormalizeSessionPath(sessionFolderPath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return 0;
        }

        var viewed = 0;
        state?.ViewedCaptureCountBySessionPath?.TryGetValue(normalizedPath, out viewed);
        return Math.Max(0, currentCaptureCount - viewed);
    }

    public int CountSessionsWithUnviewedCaptures(SessionNotificationState state, IEnumerable<Models.SessionFolderSummary> sessions)
    {
        if (sessions is null)
        {
            return 0;
        }

        return sessions.Count(session => GetUnviewedCaptureCount(state, session.FolderPath, session.CaptureCount) > 0);
    }

    public void MarkSessionViewed(SessionNotificationState state, string sessionFolderPath, int captureCount)
    {
        if (state is null)
        {
            return;
        }

        var normalizedPath = NormalizeSessionPath(sessionFolderPath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }

        state.ViewedCaptureCountBySessionPath[normalizedPath] = Math.Max(0, captureCount);
        state.LastUpdatedUtc = DateTime.UtcNow;
    }

    private static string NormalizeSessionPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return (path ?? string.Empty).Trim();
        }
    }
}

public sealed class SessionNotificationState
{
    public Dictionary<string, int> ViewedCaptureCountBySessionPath { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
}
