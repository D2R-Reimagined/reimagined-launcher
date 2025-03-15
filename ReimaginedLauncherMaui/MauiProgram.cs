using Microsoft.Extensions.Logging;
using ReimaginedLauncherMaui.Services;

namespace ReimaginedLauncherMaui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts => { fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"); });

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSingleton<GameLauncherService>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<IUniqueItemService, UniqueItemService>();
        builder.Services.AddSingleton<ISetItemService, SetItemService>();
        builder.Services.AddSingleton<IPropertyService, PropertyService>();
        
        return builder.Build();
    }
}
