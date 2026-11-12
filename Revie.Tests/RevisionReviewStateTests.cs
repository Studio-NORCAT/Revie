using Revie.App.Models;

namespace Revie.Tests;

public sealed class RevisionReviewStateTests
{
    [Fact]
    public void PendingRequest_IsOnlyShownToAssignedLocalUser_AndClearsWhenReviewed()
    {
        var requested = new DateTime(2026, 11, 12, 15, 0, 0, DateTimeKind.Utc);
        var metadata = new CaptureMetadata
        {
            UploadedImagePaths = new List<string> { "user-uploads/fix.png" },
            RevisionReviewRequestedForUser = "Lead",
            RevisionReviewRequestedUtc = requested
        };

        Assert.True(RevisionReviewState.IsPendingForUser(metadata, "lead"));
        Assert.False(RevisionReviewState.IsPendingForUser(metadata, "Artist"));
        Assert.False(RevisionReviewState.IsPendingForUser(metadata, ""));

        metadata.RevisionReviewCompletedUtc = requested.AddMinutes(1);
        Assert.False(RevisionReviewState.IsPendingForUser(metadata, "Lead"));

        metadata.RevisionReviewRequestedUtc = requested.AddMinutes(2);
        Assert.True(RevisionReviewState.IsPendingForUser(metadata, "Lead"));
    }
}
