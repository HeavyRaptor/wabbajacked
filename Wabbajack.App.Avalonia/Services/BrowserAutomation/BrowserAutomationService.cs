using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Wabbajack.DTOs;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;

namespace Wabbajack.App.Avalonia.Services.BrowserAutomation;

/// <summary>
///     Drives the user's own Edge browser (via Chrome DevTools Protocol) to click through manual download
///     pages. Because it is a real browser with the user's real profile, it is not detected as automation
///     by Cloudflare/Nexus.
/// </summary>
public sealed class BrowserAutomationService : IAsyncDisposable
{
    private static readonly string[] EdgePaths = new[]
    {
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
    };

    private const string CdpEndpoint = "http://localhost:9222";
    private const string RemoteDebuggingArg = "--remote-debugging-port=9222";

    private readonly ILogger<BrowserAutomationService> _logger;
    private readonly BrowserAutomationOptions _options;
    private readonly IEnumerable<IBrowserDownloadHandler> _handlers;
    private readonly Channel<WorkItem> _queue;
    private readonly List<Task> _workers = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Subject<string> _status = new();
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _browserSemaphore = new(1, 1);

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private Process? _edgeProcess;
    private bool _started;
    private bool _disposed;

    public BrowserAutomationService(ILogger<BrowserAutomationService> logger,
        BrowserAutomationOptions options, IEnumerable<IBrowserDownloadHandler> handlers)
    {
        _logger = logger;
        _options = options;
        _handlers = handlers;
        _queue = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = false });
    }

    public IObservable<string> Status => _status.AsObservable();

    /// <summary>Starts the worker pool lazily. Safe to call multiple times.</summary>
    public void EnsureStarted()
    {
        lock (_workers)
        {
            if (_started || _disposed) return;
            _started = true;
            for (var i = 0; i < _options.MaxConcurrency; i++)
                _workers.Add(Task.Run(() => WorkerLoop(_cts.Token)));
        }
    }

    /// <summary>Queues one archive for automation if it is not already queued.</summary>
    public void Enqueue(Archive archive, Uri url, AbsolutePath downloadFolder)
    {
        EnsureStarted();
        if (_disposed) return;
        if (!_inFlight.Add(archive.Name)) return;
        _queue.Writer.TryWrite(new WorkItem(archive, url, downloadFolder));
    }

    /// <summary>Reports a status message to subscribers, typically the manual-download card.</summary>
    public void ReportStatus(string message)
    {
        _status.OnNext(message);
    }

    private async Task WorkerLoop(CancellationToken token)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(token))
            {
                await EnsureBrowserAsync(token);
                await ProcessOneAsync(item, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Browser automation worker failed");
        }
    }

    private async Task ProcessOneAsync(WorkItem item, CancellationToken token)
    {
        var handler = _handlers.FirstOrDefault(h => h.CanHandle(item.Url));
        if (handler == null || _context == null)
        {
            _logger.LogWarning("No browser handler for {Url}", item.Url);
            _inFlight.Remove(item.Archive.Name);
            return;
        }

        await DelayBeforeNextClickAsync(token);

        var context = new BrowserDownloadContext
        {
            Archive = item.Archive,
            Url = item.Url,
            DownloadFolder = item.DownloadFolder,
            Context = _context,
            Logger = _logger,
            Status = new Progress<string>(ReportStatus)
        };

        try
        {
            ReportStatus($"Automating {item.Archive.Name}");
            var ok = await handler.DownloadAsync(context, token);
            if (!ok)
                ReportStatus($"Could not automate {item.Archive.Name}; use Open page");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Browser automation failed for {Archive}", item.Archive.Name);
            ReportStatus($"Automation failed for {item.Archive.Name}: {ex.Message}");
        }
        finally
        {
            _inFlight.Remove(item.Archive.Name);
        }
    }

    private async Task EnsureBrowserAsync(CancellationToken token)
    {
        await _browserSemaphore.WaitAsync(token);
        try
        {
            if (_context != null && _browser is { IsConnected: true }) return;

            ReportStatus("Connecting to Edge");
            _logger.LogInformation("Connecting Playwright to Edge via CDP at {Endpoint}", CdpEndpoint);

            _playwright ??= await Playwright.CreateAsync();

            // Prefer an existing Edge the user already started with remote debugging.
            try
            {
                _browser = await _playwright.Chromium.ConnectOverCDPAsync(CdpEndpoint);
                _context = _browser.Contexts.FirstOrDefault() ?? await _browser.NewContextAsync();
                ReportStatus("Connected to running Edge");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "No existing Edge on {Endpoint}; launching one", CdpEndpoint);
            }

            var edgePath = FindEdgePath();
            if (edgePath == null)
                throw new InvalidOperationException("Microsoft Edge was not found. Browser automation requires Edge.");

            var profileDir = GetProfileDirectory();
            profileDir.CreateDirectory();

            _edgeProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = edgePath,
                    Arguments = $"{RemoteDebuggingArg} --user-data-dir=\"{profileDir}\" --no-first-run --no-default-browser-check",
                    UseShellExecute = false,
                    CreateNoWindow = false
                }
            };

            if (!_edgeProcess.Start())
                throw new InvalidOperationException("Could not start Microsoft Edge.");

            // Wait for Edge to open its debugging socket.
            await Task.Delay(TimeSpan.FromSeconds(3), token);

            var connected = false;
            for (var attempt = 0; attempt < 10 && !connected; attempt++)
            {
                try
                {
                    _browser = await _playwright.Chromium.ConnectOverCDPAsync(CdpEndpoint);
                    connected = true;
                }
                catch
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
            }

            if (!connected || _browser == null)
                throw new InvalidOperationException("Edge started but Playwright could not connect to its CDP endpoint.");

            _context = _browser.Contexts.FirstOrDefault() ?? await _browser.NewContextAsync();
            ReportStatus("Connected to Edge");
        }
        finally
        {
            _browserSemaphore.Release();
        }
    }

    private static string? FindEdgePath()
    {
        foreach (var path in EdgePaths)
        {
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    private static AbsolutePath GetProfileDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return localAppData.ToAbsolutePath().Combine("Wabbajack").Combine("EdgeAutomationProfile");
    }

    private async Task DelayBeforeNextClickAsync(CancellationToken token)
    {
        var ms = Random.Shared.Next(
            (int)_options.MinDelay.TotalMilliseconds,
            (int)_options.MaxDelay.TotalMilliseconds + 1);
        await Task.Delay(ms, token);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_workers)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _cts.Cancel();
        _queue.Writer.TryComplete();

        try
        {
            await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workers did not stop cleanly");
        }

        await _browserSemaphore.WaitAsync();
        try
        {
            try
            {
                if (_context != null)
                    await _context.CloseAsync();
                if (_browser != null)
                    await _browser.CloseAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "While closing Playwright browser connection");
            }

            _edgeProcess?.Dispose();
            _playwright?.Dispose();
        }
        finally
        {
            _browserSemaphore.Release();
        }

        _cts.Dispose();
        _status.OnCompleted();
    }

    private sealed record WorkItem(Archive Archive, Uri Url, AbsolutePath DownloadFolder);
}
