namespace Revie.App.Models;

public sealed class CaptureRecord
{
    public required CaptureMetadata Metadata { get; init; }

    public string ImagePath => Metadata.ImagePath ?? string.Empty;
    public string VideoPath => Metadata.VideoPath ?? string.Empty;
    public string ThumbnailPath => Metadata.ThumbnailPath ?? string.Empty;
    public bool IsVideo => string.Equals(Metadata.CaptureType, "video", StringComparison.OrdinalIgnoreCase);
    public string CaptureDirectory => Metadata.CaptureDirectory ?? string.Empty;
    public DateTime LocalTimestamp => Metadata.LocalTimestamp;
}
