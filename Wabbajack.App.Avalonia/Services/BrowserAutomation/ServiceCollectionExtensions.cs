using System;
using Microsoft.Extensions.DependencyInjection;

namespace Wabbajack.App.Avalonia.Services.BrowserAutomation;

public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the Playwright-based browser automation service and all download handlers.
    ///     The service is transient: one instance per preflight run.
    /// </summary>
    public static IServiceCollection AddBrowserAutomation(this IServiceCollection services)
    {
        services.AddSingleton<BrowserAutomationOptions>();
        services.AddSingleton<IBrowserDownloadHandler, NexusBrowserDownloadHandler>();
        services.AddTransient<BrowserAutomationService>();
        return services;
    }

    /// <summary>
    ///     Registers the service with a custom options instance.
    /// </summary>
    public static IServiceCollection AddBrowserAutomation(this IServiceCollection services,
        BrowserAutomationOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<IBrowserDownloadHandler, NexusBrowserDownloadHandler>();
        services.AddTransient<BrowserAutomationService>();
        return services;
    }
}
