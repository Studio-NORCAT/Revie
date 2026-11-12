namespace Revie.App.Models;

public static class RevisionReviewState
{
    public static bool IsPending(CaptureMetadata metadata) =>
        !string.IsNullOrWhiteSpace(metadata.RevisionReviewRequestedForUser) &&
        metadata.RevisionReviewRequestedUtc > metadata.RevisionReviewCompletedUtc &&
        metadata.UploadedImagePaths is { Count: > 0 };

    public static bool IsPendingForUser(CaptureMetadata metadata, string? activeUser) =>
        IsPending(metadata) &&
        !string.IsNullOrWhiteSpace(activeUser) &&
        string.Equals(metadata.RevisionReviewRequestedForUser.Trim(), activeUser.Trim(), StringComparison.OrdinalIgnoreCase);
}
