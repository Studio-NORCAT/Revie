using DoodleCapture.App.Services;
using DoodleCapture.App.ViewModels;
using Xunit;

namespace Revie.Tests;

public sealed class MainViewModelProjectSyncTests
{
    [StaFact]
    public void ChangingCurrentProject_UpdatesLatestFilter_AndRaisesCaptureProjectChange()
    {
        var viewModel = CreateViewModel();
        viewModel.ApplyTaxonomySettings(new[] { "Alpha", "Beta" }, "Alpha", new[] { "UserA", "UserB" }, "UserA");

        string? raisedProject = null;
        viewModel.ActiveCaptureProjectChanged += project => raisedProject = project;

        viewModel.ActiveCaptureProjectChoice = "Beta";

        Assert.Equal("Beta", viewModel.ActiveCaptureProjectChoice);
        Assert.Equal("Beta", viewModel.LatestProjectFilterChoice);
        Assert.Equal("Beta", raisedProject);
    }

    [StaFact]
    public void ChangingCurrentProjectToNoProject_UpdatesLatestFilterToNoProject()
    {
        var viewModel = CreateViewModel();
        viewModel.ApplyTaxonomySettings(new[] { "Alpha" }, "Alpha", new[] { "UserA" }, "UserA");

        string? raisedProject = "not-empty";
        viewModel.ActiveCaptureProjectChanged += project => raisedProject = project;

        viewModel.ActiveCaptureProjectChoice = "(No Project)";

        Assert.Equal("(No Project)", viewModel.LatestProjectFilterChoice);
        Assert.Equal(string.Empty, raisedProject);
    }

    [StaFact]
    public void ChangingCurrentUser_UpdatesLatestUserFilter_AndRaisesCaptureUserChange()
    {
        var viewModel = CreateViewModel();
        viewModel.ApplyTaxonomySettings(new[] { "Alpha" }, "Alpha", new[] { "UserA", "UserB" }, "UserA");

        string? raisedUser = null;
        viewModel.ActiveCaptureUserChanged += user => raisedUser = user;

        viewModel.ActiveCaptureUserChoice = "UserB";

        Assert.Equal("UserB", viewModel.ActiveCaptureUserChoice);
        Assert.Equal("UserB", viewModel.LatestUserFilterChoice);
        Assert.Equal("UserB", raisedUser);
    }

    private static MainViewModel CreateViewModel()
    {
        return new MainViewModel(new CaptureIndexService(), () => @"C:\Temp\RevieTests");
    }
}
