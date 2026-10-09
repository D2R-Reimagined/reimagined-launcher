using System;
using System.Net.Http;
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
        if (OperatingSystem.IsLinux() && args.Length > 0 && args[0] is "--steam-game" or "--install-steam-handoff")
        {
            try
            {
                if (args[0] == "--steam-game" && args.Length == 2)
                    Environment.ExitCode = SteamGameHandoff.RunGame(args[1]);
                else if (args[0] == "--install-steam-handoff" && args.Length == 4)
                    SteamGameHandoff.Install(args[1], args[2], args[3]);
                else throw new ArgumentException("Invalid Steam handoff command arguments.");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                Environment.ExitCode = 1;
            }
            return;
        }
        VelopackApp.Build().Run();

        var services = new ServiceCollection();
        AddLauncherServices(services);
        ServiceProvider = services.BuildServiceProvider();

        // Reconcile plugin state and purge stray files before the UI comes
        // up: drops orphan asset-backup claims for plugins no longer in
        // settings, removes unreferenced empty subdirs under %AppData%, and
        // clears stale plugin zip downloads from %TEMP%.
        PluginStateSanitizer.RunStartupSanitizationAsync().GetAwaiter().GetResult();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    internal static void AddLauncherServices(IServiceCollection services)
    {
        services.AddHttpClient<GitHubAnnouncementsHttpClient>();
        services.AddHttpClient<GitHubDiscussionPluginsHttpClient>();
        services.AddHttpClient<NexusModsHttpClient>();
        services.AddHttpClient(nameof(ReimaginedApiHttpClient)).ConfigurePrimaryHttpMessageHandler(() =>
            new System.Net.Http.HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All });
        // Shared so every consumer gets the access token provider sign-in sets.
        services.AddSingleton(provider => new ReimaginedApiHttpClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ReimaginedApiHttpClient))));
        services.AddHttpClient<D2RLoaderInstallerService>();
        services.AddHttpClient<ModReleaseInstallerService>();
        services.AddSingleton<LauncherAuthenticationService>();
        services.AddSingleton<LadderBundleService>();
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
