using Revie.App.Models;
using Revie.App.Services;

namespace Revie.Tests;

public sealed class SessionNotificationServiceTests
{
    [Fact]
    public void GetUnviewedCaptureCount_UsesViewedCountDelta()
    {
        var service = new SessionNotificationService();
        var state = new SessionNotificationState();
        var sessionPath = Path.Combine(Path.GetTempPath(), "RevieTests", "SessionA");

        service.MarkSessionViewed(state, sessionPath, 3);

        var unread = service.GetUnviewedCaptureCount(state, sessionPath, 5);
        Assert.Equal(2, unread);
    }

    [Fact]
    public void CountSessionsWithUnviewedCaptures_CountsOnlySessionsWithUnreadDelta()
    {
        var service = new SessionNotificationService();
        var state = new SessionNotificationState();
        var sessionA = Path.Combine(Path.GetTempPath(), "RevieTests", "SessionA");
        var sessionB = Path.Combine(Path.GetTempPath(), "RevieTests", "SessionB");

        service.MarkSessionViewed(state, sessionA, 2);
        service.MarkSessionViewed(state, sessionB, 4);

        var sessions = new[]
        {
            new SessionFolderSummary { FolderPath = sessionA, CaptureCount = 2, SessionName = "A" },
            new SessionFolderSummary { FolderPath = sessionB, CaptureCount = 6, SessionName = "B" }
        };

        var count = service.CountSessionsWithUnviewedCaptures(state, sessions);
        Assert.Equal(1, count);
    }
}
