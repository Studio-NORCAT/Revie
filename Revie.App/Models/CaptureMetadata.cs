using System.Text.Json;
using System.Text.Json.Serialization;

namespace Revie.App.Models;

public sealed class CaptureMetadata
{
    public const int CurrentMetadataVersion = 1;

    public int MetadataVersion { get; set; } = CurrentMetadataVersion;
    public string CaptureType { get; set; } = "image";
    public string ReviewState { get; set; } = Revie.App.Models.ReviewState.None;
    public string Project { get; set; } = string.Empty;
    public string AssignedUser { get; set; } = string.Empty;
    public List<string> AssignedUsers { get; set; } = new();
    public string SessionName { get; set; } = string.Empty;
    public DateTime LocalTimestamp { get; set; }
    public DateTime UtcTimestamp { get; set; }
    public string StorageRoot { get; set; } = string.Empty;
    public string CaptureDirectory { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public string VideoPath { get; set; } = string.Empty;
    public string ThumbnailPath { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public bool HasMicrophoneAudio { get; set; }
    public string JsonPath { get; set; } = string.Empty;
    public int VirtualScreenLeft { get; set; }
    public int VirtualScreenTop { get; set; }
    public int VirtualScreenWidth { get; set; }
    public int VirtualScreenHeight { get; set; }
    public string ActiveWindowTitle { get; set; } = string.Empty;
    public string ActiveProcessName { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string SourceJsonPath { get; set; } = string.Empty;
    public List<string> UploadedImagePaths { get; set; } = new();
    public string RevisionReviewRequestedForUser { get; set; } = string.Empty;
    public DateTime RevisionReviewRequestedUtc { get; set; }
    public DateTime RevisionReviewCompletedUtc { get; set; }
    public List<MonitorSnapshot> Monitors { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
