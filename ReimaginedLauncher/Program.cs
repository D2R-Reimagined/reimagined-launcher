using System;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReimaginedLauncher.HttpClients;

namespace ReimaginedLauncher;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    public static IServiceProvider ServiceProvider { get; private set; }
    
    [STAThread]
    public static void Main(string[] args)
    {
        var services = new ServiceCollection();
        services.AddHttpClient<NexusModsHttpClient>();
        
        ServiceProvider = services.BuildServiceProvider();
        
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