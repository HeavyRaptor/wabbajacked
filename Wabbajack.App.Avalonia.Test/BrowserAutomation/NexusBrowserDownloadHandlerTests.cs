using System;
using Wabbajack.App.Avalonia.Services.BrowserAutomation;
using Xunit;

namespace Wabbajack.App.Avalonia.Test.BrowserAutomation;

public class NexusBrowserDownloadHandlerTests
{
    [Fact]
    public void CanHandle_MatchesNexusModUrls()
    {
        var handler = new NexusBrowserDownloadHandler(new BrowserAutomationOptions());
        Assert.True(handler.CanHandle(new Uri("https://www.nexusmods.com/Core/Libs/Common/Widgets/DownloadPopUp?id=123&game_id=3333")));
        Assert.True(handler.CanHandle(new Uri("https://www.nexusmods.com/skyrimspecialedition/mods/123?tab=files")));
    }

    [Fact]
    public void CanHandle_DoesNotMatchOtherSites()
    {
        var handler = new NexusBrowserDownloadHandler(new BrowserAutomationOptions());
        Assert.False(handler.CanHandle(new Uri("https://www.loverslab.com/files/file/123")));
        Assert.False(handler.CanHandle(new Uri("https://drive.google.com/uc?id=abc")));
    }
}
