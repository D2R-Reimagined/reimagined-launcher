using Microsoft.Extensions.DependencyInjection;
using ReimaginedLauncher.HttpClients;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class LauncherServicesTests
{
    [Fact]
    public async Task ViewsShareTheApiClientThatSignInAuthorizes()
    {
        var services = new ServiceCollection();
        Program.AddLauncherServices(services);
        using var provider = services.BuildServiceProvider();

        var authentication = provider.GetRequiredService<LauncherAuthenticationService>();
        await authentication.InitializeAsync(new AppSettings());

        // LaunchView resolves its own client; it must be the one sign-in wired up.
        var viewClient = provider.GetRequiredService<ReimaginedApiHttpClient>();
        Assert.NotNull(viewClient.AccessTokenProvider);
        Assert.Same(viewClient, provider.GetRequiredService<ReimaginedApiHttpClient>());
    }
}
