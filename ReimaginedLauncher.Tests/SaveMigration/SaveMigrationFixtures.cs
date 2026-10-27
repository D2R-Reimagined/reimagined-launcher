using ReimaginedLauncher.SaveMigration;

namespace ReimaginedLauncher.Tests.SaveMigration;

internal static class SaveMigrationFixtures
{
    private static readonly Lazy<string> RepositoryRootValue = new(FindRepositoryRoot);
    private static readonly Lazy<InstalledGameData> InstalledTargetValue = new(() => LoadInstalled(NewCopyDirectory()));

    public static string RepositoryRoot => RepositoryRootValue.Value;

    public static string DataDirectory =>
        Path.Combine(RepositoryRoot, "ReimaginedLauncher", "Assets", "SaveMigrationData");

    public static string InstalledDirectory => Path.Combine(DataDirectory, "D2RR 3.1.0");

    public static InstalledGameData InstalledTarget => InstalledTargetValue.Value;

    public static string NewCopyDirectory() =>
        Path.Combine(Path.GetTempPath(), "ReimaginedSaveMigrationData", Guid.NewGuid().ToString("N"));

    public static InstalledGameData LoadInstalled(string copyDirectory) =>
        InstalledGameData.Load(InstalledDirectory, copyDirectory, "3.1.0");

    public static void CopyInstalledTables(string directory) => CopyTables(InstalledDirectory, directory);

    public static void CopyTables(string sourceDirectory, string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*.txt"))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        }
    }

    public static string ReleaseDirectory(string checkpointId)
    {
        var registry = LoadRegistry();
        return registry.PathOf(registry.Get(checkpointId).Directory);
    }

    /// <summary>
    /// Installed tables copied from <paramref name="sourceDirectory"/> with one unique item's rarity changed, a field saves never
    /// store, so the data matches no registered checkpoint byte for byte.
    /// </summary>
    public static InstalledGameData LoadModified(string sourceDirectory, string? version)
    {
        var directory = NewCopyDirectory();
        CopyTables(sourceDirectory, directory);
        var path = Path.Combine(directory, "uniqueitems.txt");
        var lines = File.ReadAllLines(path, System.Text.Encoding.Latin1);
        var headers = lines[0].Split('\t');
        var cells = lines[1].Split('\t');
        var rarity = Array.IndexOf(headers, "rarity");
        cells[rarity] = cells[rarity] == "1" ? "2" : "1";
        lines[1] = string.Join('\t', cells);
        File.WriteAllLines(path, lines, System.Text.Encoding.Latin1);
        return InstalledGameData.Load(directory, NewCopyDirectory(), version);
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