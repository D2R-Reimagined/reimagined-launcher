using ReimaginedLauncher.Utilities;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using Xunit;

namespace ReimaginedLauncher.Tests;

[CollectionDefinition("Launcher updates", DisableParallelization = true)]
public sealed class LauncherUpdatesCollection;

[Collection("Launcher updates")]
public sealed class LauncherUpdateServiceTests : IAsyncLifetime
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"launcher-update-tests-{Guid.NewGuid():N}");
    private readonly bool _previousDisabled = LauncherUpdateService.AreUpdatesDisabled;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_testDirectory);
        LauncherUpdateService.AreUpdatesDisabled = false;
        await CheckAsync(new FakeUpdateManager(_testDirectory));
    }

    [Fact]
    public async Task LaterCheckFindsAnUpdateAfterStartupWasUpToDate()
    {
        var manager = new FakeUpdateManager(_testDirectory);
        Assert.Equal(LauncherUpdateCheckStatus.UpToDate, (await CheckAsync(manager)).Status);

        manager.Update = CreateUpdate("2.0.0");
        Assert.Equal(LauncherUpdateCheckStatus.UpdateReady, (await CheckAsync(manager)).Status);

        Assert.Equal(2, manager.CheckCount);
        Assert.Equal(1, manager.DownloadCount);
        Assert.True(LauncherUpdateService.IsUpdateAvailable);
        Assert.True(LauncherUpdateService.IsUpdateDownloaded);
        Assert.Equal("2.0.0", LauncherUpdateService.LatestVersion);
    }

    [Fact]
    public async Task FailedCheckReportsFailureAndCanBeRetried()
    {
        var manager = new FakeUpdateManager(_testDirectory) { CheckError = new IOException("Network unavailable") };

        var failed = await CheckAsync(manager);
        Assert.Equal(LauncherUpdateCheckStatus.Failed, failed.Status);
        Assert.Equal("Network unavailable", failed.ErrorMessage);

        manager.CheckError = null;
        Assert.Equal(LauncherUpdateCheckStatus.UpToDate, (await CheckAsync(manager)).Status);
        Assert.Equal(2, manager.CheckCount);
    }

    [Fact]
    public async Task ConcurrentCheckDoesNotStartAnotherRequestOrReportUpToDate()
    {
        var completion = new TaskCompletionSource<UpdateInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new FakeUpdateManager(_testDirectory) { PendingCheck = completion.Task };
        var first = CheckAsync(manager);
        try
        {
            Assert.Equal(LauncherUpdateCheckStatus.InProgress, (await CheckAsync(manager)).Status);
            Assert.Equal(1, manager.CheckCount);
        }
        finally
        {
            completion.SetResult(null);
            await first;
        }

        Assert.Equal(LauncherUpdateCheckStatus.UpToDate, (await CheckAsync(manager)).Status);
        Assert.Equal(2, manager.CheckCount);
    }

    [Fact]
    public async Task DownloadFailureClearsProgressAndAllowsRetry()
    {
        var manager = new FakeUpdateManager(_testDirectory)
        {
            Update = CreateUpdate("2.0.0"),
            DownloadError = new IOException("Download interrupted")
        };
        var states = new List<bool>();
        void OnStateChanged(object? sender, EventArgs e) => states.Add(LauncherUpdateService.IsDownloading);
        LauncherUpdateService.UpdateStateChanged += OnStateChanged;
        try
        {
            Assert.Equal(LauncherUpdateCheckStatus.Failed, (await CheckAsync(manager)).Status);
            Assert.True(LauncherUpdateService.IsUpdateAvailable);
            Assert.False(LauncherUpdateService.IsUpdateDownloaded);
            Assert.False(LauncherUpdateService.IsDownloading);
            Assert.Contains(true, states);
            Assert.False(states[^1]);

            manager.DownloadError = null;
            Assert.Equal(LauncherUpdateCheckStatus.UpdateReady, (await CheckAsync(manager)).Status);
            Assert.Equal(2, manager.DownloadCount);
        }
        finally
        {
            LauncherUpdateService.UpdateStateChanged -= OnStateChanged;
        }
    }

    [Fact]
    public async Task DownloadedUpdateIsNotDownloadedAgainAndSurvivesCheckFailure()
    {
        var manager = new FakeUpdateManager(_testDirectory) { Update = CreateUpdate("2.0.0") };
        await CheckAsync(manager);
        manager.Update = CreateUpdate("2.0.0");
        Assert.Equal(LauncherUpdateCheckStatus.UpdateReady, (await CheckAsync(manager)).Status);
        Assert.Equal(2, manager.CheckCount);
        Assert.Equal(1, manager.DownloadCount);

        manager.CheckError = new IOException("Offline");
        Assert.Equal(LauncherUpdateCheckStatus.Failed, (await CheckAsync(manager)).Status);
        Assert.True(LauncherUpdateService.IsUpdateDownloaded);
        Assert.Equal("2.0.0", LauncherUpdateService.LatestVersion);
    }

    [Fact]
    public async Task NewerUpdateDoesNotReusePreviouslyDownloadedVersion()
    {
        var manager = new FakeUpdateManager(_testDirectory) { Update = CreateUpdate("2.0.0") };
        await CheckAsync(manager);
        manager.Update = CreateUpdate("3.0.0");
        manager.DownloadError = new IOException("Offline");

        Assert.Equal(LauncherUpdateCheckStatus.Failed, (await CheckAsync(manager)).Status);
        Assert.Equal("3.0.0", LauncherUpdateService.LatestVersion);
        Assert.False(LauncherUpdateService.IsUpdateDownloaded);
        Assert.Equal(2, manager.DownloadCount);
    }

    [Fact]
    public async Task DisabledChecksCanRunAfterReenabling()
    {
        var manager = new FakeUpdateManager(_testDirectory);
        LauncherUpdateService.AreUpdatesDisabled = true;
        Assert.Equal(LauncherUpdateCheckStatus.Disabled, (await CheckAsync(manager)).Status);
        Assert.Equal(0, manager.CheckCount);

        LauncherUpdateService.AreUpdatesDisabled = false;
        Assert.Equal(LauncherUpdateCheckStatus.UpToDate, (await CheckAsync(manager)).Status);
        Assert.Equal(1, manager.CheckCount);
    }

    [Fact]
    public async Task UninstalledLauncherDoesNotReportUpToDate()
    {
        var manager = new FakeUpdateManager(_testDirectory) { Installed = false };
        Assert.Equal(LauncherUpdateCheckStatus.NotInstalled, (await CheckAsync(manager)).Status);
        Assert.Equal(0, manager.CheckCount);
    }

    private static Task<LauncherUpdateCheckResult> CheckAsync(FakeUpdateManager manager)
        => LauncherUpdateService.CheckForUpdatesAsync(() => manager);

    private static UpdateInfo CreateUpdate(string version)
        => new(new VelopackAsset { Version = SemanticVersion.Parse(version) }, isDowngrade: false);

    public async Task DisposeAsync()
    {
        LauncherUpdateService.AreUpdatesDisabled = false;
        await CheckAsync(new FakeUpdateManager(_testDirectory));
        LauncherUpdateService.AreUpdatesDisabled = _previousDisabled;
        Directory.Delete(_testDirectory, recursive: true);
    }

    private sealed class FakeUpdateManager(string directory)
        : UpdateManager(new SimpleFileSource(new DirectoryInfo(directory)), locator: new TestVelopackLocator("LauncherTests", "1.0.0", directory))
    {
        public bool Installed { get; set; } = true;
        public override bool IsInstalled => Installed;
        public UpdateInfo? Update { get; set; }
        public Exception? CheckError { get; set; }
        public Exception? DownloadError { get; set; }
        public Task<UpdateInfo?>? PendingCheck { get; set; }
        public int CheckCount { get; private set; }
        public int DownloadCount { get; private set; }

        public override Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            CheckCount++;
            return CheckError is not null
                ? Task.FromException<UpdateInfo?>(CheckError)
                : PendingCheck ?? Task.FromResult(Update);
        }

        public override Task DownloadUpdatesAsync(UpdateInfo updates, Action<int>? progress = null, CancellationToken cancelToken = default)
        {
            DownloadCount++;
            return DownloadError is not null ? Task.FromException(DownloadError) : Task.CompletedTask;
        }
    }
}
