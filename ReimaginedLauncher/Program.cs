using System;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReimaginedLauncher.HttpClients;
using ReimaginedLauncher.Utilities;
using Velopack;

namespace ReimaginedLauncher;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    public static IServiceProvider ServiceProvider { get; private set; } = null!;
    
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var services = new ServiceCollection();
        services.AddHttpClient<GitHubAnnouncementsHttpClient>();
        services.AddHttpClient<GitHubDiscussionPluginsHttpClient>();
        services.AddHttpClient<NexusModsHttpClient>();
        
        ServiceProvider = services.BuildServiceProvider();

        // Reconcile plugin state and purge stray files before the UI comes
        // up: drops orphan asset-backup claims for plugins no longer in
        // settings, removes unreferenced empty subdirs under %AppData%, and
        // clears stale plugin zip downloads from %TEMP%.
        PluginStateSanitizer.RunStartupSanitizationAsync().GetAwaiter().GetResult();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
