using ReimaginedLauncher.SaveMigration;

namespace ReimaginedLauncher.Tests.SaveMigration;

internal static class SaveMigrationFixtures
{
    private static readonly Lazy<string> RepositoryRootValue = new(FindRepositoryRoot);
    private static readonly Lazy<InstalledGameData> InstalledTargetValue = new(() => LoadInstalled(NewCopyDirectory()));

    public static string RepositoryRoot => RepositoryRootValue.Value;

    public static string DataDirectory =>
        Path.Combine(RepositoryRoot, "ReimaginedLauncher", "Assets", "SaveMigrationData");

    public static string InstalledDirectory => Path.Combine(RepositoryRoot, "ReimaginedLauncher.Tests", "TestFixtures",
        "SaveMigration", "Installed 3.0.13 development");

    public static InstalledGameData InstalledTarget => InstalledTargetValue.Value;

    public static string NewCopyDirectory() =>
        Path.Combine(Path.GetTempPath(), "ReimaginedSaveMigrationData", Guid.NewGuid().ToString("N"));

    public static InstalledGameData LoadInstalled(string copyDirectory) =>
        InstalledGameData.Load(InstalledDirectory, copyDirectory, "3.0.13 development");

    public static void CopyInstalledTables(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(InstalledDirectory, "*.txt"))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        }
    }

    public static SaveCheckpointRegistry LoadRegistry() => SaveCheckpointRegistry.Load(DataDirectory);

    public static SaveMigrator CreateMigrator(InstalledGameData? installedTarget = null) =>
        SaveMigrator.ForInstalledTarget(LoadRegistry(), installedTarget ?? InstalledTarget);

    public static string FixturePath(string fixture) =>
        Path.Combine(RepositoryRoot, "ReimaginedLauncher.Tests", "TestFixtures", "SaveMigration", fixture);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ReimaginedLauncher.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate ReimaginedLauncher.sln above the test output directory.");
    }
}