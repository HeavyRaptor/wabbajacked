using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wabbajack.App.Avalonia.Services.BrowserAutomation;

/// <summary>
///     Automates one supported site in a Playwright browser. Implementations are discovered by
///     <see cref="BrowserAutomationService"/> and called for URLs they claim to handle.
/// </summary>
public interface IBrowserDownloadHandler
{
    /// <summary>Whether this handler can drive the given download page.</summary>
    bool CanHandle(Uri url);

    /// <summary>
    ///     Clicks through the page until the browser has started saving the file, or until it is clear
    ///     the page cannot be automated. Returns true when the download was started, false to fall back
    ///     to opening the page for the user.
    /// </summary>
    Task<bool> DownloadAsync(BrowserDownloadContext context, CancellationToken token);
}
