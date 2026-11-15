using System;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Wabbajack.DTOs;
using Wabbajack.Paths;

namespace Wabbajack.App.Avalonia.Services.BrowserAutomation;

/// <summary>
///     The information a <see cref="IBrowserDownloadHandler"/> needs to drive one download, plus a way
///     to report progress back to the UI.
/// </summary>
public sealed class BrowserDownloadContext
{
    public required Archive Archive { get; init; }
    public required Uri Url { get; init; }
    public required AbsolutePath DownloadFolder { get; init; }
    public required IBrowserContext Context { get; init; }
    public ILogger? Logger { get; init; }
    public IProgress<string>? Status { get; init; }
}
