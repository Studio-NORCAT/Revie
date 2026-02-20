using DoodleCapture.App.Models;
using DoodleCapture.App.ViewModels;
using Xunit;

namespace Revie.Tests;

public sealed class CaptureReviewStateTests
{
    [Fact]
    public void CaptureListItemViewModel_ReviewState_NormalizesAndUpdatesFlags()
    {
        var metadata = new CaptureMetadata
        {
            CaptureType = "image",
            LocalTimestamp = DateTime.Now,
            UtcTimestamp = DateTime.UtcNow,
            CaptureDirectory = @"C:\Temp\Revie\Tests\Capture",
            ImagePath = @"C:\Temp\Revie\Tests\Capture\capture.png",
            JsonPath = @"C:\Temp\Revie\Tests\Capture\capture.json",
            ReviewState = "inreview"
        };
        var vm = new CaptureListItemViewModel(new CaptureRecord { Metadata = metadata });

        Assert.True(vm.IsInReview);
        Assert.False(vm.IsCompleted);
        Assert.Equal("In Review", vm.ReviewStateDisplay);

        vm.ReviewState = "Completed";
        Assert.False(vm.IsInReview);
        Assert.True(vm.IsCompleted);
        Assert.Equal(ReviewState.Completed, vm.Record.Metadata.ReviewState);

        vm.ReviewState = "unknown";
        Assert.False(vm.IsInReview);
        Assert.False(vm.IsCompleted);
        Assert.Equal(ReviewState.None, vm.Record.Metadata.ReviewState);
    }
}
