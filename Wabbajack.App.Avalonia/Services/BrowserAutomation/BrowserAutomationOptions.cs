using System;

namespace Wabbajack.App.Avalonia.Services.BrowserAutomation;

/// <summary>Configurable behavior for the Edge browser automation layer.</summary>
public sealed class BrowserAutomationOptions
{
    public int MaxConcurrency { get; init; } = 5;
    public TimeSpan MinDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(7);
}
