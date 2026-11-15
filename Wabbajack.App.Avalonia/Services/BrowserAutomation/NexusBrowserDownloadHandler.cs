using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Wabbajack.Paths.IO;

namespace Wabbajack.App.Avalonia.Services.BrowserAutomation;

/// <summary>
///     Automates the Nexus Mods slow-download flow in an Edge page. Clicks the Slow download button inside
///     the &lt;mod-file-download&gt; custom element's shadow root, then handles any follow-up modal, and closes
///     the tab as soon as the download has started.
/// </summary>
public sealed class NexusBrowserDownloadHandler : IBrowserDownloadHandler
{
    private readonly BrowserAutomationOptions _options;
    private readonly ILogger<NexusBrowserDownloadHandler>? _logger;

    private static readonly TimeSpan FindTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(2);

    public NexusBrowserDownloadHandler(BrowserAutomationOptions options,
        ILogger<NexusBrowserDownloadHandler>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public bool CanHandle(Uri url)
    {
        return url.Host.Equals("www.nexusmods.com", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> DownloadAsync(BrowserDownloadContext context, CancellationToken token)
    {
        context.Status?.Report($"Opening Nexus page for {context.Archive.Name}");
        _logger?.LogInformation("Automating Nexus download for {Archive} at {Url}", context.Archive.Name,
            context.Url);

        context.DownloadFolder.CreateDirectory();

        var page = await context.Context.NewPageAsync();
        IDownload? download = null;
        try
        {
            var downloadTask = page.WaitForDownloadAsync(new PageWaitForDownloadOptions
            {
                Timeout = (float) DownloadTimeout.TotalMilliseconds
            });

            await page.GotoAsync(context.Url.ToString(), new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded
            });

            // The popup URL now redirects to the mod page, so resolve the actual file id from the
            // page we ended up on.
            var fileId = ParseFileId(page.Url) ?? ParseFileId(context.Url.ToString());

            if (downloadTask.IsCompleted)
            {
                download = await downloadTask;
            }
            else
            {
                // Dismiss any endorsement / login / cookie reminders that might block the download button.
                await DismissPopupAsync(page, context, token);

                // 1. Slow download button inside the correct <mod-file-download> shadow root.
                await DelayBeforeClickAsync(token);
                context.Status?.Report($"Looking for Slow download for {context.Archive.Name}");
                var slow = await FindElementByTextAsync(page, fileId, "slow download", token);
                if (slow == null)
                {
                    _logger?.LogWarning("Slow download button not found for {Archive}", context.Archive.Name);
                    context.Status?.Report($"Slow download button not found for {context.Archive.Name}");
                    return false;
                }

                context.Status?.Report($"Clicking Slow download for {context.Archive.Name}");
                await slow.ClickAsync();

                // Dismiss any endorsement reminders that may have appeared after the first click.
                await DismissPopupAsync(page, context, token);

                // 2. Standard download button in the modal that appears for some files.
                // Search globally because the dialog may be rendered in a portal outside the custom element.
                await DelayBeforeClickAsync(token);
                context.Status?.Report($"Looking for Standard download for {context.Archive.Name}");
                var standard = await FindElementByTextAsync(page, null, "standard download", token);
                if (standard != null)
                {
                    context.Status?.Report($"Clicking Standard download for {context.Archive.Name}");
                    await standard.ClickAsync();
                }

                // 3. Wait for the browser to start the download; if it does not, click the manual fallback.
                download = await WaitForDownloadOrClickManualAsync(page, fileId, downloadTask, context, token);
                if (download == null)
                {
                    _logger?.LogWarning("Download did not start for {Archive}", context.Archive.Name);
                    context.Status?.Report($"Download did not start for {context.Archive.Name}");
                    return false;
                }
            }

            context.Status?.Report($"Download started for {context.Archive.Name}; finishing in background");
            return true;
        }
        finally
        {
            try
            {
                await page.CloseAsync();
            }
            catch
            {
                // Best effort.
            }

            // Move/copy the file to the Wabbajack watch folder after the tab is closed so tabs do not pile up.
            if (download != null)
            {
                try
                {
                    await SaveDownloadAsync(download, context);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Could not save download for {Archive}; the acquirer may still pick it up from Edge's download folder", context.Archive.Name);
                    context.Status?.Report($"Downloaded {context.Archive.Name}; copying to watch folder failed");
                }
            }
        }
    }

    private async Task<IDownload?> WaitForDownloadOrClickManualAsync(IPage page, string? fileId,
        Task<IDownload> downloadTask,
        BrowserDownloadContext context, CancellationToken token)
    {
        var completed = await Task.WhenAny(downloadTask, Task.Delay(TimeSpan.FromSeconds(3), token));
        if (completed == downloadTask)
            return await downloadTask;

        await DismissPopupAsync(page, context, token);

        await DelayBeforeClickAsync(token);
        context.Status?.Report($"Looking for manual fallback for {context.Archive.Name}");
        var manual = await FindElementByTextAsync(page, null, "start download manually", token);
        if (manual == null)
        {
            _logger?.LogWarning("Start download manually link not found for {Archive}", context.Archive.Name);
            context.Status?.Report($"Manual fallback not found for {context.Archive.Name}");
            return null;
        }

        context.Status?.Report($"Starting download manually for {context.Archive.Name}");
        var manualDownloadTask = page.WaitForDownloadAsync(new PageWaitForDownloadOptions
        {
            Timeout = (float) DownloadTimeout.TotalMilliseconds
        });
        await manual.ClickAsync();

        return await manualDownloadTask.WaitAsync(token);
    }

    private static async Task SaveDownloadAsync(IDownload download, BrowserDownloadContext context)
    {
        var suggested = download.SuggestedFilename;
        var dest = context.DownloadFolder.Combine(suggested);
        await download.SaveAsAsync(dest.ToString());
        context.Status?.Report($"Saved {suggested} for {context.Archive.Name}");
    }

    private async Task DelayBeforeClickAsync(CancellationToken token)
    {
        var ms = Random.Shared.Next(
            (int) _options.MinDelay.TotalMilliseconds,
            (int) _options.MaxDelay.TotalMilliseconds + 1);
        await Task.Delay(ms, token);
    }

    private static string? ParseFileId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var query = uri.Query.TrimStart('?');
        foreach (var part in query.Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2) continue;
            var key = Uri.UnescapeDataString(pair[0]);
            if (key.Equals("file_id", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("id", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[1]);
        }
        return null;
    }

    /// <summary>
    ///     Dismisses common Nexus reminders (endorsement, login, cookie banners, etc.) by clicking their
    ///     close button. Returns true only if a popup was actually dismissed.
    /// </summary>
    private static async Task<bool> DismissPopupAsync(IPage page, BrowserDownloadContext context,
        CancellationToken token)
    {
        try
        {
            var close = await page.EvaluateHandleAsync(@"
                () => {
                    // Magnific Popup close button.
                    const mfp = document.querySelector('.mfp-close');
                    if (mfp) return mfp;
                    // Generic title-based close buttons.
                    for (const el of document.querySelectorAll('button, [role=""button""]')) {
                        const title = (el.getAttribute('title') || '').toLowerCase();
                        const aria = (el.getAttribute('aria-label') || '').toLowerCase();
                        if (title.includes('close') || aria.includes('close')) return el;
                    }
                    return null;
                }");

            if (close is IElementHandle element && await element.IsVisibleAsync())
            {
                context.Status?.Report("Dismissing popup");
                await element.ClickAsync();
                await Task.Delay(500, token);
                return true;
            }
        }
        catch
        {
            // Best effort; do not let popup dismissal fail the download.
        }

        return false;
    }

    /// <summary>
    ///     Finds the first visible clickable element whose text contains the supplied phrase.
    ///     When fileId is supplied, the search is scoped to the &lt;mod-file-download&gt; element with that
    ///     file id; otherwise the whole document and all shadow roots are searched.
    /// </summary>
    private static async Task<IElementHandle?> FindElementByTextAsync(IPage page, string? fileId, string text,
        CancellationToken token)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < FindTimeout)
        {
            token.ThrowIfCancellationRequested();

            var handle = await page.EvaluateHandleAsync(@"
                (args) => {
                    const fileId = args.fileId;
                    const text = args.text;
                    const matches = [];

                    function isClickable(el) {
                        const tag = el.tagName.toLowerCase();
                        const role = (el.getAttribute('role') || '').toLowerCase();
                        return tag === 'button' || tag === 'a' || role === 'button';
                    }

                    function walk(root) {
                        if (!root) return;
                        for (const el of root.querySelectorAll('*')) {
                            if (el.shadowRoot) walk(el.shadowRoot);
                            if (!isClickable(el)) continue;
                            const t = (el.textContent || '').trim().replace(/\s+/g, ' ');
                            if (t.toLowerCase().includes(text.toLowerCase())) matches.push(el);
                        }
                    }

                    if (fileId) {
                        const host = document.querySelector('mod-file-download[file-id=""' + fileId + '""], mod-file-download[file_id=""' + fileId + '""], mod-file-download[fileid=""' + fileId + '""]')
                                  || document.querySelector('mod-file-download');
                        if (host && host.shadowRoot) {
                            walk(host.shadowRoot);
                        } else {
                            walk(document.body);
                        }
                    } else {
                        walk(document.body);
                    }

                    return matches[0] || null;
                }", new { fileId, text });

            if (handle is IElementHandle element)
            {
                try
                {
                    var visible = await element.IsVisibleAsync();
                    if (visible) return element;
                }
                catch
                {
                    // Element may have been detached; keep polling.
                }
            }

            await Task.Delay(500, token);
        }

        return null;
    }
}
