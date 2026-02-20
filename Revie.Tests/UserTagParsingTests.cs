using Revie.App.Models;
using Revie.App.Services;
using Revie.App.ViewModels;
using Xunit;

namespace Revie.Tests;

public sealed class UserTagParsingTests
{
    [Fact]
    public void ParseUserTags_TrimsDeduplicatesAndIgnoresEmptyValues()
    {
        var tags = CaptureService.ParseUserTags(" Alice, Bob ; alice , , Bob,Charlie ");

        Assert.Equal(3, tags.Count);
        Assert.Contains("Alice", tags);
        Assert.Contains("Bob", tags);
        Assert.Contains("Charlie", tags);
    }

    [Fact]
    public void CaptureListItem_UserTagsText_UpdatesMetadataAssignedUsers()
    {
        var metadata = new CaptureMetadata
        {
            JsonPath = @"C:\Temp\capture.json",
            CaptureDirectory = @"C:\Temp",
            ImagePath = @"C:\Temp\capture.png",
            AssignedUsers = new List<string> { "InitialUser" },
            AssignedUser = "InitialUser"
        };
        var viewModel = new CaptureListItemViewModel(new CaptureRecord { Metadata = metadata });

        viewModel.UserTagsText = "Alice, Bob, alice";

        Assert.Equal(2, viewModel.AssignedUsers.Count);
        Assert.Contains("Alice", viewModel.AssignedUsers);
        Assert.Contains("Bob", viewModel.AssignedUsers);
        Assert.Contains("Alice", metadata.AssignedUsers);
        Assert.Contains("Bob", metadata.AssignedUsers);
        Assert.Equal("Alice", metadata.AssignedUser);
    }
}
