using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using D2RReimaginedTools.JsonFileParsers;
using D2RReimaginedTools.Models;
using D2RReimaginedTools.TextFileParsers;
using ReimaginedLauncher.Generators;

namespace ReimaginedLauncher.Utilities;

public static class PluginsService
{
    private const string PluginsDirectoryName = "plugins";
    private const string BundledPluginsDirectoryName = "Assets/Plugins";
    private const string PluginInfoFileName = "plugininfo.json";
    private const string GeneratedPluginsFolderName = "plugins";
    private const string StringsDirectoryRelativePath = "local/lng/strings";
    private const string MissilesTargetFileName = "missiles.json";
    // missiles.json lives outside the excel directory, alongside the strings folder under the mod
    // root (e.g. <mod>/data/hd/missiles/missiles.json). Resolved relative to the mod data root.
    private const string MissilesRelativePath = "hd/missiles/missiles.json";
    private const string MonstersTargetFileName = "monsters.json";
    // monsters.json mirrors the missiles.json layout (flat key->asset map with a leading
    // 'dependencies' header) and lives at <mod>/data/hd/character/monsters.json.
    private const string MonstersRelativePath = "hd/character/monsters.json";
    private const string PluginAssetsDirectoryName = "assets";
    private const string PluginAssetWarningMessage =
        "This plugin directly copies an asset into the mod without reading the mods data, it may cause problems if it is not setup correctly.";

    // The 13 language columns that D2R ships in its string JSON files. Strings-plugin entries may
    // only target these keys; any other property on a plugin entry is ignored.
    private static readonly HashSet<string> KnownStringLanguageColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "enUS", "zhTW", "deDE", "esES", "frFR", "itIT", "koKR", "plPL", "esMX", "jaJP", "ptBR", "ruRU", "zhCN"
    };
    private static readonly JsonSerializerOptions JsonOptions = SerializerOptions.PropertyNameCaseInsensitive;
    private static readonly Regex ParameterTokenRegex = new(@"\{\{\s*parameter:([a-zA-Z0-9_\-]+)\s*\}\}", RegexOptions.Compiled);
    private static readonly Regex ModVersionRegex = new(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled);
    // Matches a numeric row-index range in the form "start-end" (inclusive), e.g. "50-100".
    // Whitespace around the numbers and the dash is allowed so plugins can be formatted loosely.
    // Accepts common Unicode dash variants (hyphen-minus, non-breaking hyphen, figure dash,
    // en dash, em dash, horizontal bar, minus sign) because some editors auto-replace "-".
    private static readonly Regex RowRangeRegex = new(@"^\s*(\d+)\s*[-\u2010-\u2015\u2212]\s*(\d+)\s*$", RegexOptions.Compiled);

    private static readonly Dictionary<string, FileParserRegistration> ParserRegistry =
        BuildParserRegistry();

    public static string PluginsDirectoryPath => Path.Combine(SettingsManager.AppDirectoryPath, PluginsDirectoryName);

    public static async Task EnsureBundledPluginsInstalledAsync()
    {
        var bundledPluginsRoot = Path.Combine(AppContext.BaseDirectory, BundledPluginsDirectoryName);
        if (!Directory.Exists(bundledPluginsRoot))
        {
            return;
        }

        MainWindow.Settings.CurrentProfile.Plugins ??= [];
        Directory.CreateDirectory(PluginsDirectoryPath);

        foreach (var sourceDirectory in Directory.GetDirectories(bundledPluginsRoot))
        {
            var folderName = Path.GetFileName(sourceDirectory);
            if (string.IsNullOrWhiteSpace(folderName))
            {
                continue;
            }

            var sourcePluginInfoPath = Path.Combine(sourceDirectory, PluginInfoFileName);
            if (!File.Exists(sourcePluginInfoPath))
            {
                continue;
            }

            var existingRegistration = MainWindow.Settings.CurrentProfile.Plugins
                .FirstOrDefault(plugin => plugin.FolderName.Equals(folderName, StringComparison.OrdinalIgnoreCase));

            var destinationDirectory = Path.Combine(PluginsDirectoryPath, folderName);
            if (existingRegistration != null || Directory.Exists(destinationDirectory))
            {
                continue;
            }

            await CopyDirectoryAsync(sourceDirectory, destinationDirectory);
            MainWindow.Settings.CurrentProfile.Plugins.Add(new PluginRegistration
            {
                Id = Guid.NewGuid().ToString("N"),
                FolderName = folderName,
                IsEnabled = false
            });
        }

        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    public static async Task<IReadOnlyList<OfficialPluginCatalogItem>> GetOfficialCatalogAsync()
    {
        var bundledPluginsRoot = Path.Combine(AppContext.BaseDirectory, BundledPluginsDirectoryName);
        if (!Directory.Exists(bundledPluginsRoot))
        {
            return [];
        }

        MainWindow.Settings.CurrentProfile.Plugins ??= [];
        var catalog = new List<OfficialPluginCatalogItem>();

        foreach (var sourceDirectory in Directory.GetDirectories(bundledPluginsRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var folderName = Path.GetFileName(sourceDirectory);
            if (string.IsNullOrWhiteSpace(folderName))
            {
                continue;
            }

            var sourcePluginInfoPath = Path.Combine(sourceDirectory, PluginInfoFileName);
            if (!File.Exists(sourcePluginInfoPath))
            {
                continue;
            }

            var registration = FindRegistrationByFolderName(folderName);
            var errors = new List<string>();
            var name = folderName;
            var version = "Unknown";
            var description = string.Empty;

            try
            {
                var pluginInfo = await LoadPluginInfoAsync(sourcePluginInfoPath);
                ValidatePluginInfo(pluginInfo, sourceDirectory);
                name = pluginInfo.Name;
                version = pluginInfo.Version;
                description = pluginInfo.Description ?? string.Empty;
            }
            catch (Exception ex)
            {
                errors.Add(FormatJsonError("plugininfo.json", ex));
            }

            catalog.Add(new OfficialPluginCatalogItem
            {
                FolderName = folderName,
                PluginId = registration?.Id ?? string.Empty,
                Name = name,
                Version = version,
                Description = description,
                IsInstalled = registration != null,
                IsEnabled = registration?.IsEnabled == true,
                Errors = errors
            });
        }

        return catalog;
    }

    public static async Task<IReadOnlyList<PluginCatalogItem>> GetCatalogAsync()
    {
        MainWindow.Settings.CurrentProfile.Plugins ??= [];
        var catalog = new List<PluginCatalogItem>(MainWindow.Settings.CurrentProfile.Plugins.Count);

        for (var index = 0; index < MainWindow.Settings.CurrentProfile.Plugins.Count; index++)
        {
            var registration = MainWindow.Settings.CurrentProfile.Plugins[index];
            var pluginState = await LoadPluginStateAsync(registration);
            catalog.Add(new PluginCatalogItem
            {
                Id = registration.Id,
                Name = pluginState.Name,
                Version = pluginState.Version,
                ModVersion = pluginState.ModVersion,
                Author = pluginState.Author,
                Description = pluginState.Description,
                IsEnabled = registration.IsEnabled,
                Order = index + 1,
                Parameters = pluginState.Parameters,
                Files = pluginState.Files,
                Errors = pluginState.Errors,
                Warnings = pluginState.Warnings
            });
        }

        return catalog;
    }

    public static async Task InstallOfficialPluginAsync(string folderName)
    {
        var sourceDirectory = GetBundledPluginDirectory(folderName);
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException("The selected official plugin could not be found.");
        }

        var sourcePluginInfoPath = Path.Combine(sourceDirectory, PluginInfoFileName);
        if (!File.Exists(sourcePluginInfoPath))
        {
            throw new FileNotFoundException("The selected official plugin is missing plugininfo.json.", sourcePluginInfoPath);
        }

        var pluginInfo = await LoadPluginInfoAsync(sourcePluginInfoPath);
        ValidatePluginInfo(pluginInfo, sourceDirectory);

        Directory.CreateDirectory(PluginsDirectoryPath);
        MainWindow.Settings.CurrentProfile.Plugins ??= [];

        var registration = FindRegistrationByFolderName(folderName);
        var destinationDirectory = Path.Combine(PluginsDirectoryPath, folderName);
        if (!Directory.Exists(destinationDirectory))
        {
            await CopyDirectoryAsync(sourceDirectory, destinationDirectory);
        }

        if (registration == null)
        {
            MainWindow.Settings.CurrentProfile.Plugins.Add(new PluginRegistration
            {
                Id = Guid.NewGuid().ToString("N"),
                FolderName = folderName,
                IsEnabled = true
            });
        }
        else
        {
            registration.IsEnabled = true;
        }

        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    public static async Task<PluginImportPreview> LoadPluginImportPreviewAsync(string zipPath)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException("The selected plugin archive was not found.", zipPath);
        }

        if (!IsZipArchive(zipPath))
        {
            throw new InvalidDataException("The selected file is not a valid zip archive.");
        }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "d2r-reimagined-launcher",
            "plugin-import",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(tempDirectory);

        try
        {
            ZipFile.ExtractToDirectory(zipPath, tempDirectory, overwriteFiles: true);

            var pluginInfoPaths = Directory.GetFiles(tempDirectory, PluginInfoFileName, SearchOption.AllDirectories);
            if (pluginInfoPaths.Length == 0)
            {
                throw new InvalidDataException("plugininfo.json was not found in the selected archive.");
            }

            if (pluginInfoPaths.Length > 1)
            {
                throw new InvalidDataException("The selected archive contains multiple plugininfo.json files.");
            }

            var pluginInfoPath = pluginInfoPaths[0];
            var pluginRootDirectory = Path.GetDirectoryName(pluginInfoPath);
            if (string.IsNullOrWhiteSpace(pluginRootDirectory))
            {
                throw new InvalidDataException("The plugin root directory could not be resolved.");
            }

            var pluginInfo = await LoadPluginInfoAsync(pluginInfoPath);
            ValidatePluginInfo(pluginInfo, pluginRootDirectory);

            return new PluginImportPreview
            {
                Name = pluginInfo.Name,
                Version = pluginInfo.Version
            };
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    public static async Task<InstalledPluginLookupResult?> FindInstalledPluginByNameAsync(string pluginName)
    {
        MainWindow.Settings.CurrentProfile.Plugins ??= [];

        foreach (var registration in MainWindow.Settings.CurrentProfile.Plugins)
        {
            var pluginState = await LoadPluginStateAsync(registration);
            if (!pluginState.Name.Equals(pluginName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return new InstalledPluginLookupResult
            {
                PluginId = registration.Id,
                Name = pluginState.Name,
                Version = pluginState.Version
            };
        }

        return null;
    }

    public static async Task ImportPluginAsync(string zipPath, string? replacePluginId = null)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException("The selected plugin archive was not found.", zipPath);
        }

        if (!IsZipArchive(zipPath))
        {
            throw new InvalidDataException("The selected file is not a valid zip archive.");
        }

        Directory.CreateDirectory(PluginsDirectoryPath);

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "d2r-reimagined-launcher",
            "plugin-import",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(tempDirectory);

        try
        {
            ZipFile.ExtractToDirectory(zipPath, tempDirectory, overwriteFiles: true);

            var pluginInfoPaths = Directory.GetFiles(tempDirectory, PluginInfoFileName, SearchOption.AllDirectories);
            if (pluginInfoPaths.Length == 0)
            {
                throw new InvalidDataException("plugininfo.json was not found in the selected archive.");
            }

            if (pluginInfoPaths.Length > 1)
            {
                throw new InvalidDataException("The selected archive contains multiple plugininfo.json files.");
            }

            var pluginInfoPath = pluginInfoPaths[0];
            var pluginRootDirectory = Path.GetDirectoryName(pluginInfoPath);
            if (string.IsNullOrWhiteSpace(pluginRootDirectory))
            {
                throw new InvalidDataException("The plugin root directory could not be resolved.");
            }

            var pluginInfo = await LoadPluginInfoAsync(pluginInfoPath);
            ValidatePluginInfo(pluginInfo, pluginRootDirectory);

            if (!string.IsNullOrWhiteSpace(replacePluginId))
            {
                var registration = GetRegistration(replacePluginId);
                var destDirectory = GetPluginDirectory(registration);
                if (Directory.Exists(destDirectory))
                {
                    Directory.Delete(destDirectory, recursive: true);
                }

                await CopyDirectoryAsync(pluginRootDirectory, destDirectory);
                await SettingsManager.SaveAsync(MainWindow.Settings);
                return;
            }

            var destinationFolderName = GetUniquePluginFolderName(pluginInfo.Name, pluginInfo.Version);
            var destinationDirectory = Path.Combine(PluginsDirectoryPath, destinationFolderName);
            await CopyDirectoryAsync(pluginRootDirectory, destinationDirectory);

            MainWindow.Settings.CurrentProfile.Plugins ??= [];
            MainWindow.Settings.CurrentProfile.Plugins.Add(new PluginRegistration
            {
                Id = Guid.NewGuid().ToString("N"),
                FolderName = destinationFolderName,
                IsEnabled = false
            });

            await SettingsManager.SaveAsync(MainWindow.Settings);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    public static async Task SetPluginEnabledAsync(string pluginId, bool isEnabled)
    {
        var registration = GetRegistration(pluginId);
        registration.IsEnabled = isEnabled;
        await SettingsManager.SaveAsync(MainWindow.Settings);

        if (!isEnabled)
        {
            // Restore any mod files this plugin replaced via asset operations
            // so disabling truly reverts its on-disk effects.
            await PluginAssetBackupService.RestoreForPluginAsync(pluginId);
        }
    }

    public static async Task MovePluginAsync(string pluginId, int direction)
    {
        MainWindow.Settings.CurrentProfile.Plugins ??= [];
        var currentIndex = MainWindow.Settings.CurrentProfile.Plugins.FindIndex(plugin => plugin.Id == pluginId);
        if (currentIndex < 0)
        {
            throw new InvalidOperationException("The selected plugin could not be found.");
        }

        var nextIndex = currentIndex + direction;
        if (nextIndex < 0 || nextIndex >= MainWindow.Settings.CurrentProfile.Plugins.Count)
        {
            return;
        }

        (MainWindow.Settings.CurrentProfile.Plugins[currentIndex], MainWindow.Settings.CurrentProfile.Plugins[nextIndex]) =
            (MainWindow.Settings.CurrentProfile.Plugins[nextIndex], MainWindow.Settings.CurrentProfile.Plugins[currentIndex]);

        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    public static async Task DeletePluginAsync(string pluginId)
    {
        MainWindow.Settings.CurrentProfile.Plugins ??= [];
        var registration = GetRegistration(pluginId);
        MainWindow.Settings.CurrentProfile.Plugins.Remove(registration);
        await SettingsManager.SaveAsync(MainWindow.Settings);

        // Restore any replaced mod files before the plugin's metadata is gone.
        await PluginAssetBackupService.RestoreForPluginAsync(pluginId);

        var pluginDirectory = GetPluginDirectory(registration);
        if (Directory.Exists(pluginDirectory))
        {
            Directory.Delete(pluginDirectory, recursive: true);
        }
    }

    public static async Task<PluginEditorDocument> LoadEditorDocumentAsync(string pluginId, string relativePath)
    {
        var registration = GetRegistration(pluginId);
        var absolutePath = GetPluginFilePath(pluginId, relativePath);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException("The selected plugin JSON file could not be found.", relativePath);
        }

        // Avoid a full LoadPluginStateAsync (which validates every file in the
        // plugin); the editor only needs the display name.
        var pluginInfoPath = Path.Combine(GetPluginDirectory(registration), PluginInfoFileName);
        var pluginName = registration.FolderName;
        if (File.Exists(pluginInfoPath))
        {
            try
            {
                var pluginInfo = await LoadPluginInfoAsync(pluginInfoPath);
                pluginName = pluginInfo.Name;
            }
            catch (Exception ex)
            {
                LaunchDiagnostics.LogException(
                    $"Failed to read plugin name for '{registration.FolderName}'", ex);
            }
        }

        return new PluginEditorDocument
        {
            PluginId = pluginId,
            PluginName = pluginName,
            RelativePath = relativePath,
            Content = await File.ReadAllTextAsync(absolutePath)
        };
    }

    public static async Task SaveEditorDocumentAsync(string pluginId, string relativePath, string content)
    {
        _ = ParsePluginOperations(content);
        var absolutePath = GetPluginFilePath(pluginId, relativePath);
        await File.WriteAllTextAsync(absolutePath, content);
    }

    public static async Task<bool> SaveParameterValueAsync(string pluginId, string parameterKey, string value)
    {
        var registration = GetRegistration(pluginId);
        var pluginInfoPath = Path.Combine(GetPluginDirectory(registration), PluginInfoFileName);
        var pluginInfo = await LoadPluginInfoAsync(pluginInfoPath);
        var parameter = pluginInfo.Parameters.FirstOrDefault(item =>
            item.Key.Equals(parameterKey, StringComparison.OrdinalIgnoreCase));

        if (parameter == null)
        {
            throw new InvalidOperationException("The selected plugin parameter could not be found.");
        }

        // Checkbox parameters always persist as the canonical "true"/"false" so the saved
        // plugininfo.json stays consistent regardless of whether the UI sent "true"/"false",
        // "1"/"0", a localized string, or the empty string when the user uncheck/checks the box.
        var normalizedValue = string.Equals(parameter.Type, "checkbox", StringComparison.OrdinalIgnoreCase)
            ? NormalizeCheckboxValue(value)
            : value.Trim();
        var currentValue = string.IsNullOrWhiteSpace(parameter.Value) ? parameter.DefaultValue : parameter.Value;
        if (string.Equals(currentValue, normalizedValue, StringComparison.Ordinal))
        {
            return false;
        }

        parameter.Value = normalizedValue;
        await SavePluginInfoAsync(pluginInfoPath, pluginInfo);
        return true;
    }

    /// <summary>
    /// Applies the excel-directory-scoped portion of every enabled plugin to
    /// <paramref name="excelDirectory"/> -- i.e. operations that target a .txt
    /// file inside the excel folder via the parser registry. The launcher
    /// invokes this once per excel directory (e.g. <c>excel</c> and
    /// <c>excel/base</c>) because those directories ship distinct .txt
    /// content. Mod-root-relative work (missiles.json, monsters.json, strings,
    /// asset copies) is intentionally NOT performed here -- it runs exactly
    /// once per launch via <see cref="ApplyEnabledPluginsModRootAsync"/> so
    /// the plugin asset backup service registers each destination a single
    /// time per pass.
    /// </summary>
    public static async Task ApplyEnabledPluginsExcelAsync(string excelDirectory, IProgress<string>? progress = null)
    {
        MainWindow.Settings.CurrentProfile.Plugins ??= [];

        foreach (var registration in MainWindow.Settings.CurrentProfile.Plugins.Where(plugin => plugin.IsEnabled))
        {
            var pluginState = await LoadPluginStateAsync(registration);
            if (pluginState.Errors.Count > 0)
            {
                // Errors and authoring warnings are emitted once per launch
                // from ApplyEnabledPluginsModRootAsync; skip silently here.
                continue;
            }

            try
            {
                // Build the parameter dictionary once per plugin instead of
                // per file; the values are constant for the entire apply pass.
                var parameters = pluginState.Parameters.ToDictionary(
                    parameter => parameter.Key,
                    parameter => parameter.Value,
                    StringComparer.OrdinalIgnoreCase);

                var hasExcelWork = false;
                foreach (var pluginFile in pluginState.Files)
                {
                    var operations = await LoadPluginOperationsAsync(GetPluginFilePath(registration.Id, pluginFile.RelativePath));
                    var filtered = FilterConditionalOperations(operations, parameters, pluginState.Name, pluginFile.RelativePath);
                    if (filtered.Count == 0)
                    {
                        continue;
                    }

                    if (!hasExcelWork)
                    {
                        ReportProgress(progress, $"Applying plugin {pluginState.Name} (excel)...");
                        hasExcelWork = true;
                    }

                    await ApplyExcelOperationsAsync(excelDirectory, filtered, parameters);
                }
            }
            catch (Exception ex)
            {
                LaunchDiagnostics.LogException($"Failed to apply plugin '{pluginState.Name}' (excel)", ex);
                Notifications.SendNotification($"Plugin '{pluginState.Name}' failed: {ex.Message}", "Warning");
            }
        }
    }

    /// <summary>
    /// Surfaces an authoring/load-order warning when two or more enabled plugins
    /// declare an asset copy targeting the same destination under
    /// <paramref name="modRoot"/>. Last-writer-wins semantics are unchanged --
    /// this only makes the silent override visible. Conditional assets whose
    /// <c>Condition</c> evaluates to <c>false</c> for the current parameters are
    /// excluded from the collision set, and collisions where every claimant
    /// ships byte-identical source bytes are demoted to a diagnostics-log entry
    /// (no user-facing notification) since they cannot actually disagree.
    /// Intended to be called once per launch, before the four pre-stage entry
    /// points and <see cref="ApplyEnabledPluginsModRootAsync"/> run.
    /// </summary>
    public static async Task WarnAssetCollisionsAsync(string modRoot, IProgress<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(modRoot))
        {
            return;
        }

        MainWindow.Settings.CurrentProfile.Plugins ??= [];

        var modRootFull = Path.GetFullPath(modRoot);
        var claimants = new Dictionary<string, List<AssetClaim>>(StringComparer.OrdinalIgnoreCase);

        foreach (var registration in MainWindow.Settings.CurrentProfile.Plugins.Where(plugin => plugin.IsEnabled))
        {
            PluginState pluginState;
            try
            {
                pluginState = await LoadPluginStateAsync(registration);
            }
            catch
            {
                // Loading failures surface from ApplyEnabledPluginsModRootAsync;
                // skip silently here so the same plugin is not double-reported.
                continue;
            }

            if (pluginState.Errors.Count > 0 || pluginState.Assets.Count == 0)
            {
                continue;
            }

            var parameterValues = pluginState.Parameters.ToDictionary(
                parameter => parameter.Key,
                parameter => parameter.Value,
                StringComparer.OrdinalIgnoreCase);

            foreach (var asset in pluginState.Assets)
            {
                if (asset.Condition != null && !EvaluateCondition(asset.Condition, parameterValues))
                {
                    continue;
                }

                string destinationFull;
                try
                {
                    destinationFull = Path.GetFullPath(Path.Combine(modRootFull, asset.TargetRelativePath));
                }
                catch
                {
                    // Path resolution failures are reported by the canonical
                    // apply pass; ignore here to avoid duplicate notifications.
                    continue;
                }

                if (!claimants.TryGetValue(destinationFull, out var list))
                {
                    list = new List<AssetClaim>();
                    claimants[destinationFull] = list;
                }

                list.Add(new AssetClaim(pluginState.Name, asset.SourceAbsolutePath, asset.TargetRelativePath));
            }
        }

        foreach (var (destinationFull, list) in claimants)
        {
            if (list.Count < 2)
            {
                continue;
            }

            var relative = TryGetModRootRelativePath(modRootFull, destinationFull);
            var winner = list[^1];
            var losers = list.Take(list.Count - 1).ToList();

            // If every claimant ships byte-identical source bytes the override
            // cannot disagree. Log it for auditability but do not raise a
            // warning notification -- otherwise authors who legitimately
            // bundle the same upstream file in two compatible plugins would
            // see noise on every launch.
            if (await AreAllSourcesIdenticalAsync(list))
            {
                LaunchDiagnostics.Log(
                    $"Asset collision on '{relative}': {list.Count} plugins ship identical bytes; "
                    + $"resolving to '{winner.PluginName}' (load-order last).");
                continue;
            }

            var loserList = string.Join(", ", losers.Select(c => $"'{c.PluginName}'"));
            var message =
                $"Asset collision on '{relative}': {loserList} will be overwritten by "
                + $"'{winner.PluginName}' (later in load order).";

            ReportProgress(progress, message);
            LaunchDiagnostics.Log(message);
            Notifications.SendNotification(message, "Warning");
        }
    }

    private sealed record AssetClaim(string PluginName, string SourceAbsolutePath, string TargetRelativePath);

    private static string TryGetModRootRelativePath(string modRootFull, string destinationFull)
    {
        try
        {
            var relative = Path.GetRelativePath(modRootFull, destinationFull);
            return string.IsNullOrEmpty(relative) ? destinationFull : relative;
        }
        catch
        {
            return destinationFull;
        }
    }

    // Cheap structural compare: short-circuit on length mismatch, then stream
    // both files in matched chunks. No crypto -- mirrors the launcher's
    // existing "no hashing for parser-managed paths" stance.
    private static async Task<bool> AreAllSourcesIdenticalAsync(IReadOnlyList<AssetClaim> claims)
    {
        if (claims.Count < 2)
        {
            return true;
        }

        try
        {
            var firstInfo = new FileInfo(claims[0].SourceAbsolutePath);
            if (!firstInfo.Exists)
            {
                return false;
            }

            var firstLength = firstInfo.Length;
            for (var i = 1; i < claims.Count; i++)
            {
                var info = new FileInfo(claims[i].SourceAbsolutePath);
                if (!info.Exists || info.Length != firstLength)
                {
                    return false;
                }
            }

            const int bufferSize = 64 * 1024;
            var streams = new FileStream[claims.Count];
            try
            {
                for (var i = 0; i < claims.Count; i++)
                {
                    streams[i] = new FileStream(
                        claims[i].SourceAbsolutePath,
                        FileMode.Open, FileAccess.Read, FileShare.Read,
                        bufferSize, useAsync: true);
                }

                var firstBuffer = new byte[bufferSize];
                var otherBuffer = new byte[bufferSize];

                while (true)
                {
                    var firstRead = await streams[0].ReadAsync(firstBuffer.AsMemory(0, bufferSize));
                    if (firstRead == 0)
                    {
                        return true;
                    }

                    for (var i = 1; i < streams.Length; i++)
                    {
                        var totalRead = 0;
                        while (totalRead < firstRead)
                        {
                            var read = await streams[i].ReadAsync(otherBuffer.AsMemory(totalRead, firstRead - totalRead));
                            if (read == 0)
                            {
                                return false;
                            }
                            totalRead += read;
                        }

                        if (!firstBuffer.AsSpan(0, firstRead).SequenceEqual(otherBuffer.AsSpan(0, firstRead)))
                        {
                            return false;
                        }
                    }
                }
            }
            finally
            {
                foreach (var stream in streams)
                {
                    stream?.Dispose();
                }
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Applies the mod-root-scoped portion of every enabled plugin under
    /// <paramref name="modRoot"/>: missiles.json, monsters.json, the strings
    /// translation files, and all asset copies (including the animdata.d2 /
    /// exanimdata.d2 pair sync). Authoring warnings and per-plugin errors are
    /// also emitted from here so users see them exactly once per launch
    /// regardless of how many excel directories
    /// <see cref="ApplyEnabledPluginsExcelAsync"/> iterates over.
    /// </summary>
    public static async Task ApplyEnabledPluginsModRootAsync(string modRoot, IProgress<string>? progress = null)
    {
        MainWindow.Settings.CurrentProfile.Plugins ??= [];

        foreach (var registration in MainWindow.Settings.CurrentProfile.Plugins.Where(plugin => plugin.IsEnabled))
        {
            var pluginState = await LoadPluginStateAsync(registration);
            if (pluginState.Errors.Count > 0)
            {
                var message = $"Plugin '{pluginState.Name}' is invalid and was skipped.";
                ReportProgress(progress, message);
                Notifications.SendNotification(message, "Warning");
                continue;
            }

            foreach (var warning in pluginState.Warnings)
            {
                ReportProgress(progress, $"Plugin '{pluginState.Name}' warning: {warning}");
                Notifications.SendNotification($"Plugin '{pluginState.Name}': {warning}", "Warning");
            }

            ReportProgress(progress, $"Applying plugin {pluginState.Name}...");

            try
            {
                var parameters = pluginState.Parameters.ToDictionary(
                    parameter => parameter.Key,
                    parameter => parameter.Value,
                    StringComparer.OrdinalIgnoreCase);

                foreach (var pluginFile in pluginState.Files)
                {
                    var operations = await LoadPluginOperationsAsync(GetPluginFilePath(registration.Id, pluginFile.RelativePath));
                    var filtered = FilterConditionalOperations(operations, parameters, pluginState.Name, pluginFile.RelativePath);
                    if (filtered.Count == 0)
                    {
                        continue;
                    }

                    await ApplyModRootOperationsAsync(modRoot, filtered, parameters);
                }

                if (pluginState.Assets.Count > 0)
                {
                    await ApplyPluginAssetsAsync(modRoot, registration.Id, pluginState, progress);
                }
            }
            catch (Exception ex)
            {
                LaunchDiagnostics.LogException($"Failed to apply plugin '{pluginState.Name}'", ex);
                Notifications.SendNotification($"Plugin '{pluginState.Name}' failed: {ex.Message}", "Warning");
            }
        }
    }

    /// <summary>
    /// Pre-stages plugin asset copies whose target lies *directly* under the supplied
    /// <paramref name="excelDirectory"/>. Called from the per-excel-directory loop after
    /// the launcher's clean variant has been copied into place and before parser ops
    /// run, so plugin parser ops layer on top of the wholesale replacement
    /// (clean -> plugin asset -> launcher tweaks -> plugin parser ops).
    /// No backup is registered with <see cref="PluginAssetBackupService"/> because the
    /// launcher's clean-copy step is the recovery mechanism for excel files.
    /// </summary>
    public static Task ApplyEnabledPluginsExcelAssetsAsync(string modRoot, string excelDirectory, IProgress<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(modRoot) || string.IsNullOrWhiteSpace(excelDirectory))
        {
            return Task.CompletedTask;
        }

        var excelFull = Path.GetFullPath(excelDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Match assets whose direct parent directory is the supplied excel directory --
        // assets under <excel>/base are pre-staged on the base pass instead, so each
        // excel iteration only touches files that variant actually owns.
        return ApplyPreStagedAssetsAsync(
            modRoot,
            "excel",
            destinationFull => string.Equals(
                Path.GetDirectoryName(destinationFull),
                excelFull,
                StringComparison.OrdinalIgnoreCase),
            progress);
    }

    /// <summary>
    /// Pre-stages plugin asset copies whose target is the mod's missiles.json. Called
    /// after <c>RestoreMissilesFileAsync</c> and before <c>ApplyMissilesTweaksAsync</c>
    /// so launcher tweaks and plugin parser ops both layer on top of the plugin asset.
    /// </summary>
    public static Task ApplyEnabledPluginsMissilesAssetsAsync(string modRoot, IProgress<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(modRoot))
        {
            return Task.CompletedTask;
        }

        var missilesFull = Path.GetFullPath(Path.Combine(modRoot, "data", "hd", "missiles", MissilesTargetFileName));

        return ApplyPreStagedAssetsAsync(
            modRoot,
            "missiles",
            destinationFull => string.Equals(destinationFull, missilesFull, StringComparison.OrdinalIgnoreCase),
            progress);
    }

    /// <summary>
    /// Pre-stages plugin asset copies whose target is the mod's monsters.json. Called
    /// after <c>RestoreMonstersFileAsync</c> so plugin parser ops layer on top.
    /// </summary>
    public static Task ApplyEnabledPluginsMonstersAssetsAsync(string modRoot, IProgress<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(modRoot))
        {
            return Task.CompletedTask;
        }

        var monstersFull = Path.GetFullPath(Path.Combine(modRoot, "data", "hd", "character", MonstersTargetFileName));

        return ApplyPreStagedAssetsAsync(
            modRoot,
            "monsters",
            destinationFull => string.Equals(destinationFull, monstersFull, StringComparison.OrdinalIgnoreCase),
            progress);
    }

    /// <summary>
    /// Pre-stages plugin asset copies whose target is any .json file under
    /// <c>data/local/lng/strings</c>. Called after <c>RestoreStringsFromCleanCopyAsync</c>
    /// so plugin parser ops layer on top.
    /// </summary>
    public static Task ApplyEnabledPluginsStringsAssetsAsync(string modRoot, IProgress<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(modRoot))
        {
            return Task.CompletedTask;
        }

        var stringsRootFull = Path.GetFullPath(Path.Combine(modRoot, "data", "local", "lng", "strings"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var stringsPrefix = stringsRootFull + Path.DirectorySeparatorChar;

        return ApplyPreStagedAssetsAsync(
            modRoot,
            "strings",
            destinationFull =>
                destinationFull.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && destinationFull.StartsWith(stringsPrefix, StringComparison.OrdinalIgnoreCase),
            progress);
    }

    // Shared body for the four pre-stage entry points above. Iterates enabled
    // plugins in load order, evaluates each asset's condition, validates the
    // resolved destination stays inside the mod root, and copies via
    // FileCopyHelper -- no backup registration, because the caller has just
    // restored the destination from a launcher-managed clean copy.
    private static async Task ApplyPreStagedAssetsAsync(
        string modRoot,
        string scopeLabel,
        Func<string, bool> destinationIsInScope,
        IProgress<string>? progress)
    {
        MainWindow.Settings.CurrentProfile.Plugins ??= [];
        var modRootFull = Path.GetFullPath(modRoot);

        foreach (var registration in MainWindow.Settings.CurrentProfile.Plugins.Where(plugin => plugin.IsEnabled))
        {
            var pluginState = await LoadPluginStateAsync(registration);
            if (pluginState.Errors.Count > 0 || pluginState.Assets.Count == 0)
            {
                // Errors and authoring warnings are emitted once per launch from
                // ApplyEnabledPluginsModRootAsync -- skip silently here so users
                // do not see them N times across the four pre-stage passes.
                continue;
            }

            var parameterValues = pluginState.Parameters.ToDictionary(
                parameter => parameter.Key,
                parameter => parameter.Value,
                StringComparer.OrdinalIgnoreCase);

            var announced = false;
            foreach (var asset in pluginState.Assets)
            {
                string destinationFull;
                try
                {
                    destinationFull = Path.GetFullPath(Path.Combine(modRootFull, asset.TargetRelativePath));
                }
                catch
                {
                    // Path resolution failures are reported by the canonical
                    // ApplyPluginAssetsAsync pass; skip silently here.
                    continue;
                }

                if (!destinationIsInScope(destinationFull))
                {
                    continue;
                }

                if (asset.Condition != null && !EvaluateCondition(asset.Condition, parameterValues))
                {
                    LaunchDiagnostics.Log(
                        $"Plugin '{pluginState.Name}': skipped conditional asset '{asset.TargetRelativePath}' ({scopeLabel} pre-stage).");
                    continue;
                }

                try
                {
                    var relative = Path.GetRelativePath(modRootFull, destinationFull);
                    if (string.IsNullOrEmpty(relative) ||
                        relative.StartsWith("..", StringComparison.Ordinal) ||
                        Path.IsPathRooted(relative))
                    {
                        throw new InvalidDataException(
                            $"Asset target '{asset.TargetRelativePath}' resolves outside the mod folder.");
                    }

                    if (!announced)
                    {
                        ReportProgress(progress, $"Pre-staging {scopeLabel} assets for {pluginState.Name}...");
                        announced = true;
                    }

                    await FileCopyHelper.CopyFileAsync(asset.SourceAbsolutePath, destinationFull);
                    LaunchDiagnostics.Log(
                        $"Plugin '{pluginState.Name}': pre-staged {scopeLabel} asset '{asset.TargetRelativePath}'.");
                    ReportProgress(progress, $"Copied asset to {asset.TargetRelativePath}.");
                }
                catch (Exception ex)
                {
                    LaunchDiagnostics.LogException(
                        $"Failed to pre-stage {scopeLabel} asset '{asset.TargetRelativePath}' for plugin '{pluginState.Name}'", ex);
                    Notifications.SendNotification(
                        $"Plugin '{pluginState.Name}': failed to pre-stage asset '{asset.TargetRelativePath}': {ex.Message}",
                        "Warning");
                }
            }
        }
    }

    // Returns true if the supplied absolute destination sits inside one of the
    // launcher's clean-copy-managed scopes (excel, missiles, monsters, strings).
    // Such destinations are pre-staged earlier in the launch pipeline and must
    // be skipped by ApplyPluginAssetsAsync to avoid double-writing the file and
    // registering a redundant backup -- restoration is handled by the next
    // launch's clean-copy step rather than by PluginAssetBackupService.
    private static bool IsAssetTargetCleanCovered(string modRootFull, string destinationFull)
    {
        var excelDir = Path.GetFullPath(Path.Combine(modRootFull, "data", "global", "excel"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (destinationFull.StartsWith(excelDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var missilesFull = Path.GetFullPath(Path.Combine(modRootFull, "data", "hd", "missiles", MissilesTargetFileName));
        if (string.Equals(destinationFull, missilesFull, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var monstersFull = Path.GetFullPath(Path.Combine(modRootFull, "data", "hd", "character", MonstersTargetFileName));
        if (string.Equals(destinationFull, monstersFull, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var stringsRootFull = Path.GetFullPath(Path.Combine(modRootFull, "data", "local", "lng", "strings"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (destinationFull.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && destinationFull.StartsWith(stringsRootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    // Drops any operation whose declarative condition evaluates to false
    // against the plugin's effective parameter values; preserves the original
    // operation order for the remaining entries so the apply pipeline behaves
    // identically when no conditions are present.
    private static List<PluginJsonOperation> FilterConditionalOperations(
        IReadOnlyList<PluginJsonOperation> operations,
        IReadOnlyDictionary<string, string> parameters,
        string pluginName,
        string pluginRelativePath)
    {
        var filtered = new List<PluginJsonOperation>(operations.Count);
        foreach (var op in operations)
        {
            if (op.Condition != null && !EvaluateCondition(op.Condition, parameters))
            {
                LaunchDiagnostics.Log(
                    $"Plugin '{pluginName}': skipped conditional operation in '{pluginRelativePath}' targeting '{op.File}'.");
                continue;
            }

            filtered.Add(op);
        }

        return filtered;
    }

    // Pair of binary files D2R reads as a unit. If a plugin replaces only one
    // of the two, the launcher mirrors the replacement onto the other so the
    // game does not see a mismatched pair (see SyncAnimDataPairAsync below).
    private const string AnimDataRelativePath = "data/global/animdata.d2";
    private const string ExAnimDataRelativePath = "data/global/exanimdata.d2";

    private static async Task ApplyPluginAssetsAsync(string modRoot, string pluginId, PluginState pluginState, IProgress<string>? progress)
    {
        if (string.IsNullOrWhiteSpace(modRoot))
        {
            ReportProgress(progress, $"Skipped assets for plugin '{pluginState.Name}': mod root could not be resolved.");
            return;
        }

        ReportProgress(progress, $"Applying plugin assets for {pluginState.Name}...");

        // Resolve the mod root once; every asset is validated against it.
        var modRootFull = Path.GetFullPath(modRoot);

        // Asset conditions are evaluated against the same effective parameter map used by
        // operations, so authors can drive both excel edits and asset replacements from the same
        // checkbox(es).
        var parameterValues = pluginState.Parameters.ToDictionary(
            parameter => parameter.Key,
            parameter => parameter.Value,
            StringComparer.OrdinalIgnoreCase);

        // Track which of the animdata.d2 / exanimdata.d2 pair were successfully
        // written by this plugin's assets so we can mirror a single-sided edit
        // onto its twin once the regular asset loop finishes.
        var animDataWritten = false;
        var exAnimDataWritten = false;

        foreach (var asset in pluginState.Assets)
        {
            if (asset.Condition != null && !EvaluateCondition(asset.Condition, parameterValues))
            {
                LaunchDiagnostics.Log(
                    $"Plugin '{pluginState.Name}': skipped conditional asset '{asset.TargetRelativePath}'.");
                continue;
            }

            try
            {
                var destinationPath = Path.GetFullPath(Path.Combine(modRootFull, asset.TargetRelativePath));

                // Use Path.GetRelativePath so we can detect both `..` traversal
                // and sibling-prefix attacks (e.g. "<modRoot>-evil\foo").
                var relative = Path.GetRelativePath(modRootFull, destinationPath);
                if (string.IsNullOrEmpty(relative) ||
                    relative.StartsWith("..", StringComparison.Ordinal) ||
                    Path.IsPathRooted(relative))
                {
                    throw new InvalidDataException(
                        $"Asset target '{asset.TargetRelativePath}' resolves outside the mod folder.");
                }

                // Destinations covered by a launcher-managed clean copy are
                // pre-staged earlier in the launch pipeline (before parser ops
                // run) so plugin parser ops can layer on top of the wholesale
                // replacement. Skip them here to avoid double-writing the file
                // and registering a redundant backup; the next launch's
                // clean-copy step is the recovery mechanism, not the backup
                // service.
                if (IsAssetTargetCleanCovered(modRootFull, destinationPath))
                {
                    LaunchDiagnostics.Log(
                        $"Plugin '{pluginState.Name}': asset '{asset.TargetRelativePath}' was pre-staged earlier; skipping mod-root pass.");
                    continue;
                }

                // Capture the pre-plugin original (if any) before we overwrite it,
                // so the file can be restored when the plugin is disabled or deleted.
                await PluginAssetBackupService.RegisterReplacementAsync(pluginId, destinationPath);
                await FileCopyHelper.CopyFileAsync(asset.SourceAbsolutePath, destinationPath);

                if (IsAnimDataPairTarget(relative, AnimDataRelativePath))
                {
                    animDataWritten = true;
                }
                else if (IsAnimDataPairTarget(relative, ExAnimDataRelativePath))
                {
                    exAnimDataWritten = true;
                }

                ReportProgress(progress, $"Copied asset to {asset.TargetRelativePath}.");
            }
            catch (Exception ex)
            {
                LaunchDiagnostics.LogException(
                    $"Failed to apply asset '{asset.TargetRelativePath}' for plugin '{pluginState.Name}'", ex);
                Notifications.SendNotification(
                    $"Plugin '{pluginState.Name}': failed to apply asset '{asset.TargetRelativePath}': {ex.Message}",
                    "Warning");
                ReportProgress(progress, $"Asset '{asset.TargetRelativePath}' failed: {ex.Message}");
            }
        }

        await SyncAnimDataPairAsync(modRootFull, pluginId, pluginState, animDataWritten, exAnimDataWritten, progress);
    }

    // Path-equality helper used to recognise the animdata.d2 / exanimdata.d2
    // targets regardless of whether the author wrote forward or back slashes.
    private static bool IsAnimDataPairTarget(string relativePath, string expectedRelativePath)
    {
        var normalizedActual = relativePath.Replace('\\', '/');
        return string.Equals(normalizedActual, expectedRelativePath, StringComparison.OrdinalIgnoreCase);
    }

    // animdata.d2 and exanimdata.d2 are the same kind of binary table read by
    // D2R as a pair; if a plugin only replaces one of them the game can end up
    // with mismatched animation entries. When exactly one of the pair was
    // written by this plugin's assets, mirror the written file onto its twin
    // so both sides stay aligned. The pre-existing twin is registered with the
    // backup service first so it can be restored when the plugin is disabled.
    private static async Task SyncAnimDataPairAsync(
        string modRootFull,
        string pluginId,
        PluginState pluginState,
        bool animDataWritten,
        bool exAnimDataWritten,
        IProgress<string>? progress)
    {
        if (animDataWritten == exAnimDataWritten)
        {
            // Both written (author covered the pair) or neither written (this
            // plugin did not touch the pair at all) — nothing to mirror.
            return;
        }

        var sourceRelative = animDataWritten ? AnimDataRelativePath : ExAnimDataRelativePath;
        var twinRelative = animDataWritten ? ExAnimDataRelativePath : AnimDataRelativePath;
        var sourceAbsolute = Path.GetFullPath(Path.Combine(modRootFull, sourceRelative));
        var twinAbsolute = Path.GetFullPath(Path.Combine(modRootFull, twinRelative));

        if (!File.Exists(sourceAbsolute))
        {
            // The asset copy reported success but the file is gone — bail out
            // rather than overwrite the twin with a non-existent payload.
            return;
        }

        try
        {
            await PluginAssetBackupService.RegisterReplacementAsync(pluginId, twinAbsolute);
            await FileCopyHelper.CopyFileAsync(sourceAbsolute, twinAbsolute);

            LaunchDiagnostics.Log(
                $"Plugin '{pluginState.Name}': mirrored {sourceRelative} onto {twinRelative} to keep the animdata pair in sync.");
            ReportProgress(progress,
                $"Mirrored {sourceRelative} onto {twinRelative} so the animdata pair stays in sync.");
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException(
                $"Failed to mirror {sourceRelative} onto {twinRelative} for plugin '{pluginState.Name}'", ex);
            Notifications.SendNotification(
                $"Plugin '{pluginState.Name}': failed to mirror {sourceRelative} onto {twinRelative}: {ex.Message}",
                "Warning");
            ReportProgress(progress,
                $"Mirror of {sourceRelative} onto {twinRelative} failed: {ex.Message}");
        }
    }

    public static string GetSupportedTargetsSummary()
    {
        return "All .txt files in the base excel folder are supported except itemstatcost.txt. Most files match rows by a unique column; files with duplicate values in their identifier column use a numeric row ID (0-based data row index) instead. Multiply-existing and append operations can reference parameters declared in plugininfo.json. String JSON files from data/local/lng/strings (e.g. item-runes.json) are also supported using the same flat d2rr-style layout: each entry lists the target file, the D2R Key, and one or more language fields (enUS, zhTW, deDE, esES, frFR, itIT, koKR, plPL, esMX, jaJP, ptBR, ruRU, zhCN); only the listed languages are replaced and any other languages on that entry are left untouched. The missiles.json file at data/hd/missiles/missiles.json is also supported: each entry lists the target file, a Key, and an updatedValue (or parameterKey) to write; addRow appends a new key/value pair while preserving the existing JSON formatting. The monsters.json file at data/hd/character/monsters.json shares the same layout and is supported with the same {file, Key, updatedValue|parameterKey, [operation]} shape and addRow semantics.";
    }

    // Dispatches the subset of plugin operations whose target lives inside the
    // excel directory (i.e. .txt files routed through ParserRegistry).
    // Mod-root-relative targets are intentionally ignored here; they are
    // dispatched by ApplyModRootOperationsAsync exactly once per launch.
    private static async Task ApplyExcelOperationsAsync(
        string excelDirectory,
        IReadOnlyList<PluginJsonOperation> operations,
        IReadOnlyDictionary<string, string> parameters)
    {
        var fileNames = operations
            .Where(operation => !string.IsNullOrWhiteSpace(operation.File) && IsSupportedTargetFile(operation.File))
            .Select(operation => operation.File!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var fileName in fileNames)
        {
            if (ParserRegistry.TryGetValue(fileName, out var registration))
            {
                await registration.ApplyAsync(excelDirectory, operations, parameters);
            }
        }
    }

    // Dispatches the subset of plugin operations whose target lives outside
    // the excel directory but under the mod root (missiles.json,
    // monsters.json, the strings translation files). Excel parser operations
    // are intentionally ignored here; they are dispatched by
    // ApplyExcelOperationsAsync once per excel directory.
    private static async Task ApplyModRootOperationsAsync(
        string modRoot,
        IReadOnlyList<PluginJsonOperation> operations,
        IReadOnlyDictionary<string, string> parameters)
    {
        var fileNames = operations
            .Where(operation => !string.IsNullOrWhiteSpace(operation.File) && IsSupportedTargetFile(operation.File))
            .Select(operation => operation.File!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        string? resolvedStringsDirectory = null;

        foreach (var fileName in fileNames)
        {
            if (IsMissilesTargetFile(fileName))
            {
                var missilesFilePath = ResolveMissilesFilePathFromModRoot(modRoot)
                    ?? throw new FileNotFoundException(
                        $"Could not resolve {MissilesTargetFileName} ({MissilesRelativePath}) relative to mod root '{modRoot}'.");

                await ApplyMissilesOperationsAsync(missilesFilePath, operations, parameters);
                continue;
            }

            if (IsMonstersTargetFile(fileName))
            {
                var monstersFilePath = ResolveMonstersFilePathFromModRoot(modRoot)
                    ?? throw new FileNotFoundException(
                        $"Could not resolve {MonstersTargetFileName} ({MonstersRelativePath}) relative to mod root '{modRoot}'.");

                await ApplyMonstersOperationsAsync(monstersFilePath, operations, parameters);
                continue;
            }

            if (IsStringsTargetFile(fileName))
            {
                resolvedStringsDirectory ??= ResolveStringsDirectoryFromModRoot(modRoot)
                    ?? throw new DirectoryNotFoundException(
                        $"Could not resolve the strings directory ({StringsDirectoryRelativePath}) relative to mod root '{modRoot}'.");

                await ApplyStringsOperationsForTargetAsync(resolvedStringsDirectory, operations, fileName);
            }
        }
    }

    private static string? ResolveMissilesFilePathFromModRoot(string modRoot)
    {
        if (string.IsNullOrWhiteSpace(modRoot))
        {
            return null;
        }

        return Path.Combine(modRoot, "data", "hd", "missiles", MissilesTargetFileName);
    }

    private static string? ResolveMonstersFilePathFromModRoot(string modRoot)
    {
        if (string.IsNullOrWhiteSpace(modRoot))
        {
            return null;
        }

        return Path.Combine(modRoot, "data", "hd", "character", MonstersTargetFileName);
    }

    private static string? ResolveStringsDirectoryFromModRoot(string modRoot)
    {
        if (string.IsNullOrWhiteSpace(modRoot))
        {
            return null;
        }

        return Path.Combine(modRoot, "data", "local", "lng", "strings");
    }

    // Replace-by-key and addRow dispatcher for missiles.json. The file is a single JSON object
    // mapping a missile key to a string asset; edits go through MissilesFileParser so the original
    // property order and surrounding entries are preserved on disk.
    private static async Task ApplyMissilesOperationsAsync(
        string missilesFilePath,
        IReadOnlyList<PluginJsonOperation> operations,
        IReadOnlyDictionary<string, string> parameters)
    {
        var targetOperations = operations
            .Where(operation => IsMissilesTargetFile(operation.File))
            .ToList();

        if (targetOperations.Count == 0)
        {
            return;
        }

        if (!File.Exists(missilesFilePath))
        {
            throw new FileNotFoundException($"{MissilesTargetFileName} was not found at: {missilesFilePath}");
        }

        var parser = new MissilesFileParser(missilesFilePath);

        foreach (var operation in targetOperations)
        {
            if (string.IsNullOrWhiteSpace(operation.Key))
            {
                throw new InvalidDataException($"A {MissilesTargetFileName} entry is missing its Key.");
            }

            var resolvedValue = ResolveMissileValue(operation, parameters);
            var isAddRow = !string.IsNullOrWhiteSpace(operation.Operation)
                           && operation.Operation.Equals("addRow", StringComparison.OrdinalIgnoreCase);

            if (isAddRow)
            {
                await parser.AddMissileAsync(operation.Key!, resolvedValue);
                continue;
            }

            var matched = await parser.ReplaceMissileValueAsync(operation.Key!, resolvedValue);
            if (!matched)
            {
                throw new InvalidDataException(
                    $"Could not find entry with Key '{operation.Key}' in {MissilesTargetFileName}.");
            }
        }
    }

    // Picks the value to write for a missiles operation: prefer an explicit updatedValue, otherwise
    // resolve a parameterKey against the plugin's parameters. Both forms support {{parameter:key}}
    // tokens so missile values can be parameterized.
    private static string ResolveMissileValue(
        PluginJsonOperation operation,
        IReadOnlyDictionary<string, string> parameters)
    {
        string? rawValue = null;
        if (!string.IsNullOrEmpty(operation.UpdatedValue))
        {
            rawValue = operation.UpdatedValue;
        }
        else if (!string.IsNullOrWhiteSpace(operation.ParameterKey)
                 && parameters.TryGetValue(operation.ParameterKey, out var parameterValue))
        {
            rawValue = parameterValue;
        }

        if (string.IsNullOrEmpty(rawValue))
        {
            throw new InvalidDataException(
                $"The {MissilesTargetFileName} entry for Key '{operation.Key}' does not provide a value (set 'updatedValue' or a 'parameterKey').");
        }

        return ParameterTokenRegex.Replace(rawValue, match =>
        {
            var parameterKey = match.Groups[1].Value;
            return parameters.TryGetValue(parameterKey, out var resolved) ? resolved : match.Value;
        });
    }

    private static bool IsMissilesTargetFile(string? fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName)
               && string.Equals(fileName, MissilesTargetFileName, StringComparison.OrdinalIgnoreCase);
    }


    // Replace-by-key and addRow dispatcher for monsters.json. Mirrors ApplyMissilesOperationsAsync:
    // monsters.json shares the missiles.json layout (flat key->asset string map with a leading
    // 'dependencies' object), so edits are routed through MonstersFileParser to keep the original
    // property order and surrounding entries intact on disk.
    private static async Task ApplyMonstersOperationsAsync(
        string monstersFilePath,
        IReadOnlyList<PluginJsonOperation> operations,
        IReadOnlyDictionary<string, string> parameters)
    {
        var targetOperations = operations
            .Where(operation => IsMonstersTargetFile(operation.File))
            .ToList();

        if (targetOperations.Count == 0)
        {
            return;
        }

        if (!File.Exists(monstersFilePath))
        {
            throw new FileNotFoundException($"{MonstersTargetFileName} was not found at: {monstersFilePath}");
        }

        var parser = new MonstersFileParser(monstersFilePath);

        foreach (var operation in targetOperations)
        {
            if (string.IsNullOrWhiteSpace(operation.Key))
            {
                throw new InvalidDataException($"A {MonstersTargetFileName} entry is missing its Key.");
            }

            var resolvedValue = ResolveMonsterValue(operation, parameters);
            var isAddRow = !string.IsNullOrWhiteSpace(operation.Operation)
                           && operation.Operation.Equals("addRow", StringComparison.OrdinalIgnoreCase);

            if (isAddRow)
            {
                await parser.AddMonsterAsync(operation.Key!, resolvedValue);
                continue;
            }

            var matched = await parser.ReplaceMonsterValueAsync(operation.Key!, resolvedValue);
            if (!matched)
            {
                throw new InvalidDataException(
                    $"Could not find entry with Key '{operation.Key}' in {MonstersTargetFileName}.");
            }
        }
    }

    // Picks the value to write for a monsters operation: prefer an explicit updatedValue, otherwise
    // resolve a parameterKey against the plugin's parameters. Both forms support {{parameter:key}}
    // tokens so monster values can be parameterized. Mirrors ResolveMissileValue.
    private static string ResolveMonsterValue(
        PluginJsonOperation operation,
        IReadOnlyDictionary<string, string> parameters)
    {
        string? rawValue = null;
        if (!string.IsNullOrEmpty(operation.UpdatedValue))
        {
            rawValue = operation.UpdatedValue;
        }
        else if (!string.IsNullOrWhiteSpace(operation.ParameterKey)
                 && parameters.TryGetValue(operation.ParameterKey, out var parameterValue))
        {
            rawValue = parameterValue;
        }

        if (string.IsNullOrEmpty(rawValue))
        {
            throw new InvalidDataException(
                $"The {MonstersTargetFileName} entry for Key '{operation.Key}' does not provide a value (set 'updatedValue' or a 'parameterKey').");
        }

        return ParameterTokenRegex.Replace(rawValue, match =>
        {
            var parameterKey = match.Groups[1].Value;
            return parameters.TryGetValue(parameterKey, out var resolved) ? resolved : match.Value;
        });
    }

    private static bool IsMonstersTargetFile(string? fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName)
               && string.Equals(fileName, MonstersTargetFileName, StringComparison.OrdinalIgnoreCase);
    }


    private static async Task ApplyStringsOperationsForTargetAsync(
        string stringsDirectory,
        IReadOnlyList<PluginJsonOperation> operations,
        string fileName)
    {
        var targetOperations = operations
            .Where(operation => string.Equals(operation.File, fileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (targetOperations.Count == 0)
        {
            return;
        }

        var filePath = Path.Combine(stringsDirectory, fileName);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"{fileName} was not found in the target strings directory: {stringsDirectory}");
        }

        var parser = new TranslationFileParser(filePath);

        foreach (var operation in targetOperations)
        {
            if (string.IsNullOrWhiteSpace(operation.Key))
            {
                throw new InvalidDataException($"A {fileName} entry is missing its Key.");
            }

            var languageValues = operation.LanguageValues;
            if (languageValues == null || languageValues.Count == 0)
            {
                throw new InvalidDataException(
                    $"The {fileName} entry for Key '{operation.Key}' does not list any language fields to replace.");
            }

            var matchedAny = false;
            foreach (var pair in languageValues)
            {
                var matched = await parser.ReplaceLanguageValueAsync(
                    operation.Key!,
                    pair.Key,
                    pair.Value);

                matchedAny |= matched;
            }

            if (!matchedAny)
            {
                throw new InvalidDataException(
                    $"Could not find entry with Key '{operation.Key}' in {fileName}.");
            }
        }
    }

    private static bool IsStringsTargetFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        // missiles.json lives under data/hd/missiles and monsters.json under data/hd/character;
        // both use their own flat-map JSON layout, so route them to the dedicated dispatchers
        // instead of treating them as strings translation files.
        if (IsMissilesTargetFile(fileName) || IsMonstersTargetFile(fileName))
        {
            return false;
        }

        return fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }


    private static async Task ApplyOperationsForTargetAsync<TEntry>(
        string excelDirectory,
        IReadOnlyList<PluginJsonOperation> operations,
        IReadOnlyDictionary<string, string> parameters,
        string fileName,
        string rowIdentifierPropertyName,
        Func<TEntry, string?> rowIdentifierSelector,
        Func<string, Task<IList<TEntry>>> loadEntriesAsync,
        Func<IList<TEntry>, string, string, CancellationToken, Task<FileInfo>> saveEntriesAsync,
        Func<string, PropertyInfo?> resolveColumn,
        bool usesRowId = false)
        where TEntry : class, new()
    {
        var targetOperations = operations
            .Where(operation => string.Equals(operation.File, fileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (targetOperations.Count == 0)
        {
            return;
        }

        var filePath = Path.Combine(excelDirectory, fileName);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"{fileName} was not found in the target excel directory: {excelDirectory}");
        }

        var entries = await loadEntriesAsync(filePath);
        if (entries.Count == 0)
        {
            throw new InvalidDataException($"{fileName} did not contain any editable rows for plugin execution.");
        }

        foreach (var operation in targetOperations)
        {
            var assignments = GetColumnAssignments(operation);

            if (string.Equals(operation.Operation, "cloneRow", StringComparison.OrdinalIgnoreCase))
            {
                if (operation.RowMatchers is { Count: > 0 })
                {
                    throw new InvalidDataException(
                        $"cloneRow operation for {fileName} does not support the array form of 'rowIdentifier'. Use a single string or object so the launcher knows which row to copy or overwrite.");
                }

                if (string.IsNullOrWhiteSpace(operation.SourceRowIdentifier))
                {
                    throw new InvalidDataException(
                        $"cloneRow operation for {fileName} must specify 'sourceRowIdentifier' (numeric index or matching value of the default rowIdentifier column).");
                }

                var sourceIndex = ResolveSingleRowIndex(
                    entries, operation.SourceRowIdentifier!, rowIdentifierSelector, fileName, "sourceRowIdentifier");
                var clonedEntry = CloneEntry(entries[sourceIndex]);

                var isReplaceMode = !string.IsNullOrWhiteSpace(operation.Mode)
                                    && operation.Mode.Equals("replace", StringComparison.OrdinalIgnoreCase);

                int targetIndex;
                if (isReplaceMode)
                {
                    if (string.IsNullOrWhiteSpace(operation.RowIdentifier))
                    {
                        throw new InvalidDataException(
                            $"cloneRow (mode='replace') for {fileName} must specify 'rowIdentifier' for the row to overwrite.");
                    }

                    targetIndex = ResolveSingleRowIndex(
                        entries, operation.RowIdentifier!, rowIdentifierSelector, fileName, "rowIdentifier");
                    entries[targetIndex] = clonedEntry;
                }
                else
                {
                    // "add" mode (default): always appends the cloned row to the end of the file.
                    // Insertion at a specific index is not supported; rowIdentifier must be omitted.
                    if (!string.IsNullOrWhiteSpace(operation.RowIdentifier))
                    {
                        throw new InvalidDataException(
                            $"cloneRow (mode='add') for {fileName} does not support insertion at a specific index; omit 'rowIdentifier' so the cloned row is appended to the end. Use mode='replace' with 'rowIdentifier' to overwrite an existing row instead.");
                    }

                    entries.Add(clonedEntry);
                    targetIndex = entries.Count - 1;
                }

                // Apply column overrides on top of the cloned row, mirroring addRow's per-column
                // pipeline so authors get the standard replace/append/multiplyExisting operators.
                var cloneParent = operation with { Operation = null };
                foreach (var assignment in assignments)
                {
                    var perColumnOp = BuildPerColumnOperation(cloneParent, assignment, defaultOperation: "replace");
                    entries[targetIndex] = UpdateRecord(
                        entries[targetIndex],
                        perColumnOp.Column ?? string.Empty,
                        ResolveOperationValue(entries[targetIndex], perColumnOp, parameters, fileName, resolveColumn),
                        fileName,
                        resolveColumn,
                        operation.RowIdentifier);
                }

                continue;
            }

            if (string.Equals(operation.Operation, "swapRow", StringComparison.OrdinalIgnoreCase))
            {
                if (operation.RowMatchers is { Count: > 0 })
                {
                    throw new InvalidDataException(
                        $"swapRow operation for {fileName} does not support the array form of 'rowIdentifier'. Use a single string or object — swapRow always exchanges exactly two rows.");
                }

                if (string.IsNullOrWhiteSpace(operation.RowIdentifier))
                {
                    throw new InvalidDataException(
                        $"swapRow operation for {fileName} must specify 'rowIdentifier' for the first row to swap.");
                }

                if (string.IsNullOrWhiteSpace(operation.SwapRowIdentifier))
                {
                    throw new InvalidDataException(
                        $"swapRow operation for {fileName} must specify 'swapRowIdentifier' for the second row to swap.");
                }

                var indexA = ResolveSingleRowIndex(
                    entries, operation.RowIdentifier!, rowIdentifierSelector, fileName, "rowIdentifier");
                var indexB = ResolveSingleRowIndex(
                    entries, operation.SwapRowIdentifier!, rowIdentifierSelector, fileName, "swapRowIdentifier");

                if (indexA == indexB)
                {
                    throw new InvalidDataException(
                        $"swapRow operation for {fileName} cannot swap a row with itself ('{operation.RowIdentifier}' resolved to the same row as '{operation.SwapRowIdentifier}').");
                }

                (entries[indexA], entries[indexB]) = (entries[indexB], entries[indexA]);

                // After the swap, "columns" (the standard assignments) target the row now at indexA
                // (originally at indexB) and "swapColumns" target the row now at indexB. This makes
                // the post-swap intent explicit per the row at that final position.
                var swapParent = operation with { Operation = null };
                foreach (var assignment in assignments)
                {
                    var perColumnOp = BuildPerColumnOperation(swapParent, assignment, defaultOperation: "replace");
                    entries[indexA] = UpdateRecord(
                        entries[indexA],
                        perColumnOp.Column ?? string.Empty,
                        ResolveOperationValue(entries[indexA], perColumnOp, parameters, fileName, resolveColumn),
                        fileName,
                        resolveColumn,
                        operation.RowIdentifier);
                }

                if (operation.SwapColumns is { Count: > 0 } swapAssignments)
                {
                    foreach (var assignment in swapAssignments)
                    {
                        var perColumnOp = BuildPerColumnOperation(swapParent, assignment, defaultOperation: "replace");
                        entries[indexB] = UpdateRecord(
                            entries[indexB],
                            perColumnOp.Column ?? string.Empty,
                            ResolveOperationValue(entries[indexB], perColumnOp, parameters, fileName, resolveColumn),
                            fileName,
                            resolveColumn,
                            operation.SwapRowIdentifier);
                    }
                }

                continue;
            }

            if (string.Equals(operation.Operation, "addRow", StringComparison.OrdinalIgnoreCase))
            {
                if (operation.RowMatchers is { Count: > 0 })
                {
                    throw new InvalidDataException(
                        $"addRow operation for {fileName} does not support the array form of 'rowIdentifier'. Use a single numeric 0-based index, or omit rowIdentifier to append at the end.");
                }

                if (assignments.Count == 0)
                {
                    throw new InvalidDataException(
                        $"addRow operation for {fileName} must specify at least one column (use 'columns' array or 'column'/'updatedValue').");
                }

                var newEntry = new TEntry();
                // For addRow, each column assignment is materialized into the new row via the
                // standard value-resolution pipeline. The parent's Operation ("addRow") is not a
                // value-producing operation, so we strip it before building per-column ops; this
                // lets per-assignment Operation overrides win and otherwise falls back to "replace".
                var addRowParent = operation with { Operation = null };
                foreach (var assignment in assignments)
                {
                    var perColumnOp = BuildPerColumnOperation(addRowParent, assignment, defaultOperation: "replace");
                    newEntry = UpdateRecord(
                        newEntry,
                        perColumnOp.Column ?? string.Empty,
                        ResolveOperationValue(newEntry, perColumnOp, parameters, fileName, resolveColumn),
                        fileName,
                        resolveColumn,
                        operation.RowIdentifier);
                }

                int insertIndex;
                if (string.IsNullOrWhiteSpace(operation.RowIdentifier))
                {
                    insertIndex = entries.Count;
                }
                else if (int.TryParse(operation.RowIdentifier, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedIndex))
                {
                    if (parsedIndex < 0 || parsedIndex > entries.Count)
                    {
                        throw new InvalidDataException(
                            $"addRow rowIdentifier '{operation.RowIdentifier}' is out of bounds for {fileName}. Valid range is 0 to {entries.Count} (inclusive, where {entries.Count} appends to the end).");
                    }

                    insertIndex = parsedIndex;
                }
                else
                {
                    throw new InvalidDataException(
                        $"addRow rowIdentifier '{operation.RowIdentifier}' must be a numeric 0-based index or empty (to append).");
                }

                if (insertIndex == entries.Count)
                {
                    entries.Add(newEntry);
                }
                else
                {
                    entries.Insert(insertIndex, newEntry);
                }

                continue;
            }

            List<int> matchingIndices;

            if (operation.RowMatchers is { Count: > 0 } rowMatchers)
            {
                // Array form: each element is OR-ed into the final row set, scalar elements use
                // the same parsing rules as a single-string rowIdentifier (range, numeric index on
                // usesRowId files, or default identifier-column equality), and object elements
                // delegate to the multi-column AND matcher. Indices are de-duplicated so a row
                // matched by two elements is still updated only once per assignment.
                matchingIndices = ResolveRowMatcherIndices(
                    entries,
                    rowMatchers,
                    rowIdentifierSelector,
                    resolveColumn,
                    fileName,
                    rowIdentifierPropertyName,
                    usesRowId);
            }
            else if (operation.RowIdentifiers is { Count: > 0 } identifierMap)
            {
                // Multi-column identifier override: a row matches only when every listed
                // column equals the supplied value (case-insensitive). This bypasses the
                // file's default identifier column and any usesRowId numeric requirement.
                matchingIndices = Enumerable.Range(0, entries.Count)
                    .Where(i => MatchesAllRowIdentifiers(entries[i], identifierMap, resolveColumn))
                    .ToList();

                if (matchingIndices.Count == 0)
                {
                    var description = string.Join(", ", identifierMap.Select(p => $"{p.Key}='{p.Value}'"));
                    throw new InvalidDataException(
                        $"Could not find row in {fileName} matching identifiers: {description}.");
                }
            }
            else if (TryParseRowRange(operation.RowIdentifier, out var rangeStart, out var rangeEnd))
            {
                if (rangeStart > rangeEnd)
                {
                    (rangeStart, rangeEnd) = (rangeEnd, rangeStart);
                }

                if (rangeStart < 0 || rangeEnd >= entries.Count)
                {
                    throw new InvalidDataException(
                        $"Row range '{operation.RowIdentifier}' is out of bounds for {fileName}. Valid range is 0 to {entries.Count - 1}.");
                }

                matchingIndices = Enumerable.Range(rangeStart, rangeEnd - rangeStart + 1).ToList();
            }
            else if (usesRowId)
            {
                if (!int.TryParse(operation.RowIdentifier, out var rowIndex) || rowIndex < 0 || rowIndex >= entries.Count)
                {
                    throw new InvalidDataException(
                        $"Row ID '{operation.RowIdentifier}' is not a valid index for {fileName}. Valid range is 0 to {entries.Count - 1}.");
                }

                matchingIndices = [rowIndex];
            }
            else
            {
                matchingIndices = Enumerable.Range(0, entries.Count)
                    .Where(i => string.Equals(rowIdentifierSelector(entries[i]), operation.RowIdentifier, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matchingIndices.Count == 0)
                {
                    throw new InvalidDataException(
                        $"Could not find row '{operation.RowIdentifier}' in {fileName} using the {rowIdentifierPropertyName} column.");
                }
            }

            if (assignments.Count == 0)
            {
                throw new InvalidDataException(
                    $"Operation for {fileName} (row '{operation.RowIdentifier}') has no column to update. Provide 'column' or a non-empty 'columns' array.");
            }

            foreach (var entryIndex in matchingIndices)
            {
                foreach (var assignment in assignments)
                {
                    var perColumnOp = BuildPerColumnOperation(operation, assignment, defaultOperation: operation.Operation);
                    entries[entryIndex] = UpdateRecord(
                        entries[entryIndex],
                        perColumnOp.Column ?? string.Empty,
                        ResolveOperationValue(entries[entryIndex], perColumnOp, parameters, fileName, resolveColumn),
                        fileName,
                        resolveColumn,
                        operation.RowIdentifier);
                }
            }
        }

        await SaveGeneratedEntriesAsync(entries, filePath, saveEntriesAsync);
    }

    // Returns the effective list of column assignments for an operation. When a 'columns' array is
    // provided it is used as-is; otherwise the operation's top-level column/updatedValue/parameterKey
    // become a single implicit assignment. The list will be empty when neither is supplied.
    private static IReadOnlyList<PluginJsonColumnAssignment> GetColumnAssignments(PluginJsonOperation operation)
    {
        if (operation.Columns is { Count: > 0 } columns)
        {
            return columns;
        }

        if (!string.IsNullOrWhiteSpace(operation.Column))
        {
            return [new PluginJsonColumnAssignment(operation.Column, operation.UpdatedValue, operation.ParameterKey, operation.Operation)];
        }

        return Array.Empty<PluginJsonColumnAssignment>();
    }

    // Produces an effective single-column PluginJsonOperation by overlaying a column assignment on
    // top of its parent operation, so the existing ResolveOperationValue/UpdateRecord helpers can be
    // reused unchanged for multi-column and addRow execution paths.
    private static PluginJsonOperation BuildPerColumnOperation(
        PluginJsonOperation parent,
        PluginJsonColumnAssignment assignment,
        string? defaultOperation)
    {
        var operationName = !string.IsNullOrWhiteSpace(assignment.Operation)
            ? assignment.Operation
            : !string.IsNullOrWhiteSpace(parent.Operation) ? parent.Operation : defaultOperation;

        return parent with
        {
            Column = assignment.Column,
            UpdatedValue = assignment.UpdatedValue ?? parent.UpdatedValue,
            ParameterKey = assignment.ParameterKey ?? parent.ParameterKey,
            Operation = operationName,
            Columns = null
        };
    }

    // Returns true when every key/value pair in the supplied identifier map matches the entry's
    // corresponding column (case-insensitive). Unknown columns or missing values fail the match
    // so authors get a clear "row not found" error rather than a false positive.
    private static bool MatchesAllRowIdentifiers<TEntry>(
        TEntry entry,
        IReadOnlyDictionary<string, string> identifiers,
        Func<string, PropertyInfo?> resolveColumn)
    {
        foreach (var pair in identifiers)
        {
            var property = resolveColumn(pair.Key);
            if (property == null)
            {
                return false;
            }

            var actual = property.GetValue(entry);
            var actualText = actual switch
            {
                null => string.Empty,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => actual.ToString() ?? string.Empty
            };

            if (!string.Equals(actualText, pair.Value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    // Resolves the canonical "array form" of rowIdentifier into a deduplicated list of row
    // indices. Each matcher is evaluated independently (string matchers reuse the same
    // range / usesRowId / default-column rules as the legacy scalar path; object matchers
    // delegate to MatchesAllRowIdentifiers) and the union of their results is returned in the
    // order matchers were declared, so authors get predictable apply ordering. Throws when an
    // individual matcher resolves to zero rows or when the union ends up empty, mirroring the
    // single-rowIdentifier behavior so authoring mistakes surface loudly.
    private static List<int> ResolveRowMatcherIndices<TEntry>(
        IList<TEntry> entries,
        IReadOnlyList<PluginRowMatcher> matchers,
        Func<TEntry, string?> rowIdentifierSelector,
        Func<string, PropertyInfo?> resolveColumn,
        string fileName,
        string rowIdentifierPropertyName,
        bool usesRowId)
    {
        var seen = new HashSet<int>();
        var ordered = new List<int>();

        for (var matcherIndex = 0; matcherIndex < matchers.Count; matcherIndex++)
        {
            var matcher = matchers[matcherIndex];
            IEnumerable<int> matched;

            if (matcher.Columns is { Count: > 0 } columnMap)
            {
                var indices = Enumerable.Range(0, entries.Count)
                    .Where(i => MatchesAllRowIdentifiers(entries[i], columnMap, resolveColumn))
                    .ToList();

                if (indices.Count == 0)
                {
                    var description = string.Join(", ", columnMap.Select(p => $"{p.Key}='{p.Value}'"));
                    throw new InvalidDataException(
                        $"rowIdentifier array element at index {matcherIndex} matched no rows in {fileName}: {description}.");
                }

                matched = indices;
            }
            else
            {
                var value = matcher.Value ?? string.Empty;

                if (TryParseRowRange(value, out var rangeStart, out var rangeEnd))
                {
                    if (rangeStart > rangeEnd)
                    {
                        (rangeStart, rangeEnd) = (rangeEnd, rangeStart);
                    }

                    if (rangeStart < 0 || rangeEnd >= entries.Count)
                    {
                        throw new InvalidDataException(
                            $"rowIdentifier array element at index {matcherIndex} ('{value}') is out of bounds for {fileName}. Valid range is 0 to {entries.Count - 1}.");
                    }

                    matched = Enumerable.Range(rangeStart, rangeEnd - rangeStart + 1);
                }
                else if (usesRowId)
                {
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowIndex)
                        || rowIndex < 0 || rowIndex >= entries.Count)
                    {
                        throw new InvalidDataException(
                            $"rowIdentifier array element at index {matcherIndex} ('{value}') is not a valid row index for {fileName}. Valid range is 0 to {entries.Count - 1}.");
                    }

                    matched = [rowIndex];
                }
                else
                {
                    var indices = Enumerable.Range(0, entries.Count)
                        .Where(i => string.Equals(rowIdentifierSelector(entries[i]), value, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (indices.Count == 0)
                    {
                        throw new InvalidDataException(
                            $"rowIdentifier array element at index {matcherIndex} ('{value}') matched no rows in {fileName} using the {rowIdentifierPropertyName} column.");
                    }

                    matched = indices;
                }
            }

            foreach (var index in matched)
            {
                if (seen.Add(index))
                {
                    ordered.Add(index);
                }
            }
        }

        if (ordered.Count == 0)
        {
            throw new InvalidDataException(
                $"rowIdentifier array for {fileName} matched no rows.");
        }

        return ordered;
    }

    // Resolves a single-row identifier supplied to cloneRow/swapRow. Accepts either a numeric
    // 0-based row index, or a value matched (case-insensitive) against the file's default
    // rowIdentifier column. Throws when zero or multiple rows would match so authors get a clear
    // error before any in-place mutation runs. The 'fieldName' is used in error messages so authors
    // can tell which JSON field (e.g. "sourceRowIdentifier", "swapRowIdentifier") is at fault.
    private static int ResolveSingleRowIndex<TEntry>(
        IList<TEntry> entries,
        string identifier,
        Func<TEntry, string?> rowIdentifierSelector,
        string fileName,
        string fieldName)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new InvalidDataException(
                $"{fieldName} for {fileName} must not be empty.");
        }

        if (int.TryParse(identifier, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedIndex))
        {
            if (parsedIndex < 0 || parsedIndex >= entries.Count)
            {
                throw new InvalidDataException(
                    $"{fieldName} '{identifier}' is out of bounds for {fileName}. Valid range is 0 to {entries.Count - 1}.");
            }

            return parsedIndex;
        }

        var matches = Enumerable.Range(0, entries.Count)
            .Where(i => string.Equals(rowIdentifierSelector(entries[i]), identifier, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            throw new InvalidDataException(
                $"Could not find row '{identifier}' in {fileName} via {fieldName} (use a numeric 0-based index or a value matching the default rowIdentifier column).");
        }

        if (matches.Count > 1)
        {
            throw new InvalidDataException(
                $"{fieldName} '{identifier}' matches {matches.Count} rows in {fileName}. Use a numeric 0-based index to disambiguate.");
        }

        return matches[0];
    }

    // Parses a plugin rowIdentifier of the form "start-end" (e.g. "50-100") into inclusive numeric
    // row-index bounds. Returns false when the identifier is null/empty or not a range, allowing the
    // caller to fall back to exact row-ID or identifier-column matching.
    private static bool TryParseRowRange(string? rowIdentifier, out int start, out int end)
    {
        start = 0;
        end = 0;

        if (string.IsNullOrWhiteSpace(rowIdentifier))
        {
            return false;
        }

        var match = RowRangeRegex.Match(rowIdentifier);
        if (!match.Success)
        {
            return false;
        }

        return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out start)
               && int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out end);
    }

    private static TEntry UpdateRecord<TEntry>(
        TEntry entry,
        string column,
        string? updatedValue,
        string fileName,
        Func<string, PropertyInfo?>? resolveColumn = null,
        string? rowIdentifier = null)
        where TEntry : class
    {
        var property = resolveColumn?.Invoke(column)
            ?? typeof(TEntry).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(candidate => candidate.Name.Equals(column, StringComparison.OrdinalIgnoreCase));

        if (property == null)
        {
            throw new InvalidDataException(
                $"The column '{column}' is not supported for {fileName} (row '{rowIdentifier ?? "<unknown>"}', attempted value '{updatedValue ?? string.Empty}').");
        }

        var clonedEntry = CloneEntry(entry);
        try
        {
            var convertedValue = ConvertValue(updatedValue, property.PropertyType, column);
            property.SetValue(clonedEntry, convertedValue);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                $"{ex.Message} (file '{fileName}', row '{rowIdentifier ?? "<unknown>"}', column '{column}', attempted value '{updatedValue ?? string.Empty}')",
                ex);
        }
        return clonedEntry;
    }

    private static string? ResolveOperationValue<TEntry>(
        TEntry entry,
        PluginJsonOperation operation,
        IReadOnlyDictionary<string, string> parameters,
        string fileName,
        Func<string, PropertyInfo?>? resolveColumn = null)
        where TEntry : class
    {
        var resolvedUpdatedValue = ResolveParameterTokens(operation.UpdatedValue, parameters);
        var operationType = operation.Operation?.Trim();

        if (string.IsNullOrWhiteSpace(operationType) ||
            operationType.Equals("replace", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(operation.ParameterKey) &&
                parameters.TryGetValue(operation.ParameterKey, out var parameterValue))
            {
                return parameterValue;
            }

            return resolvedUpdatedValue;
        }

        if (operationType.Equals("multiplyExisting", StringComparison.OrdinalIgnoreCase) ||
            operationType.Equals("addExisting", StringComparison.OrdinalIgnoreCase) ||
            operationType.Equals("subtractExisting", StringComparison.OrdinalIgnoreCase) ||
            operationType.Equals("divideExisting", StringComparison.OrdinalIgnoreCase))
        {
            var column = operation.Column ?? string.Empty;
            var property = resolveColumn?.Invoke(column)
                ?? typeof(TEntry).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(candidate => candidate.Name.Equals(column, StringComparison.OrdinalIgnoreCase));

            if (property == null)
            {
                throw new InvalidDataException(
                    $"The column '{column}' is not supported for {fileName} (row '{operation.RowIdentifier ?? "<unknown>"}').");
            }

            var currentValue = property.GetValue(entry)?.ToString();
            if (!decimal.TryParse(currentValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var currentNumber))
            {
                throw new InvalidDataException(
                    $"The existing value '{currentValue}' in column '{column}' is not numeric and cannot be used with {operationType} (file '{fileName}', row '{operation.RowIdentifier ?? "<unknown>"}').");
            }

            var operandText = ResolveExistingOperandValue(operation, parameters, resolvedUpdatedValue, operationType);
            if (!decimal.TryParse(operandText, NumberStyles.Float, CultureInfo.InvariantCulture, out var operand))
            {
                throw new InvalidDataException(
                    $"The {operationType} operand '{operandText}' is not a valid decimal number (file '{fileName}', row '{operation.RowIdentifier ?? "<unknown>"}', column '{column}').");
            }

            decimal result;
            if (operationType.Equals("multiplyExisting", StringComparison.OrdinalIgnoreCase))
            {
                result = currentNumber * operand;
            }
            else if (operationType.Equals("addExisting", StringComparison.OrdinalIgnoreCase))
            {
                result = currentNumber + operand;
            }
            else if (operationType.Equals("subtractExisting", StringComparison.OrdinalIgnoreCase))
            {
                result = currentNumber - operand;
            }
            else
            {
                if (operand == 0m)
                {
                    throw new InvalidDataException(
                        $"divideExisting cannot divide by zero (file '{fileName}', row '{operation.RowIdentifier ?? "<unknown>"}', column '{column}').");
                }

                result = currentNumber / operand;
            }

            return FormatDecimalValue(result);
        }

        if (operationType.Equals("append", StringComparison.OrdinalIgnoreCase))
        {
            var column = operation.Column ?? string.Empty;
            var property = resolveColumn?.Invoke(column)
                ?? typeof(TEntry).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(candidate => candidate.Name.Equals(column, StringComparison.OrdinalIgnoreCase));

            if (property == null)
            {
                throw new InvalidDataException(
                    $"The column '{column}' is not supported for {fileName} (row '{operation.RowIdentifier ?? "<unknown>"}').");
            }

            var currentValue = property.GetValue(entry)?.ToString() ?? string.Empty;

            var appendText = !string.IsNullOrWhiteSpace(operation.ParameterKey) &&
                             parameters.TryGetValue(operation.ParameterKey, out var parameterValue)
                ? parameterValue
                : resolvedUpdatedValue ?? string.Empty;

            return $"({currentValue}){appendText}";
        }

        throw new InvalidDataException($"Unsupported plugin operation '{operationType}' (file '{fileName}', row '{operation.RowIdentifier ?? "<unknown>"}', column '{operation.Column ?? string.Empty}').");
    }

    private static string ResolveExistingOperandValue(
        PluginJsonOperation operation,
        IReadOnlyDictionary<string, string> parameters,
        string? resolvedUpdatedValue,
        string operationType)
    {
        if (!string.IsNullOrWhiteSpace(operation.ParameterKey))
        {
            if (!parameters.TryGetValue(operation.ParameterKey, out var parameterValue))
            {
                throw new InvalidDataException($"The parameter '{operation.ParameterKey}' was not found.");
            }

            return parameterValue;
        }

        if (!string.IsNullOrWhiteSpace(resolvedUpdatedValue))
        {
            return resolvedUpdatedValue;
        }

        throw new InvalidDataException($"{operationType} operations require either parameterKey or updatedValue.");
    }

    private static string? ResolveParameterTokens(string? value, IReadOnlyDictionary<string, string> parameters)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return ParameterTokenRegex.Replace(value, match =>
        {
            var parameterKey = match.Groups[1].Value;
            if (!parameters.TryGetValue(parameterKey, out var parameterValue))
            {
                throw new InvalidDataException($"The parameter '{parameterKey}' was not found.");
            }

            return parameterValue;
        });
    }

    private static string FormatDecimalValue(decimal value)
    {
        return value == decimal.Truncate(value)
            ? decimal.Truncate(value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.############################", CultureInfo.InvariantCulture);
    }

    private static TEntry CloneEntry<TEntry>(TEntry entry)
        where TEntry : class
    {
        var cloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
        if (cloneMethod == null)
        {
            throw new InvalidOperationException($"Could not clone '{typeof(TEntry).Name}' plugin record.");
        }

        return (TEntry)cloneMethod.Invoke(entry, null)!;
    }

    /// <summary>
    /// Normalizes a user-supplied value for an integer-typed target column. Game .txt files do not
    /// accept decimals, so any fractional portion is truncated toward negative infinity (floor),
    /// mirroring the way the game itself handles such values at runtime.
    /// </summary>
    private static string? FloorIntegerInput(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var trimmed = value.Trim();

        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
            ulong.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return trimmed;
        }

        if (decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var asDecimal))
        {
            return decimal.Floor(asDecimal).ToString(CultureInfo.InvariantCulture);
        }

        return value;
    }

    private static object? ConvertValue(string? value, Type targetType, string column)
    {
        if (targetType == typeof(string))
        {
            return value ?? string.Empty;
        }

        var underlyingType = Nullable.GetUnderlyingType(targetType);
        if (underlyingType != null)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return ConvertValue(value, underlyingType, column);
        }

        if (targetType == typeof(int))
        {
            var normalized = FloorIntegerInput(value);
            if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
            {
                return parsedInt;
            }

            throw new InvalidDataException($"The value '{value}' is not a valid integer for column '{column}'.");
        }

        if (targetType == typeof(uint))
        {
            var normalized = FloorIntegerInput(value);
            if (uint.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedUInt))
            {
                return parsedUInt;
            }

            throw new InvalidDataException($"The value '{value}' is not a valid unsigned integer for column '{column}'.");
        }

        if (targetType == typeof(long))
        {
            var normalized = FloorIntegerInput(value);
            if (long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong))
            {
                return parsedLong;
            }

            throw new InvalidDataException($"The value '{value}' is not a valid long for column '{column}'.");
        }

        if (targetType == typeof(ulong))
        {
            var normalized = FloorIntegerInput(value);
            if (ulong.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedULong))
            {
                return parsedULong;
            }

            throw new InvalidDataException($"The value '{value}' is not a valid unsigned long for column '{column}'.");
        }

        if (targetType == typeof(short))
        {
            var normalized = FloorIntegerInput(value);
            if (short.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedShort))
            {
                return parsedShort;
            }

            throw new InvalidDataException($"The value '{value}' is not a valid short for column '{column}'.");
        }

        if (targetType == typeof(ushort))
        {
            var normalized = FloorIntegerInput(value);
            if (ushort.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedUShort))
            {
                return parsedUShort;
            }

            throw new InvalidDataException($"The value '{value}' is not a valid unsigned short for column '{column}'.");
        }

        if (targetType == typeof(byte))
        {
            var normalized = FloorIntegerInput(value);
            if (byte.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedByte))
            {
                return parsedByte;
            }

            throw new InvalidDataException($"The value '{value}' is not a valid byte for column '{column}'.");
        }

        if (targetType == typeof(sbyte))
        {
            var normalized = FloorIntegerInput(value);
            if (sbyte.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSByte))
            {
                return parsedSByte;
            }

            throw new InvalidDataException($"The value '{value}' is not a valid signed byte for column '{column}'.");
        }

        if (targetType == typeof(double))
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble))
            {
                return parsedDouble;
            }

            throw new InvalidDataException($"The value '{value}' is not a valid number for column '{column}'.");
        }

        if (targetType == typeof(float))
        {
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFloat))
            {
                return parsedFloat;
            }

            throw new InvalidDataException($"The value '{value}' is not a valid number for column '{column}'.");
        }

        if (targetType == typeof(decimal))
        {
            if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDecimal))
            {
                return parsedDecimal;
            }

            throw new InvalidDataException($"The value '{value}' is not a valid decimal for column '{column}'.");
        }

        if (targetType == typeof(bool))
        {
            if (bool.TryParse(value, out var parsedBool))
            {
                return parsedBool;
            }

            if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            throw new InvalidDataException($"The value '{value}' is not a valid boolean for column '{column}'.");
        }

        throw new InvalidDataException(
            $"The column '{column}' uses the unsupported type '{targetType.Name}' for plugin editing.");
    }

    private static async Task<PluginState> LoadPluginStateAsync(PluginRegistration registration)
    {
        var pluginDirectory = GetPluginDirectory(registration);
        var errors = new List<string>();
        var warnings = new List<string>();
        var files = new List<PluginCatalogFileItem>();
        var parameters = new List<PluginParameterItem>();
        var assets = new List<PluginAssetCopy>();
        var name = registration.FolderName;
        var version = "Unknown";
        var modVersion = string.Empty;
        var author = string.Empty;
        var description = string.Empty;

        if (!Directory.Exists(pluginDirectory))
        {
            errors.Add("Imported plugin files are missing from disk.");
            return new PluginState(name, version, modVersion, author, description, parameters, files, assets, errors, warnings);
        }

        var pluginInfoPath = Path.Combine(pluginDirectory, PluginInfoFileName);
        if (!File.Exists(pluginInfoPath))
        {
            errors.Add("plugininfo.json is missing.");
            return new PluginState(name, version, modVersion, author, description, parameters, files, assets, errors, warnings);
        }

        try
        {
            var pluginInfo = await LoadPluginInfoAsync(pluginInfoPath);
            name = pluginInfo.Name;
            version = pluginInfo.Version;
            modVersion = pluginInfo.ModVersion ?? string.Empty;
            author = pluginInfo.Author ?? string.Empty;
            description = pluginInfo.Description ?? string.Empty;
            parameters = pluginInfo.Parameters.Select(parameter =>
            {
                var rawValue = string.IsNullOrWhiteSpace(parameter.Value) ? parameter.DefaultValue : parameter.Value;
                var normalizedType = parameter.Type ?? string.Empty;
                // Checkbox parameters always materialize as the canonical "true"/"false" so the UI
                // and the condition evaluator agree on the effective value, even if the on-disk
                // value uses one of the lenient boolean forms (1/0/yes/no/on/off/checked).
                var effectiveValue = string.Equals(normalizedType, "checkbox", StringComparison.OrdinalIgnoreCase)
                    ? NormalizeCheckboxValue(rawValue)
                    : rawValue;

                return new PluginParameterItem
                {
                    PluginId = registration.Id,
                    Key = parameter.Key,
                    DisplayName = parameter.Name,
                    Description = parameter.Description ?? string.Empty,
                    DefaultValue = parameter.DefaultValue,
                    Value = effectiveValue,
                    Type = normalizedType,
                    Group = parameter.Group ?? string.Empty
                };
            }).ToList();

            if (pluginInfo.Files.Count == 0 && pluginInfo.Assets.Count == 0)
            {
                errors.Add("plugininfo.json does not list any plugin JSON files or assets.");
                return new PluginState(name, version, modVersion, author, description, parameters, files, assets, errors, warnings);
            }

            foreach (var relativePath in pluginInfo.Files)
            {
                var normalizedRelativePath = NormalizeRelativePath(relativePath);
                var absolutePath = Path.Combine(pluginDirectory, normalizedRelativePath);
                if (!File.Exists(absolutePath))
                {
                    errors.Add($"Referenced plugin file '{normalizedRelativePath}' was not found.");
                    continue;
                }

                files.Add(new PluginCatalogFileItem
                {
                    PluginId = registration.Id,
                    RelativePath = normalizedRelativePath,
                    DisplayName = normalizedRelativePath
                });

                try
                {
                    var operations = await LoadPluginOperationsAsync(absolutePath);
                    ValidateOperations(operations, normalizedRelativePath, parameters, errors, warnings);
                }
                catch (Exception ex)
                {
                    errors.Add(FormatJsonError(normalizedRelativePath, ex));
                }
            }

            foreach (var asset in pluginInfo.Assets)
            {
                if (string.IsNullOrWhiteSpace(asset.Source) || string.IsNullOrWhiteSpace(asset.Target))
                {
                    errors.Add("plugininfo.json contains an asset entry with an empty source or target.");
                    continue;
                }

                var normalizedSource = NormalizeRelativePath(asset.Source);
                var sourceAbsolutePath = Path.Combine(pluginDirectory, normalizedSource);
                if (!File.Exists(sourceAbsolutePath))
                {
                    errors.Add($"Referenced asset '{normalizedSource}' was not found.");
                    continue;
                }

                var normalizedTarget = NormalizeRelativePath(asset.Target);

                if (asset.Condition != null)
                {
                    ValidateCondition(
                        asset.Condition,
                        parameters,
                        $"plugininfo.json asset '{normalizedTarget}'",
                        errors);
                }

                assets.Add(new PluginAssetCopy(sourceAbsolutePath, normalizedTarget, asset.Condition));
            }

            if (assets.Count > 0)
            {
                warnings.Add(PluginAssetWarningMessage);
            }
        }
        catch (Exception ex)
        {
            errors.Add(FormatJsonError("plugininfo.json", ex));
        }

        return new PluginState(name, version, modVersion, author, description, parameters, files, assets, errors, warnings);
    }

    private static void ValidateOperations(
        IReadOnlyList<PluginJsonOperation> operations,
        string pluginFileName,
        IReadOnlyList<PluginParameterItem> parameters,
        List<string> errors,
        List<string> warnings)
    {
        if (operations.Count == 0)
        {
            errors.Add($"'{pluginFileName}' does not contain any plugin operations.");
            return;
        }

        foreach (var operation in operations)
        {
            if (string.IsNullOrWhiteSpace(operation.File))
            {
                errors.Add($"'{pluginFileName}' contains an operation with no target file.");
                continue;
            }

            var supportedTarget = GetSupportedTarget(operation.File);
            if (supportedTarget == null)
            {
                errors.Add(
                    $"'{pluginFileName}' targets unsupported file '{operation.File}'. All .txt files except itemstatcost.txt are supported, and any .json file under data/local/lng/strings is supported.");
                continue;
            }

            if (supportedTarget.IsStringsTarget)
            {
                if (string.IsNullOrWhiteSpace(operation.Key))
                {
                    errors.Add($"'{pluginFileName}' contains a {supportedTarget.FileName} entry with no Key.");
                }

                if (operation.LanguageValues == null || operation.LanguageValues.Count == 0)
                {
                    var known = string.Join(", ", KnownStringLanguageColumns);
                    errors.Add(
                        $"'{pluginFileName}' contains a {supportedTarget.FileName} entry for Key '{operation.Key}' with no language fields to replace. Add one or more of: {known}.");
                }

                continue;
            }

            if (supportedTarget.IsMissilesTarget || supportedTarget.IsMonstersTarget)
            {
                // missiles.json and monsters.json take a flat {file, key, updatedValue|parameterKey,
                // [operation]} shape: replace by Key (default) or addRow to append a new key/value
                // pair. Both files share the same validation rules.
                if (string.IsNullOrWhiteSpace(operation.Key))
                {
                    errors.Add($"'{pluginFileName}' contains a {supportedTarget.FileName} entry with no Key.");
                }

                if (string.IsNullOrEmpty(operation.UpdatedValue)
                    && string.IsNullOrWhiteSpace(operation.ParameterKey))
                {
                    errors.Add(
                        $"'{pluginFileName}' contains a {supportedTarget.FileName} entry for Key '{operation.Key}' without a value. Set 'updatedValue' or 'parameterKey'.");
                }

                if (!string.IsNullOrWhiteSpace(operation.Operation)
                    && !operation.Operation.Equals("addRow", StringComparison.OrdinalIgnoreCase)
                    && !operation.Operation.Equals("replace", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(
                        $"'{pluginFileName}' contains a {supportedTarget.FileName} entry with unsupported operation '{operation.Operation}'. Use 'replace' (default) or 'addRow'.");
                }

                if (!string.IsNullOrWhiteSpace(operation.ParameterKey) &&
                    parameters.All(parameter => !parameter.Key.Equals(operation.ParameterKey, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add($"'{pluginFileName}' references unknown parameter '{operation.ParameterKey}'.");
                }

                continue;
            }

            var isCloneRow = !string.IsNullOrWhiteSpace(operation.Operation)
                             && operation.Operation.Equals("cloneRow", StringComparison.OrdinalIgnoreCase);
            var isSwapRow = !string.IsNullOrWhiteSpace(operation.Operation)
                            && operation.Operation.Equals("swapRow", StringComparison.OrdinalIgnoreCase);

            if (isCloneRow)
            {
                if (operation.RowMatchers is { Count: > 0 })
                {
                    errors.Add($"'{pluginFileName}' contains a cloneRow operation for {supportedTarget.FileName} with an array-form 'rowIdentifier'. cloneRow targets a single row — use one string or one object so the launcher knows which row to copy or overwrite.");
                }

                if (string.IsNullOrWhiteSpace(operation.SourceRowIdentifier))
                {
                    errors.Add($"'{pluginFileName}' contains a cloneRow operation for {supportedTarget.FileName} without 'sourceRowIdentifier'.");
                }

                var isReplaceMode = !string.IsNullOrWhiteSpace(operation.Mode)
                                    && operation.Mode.Equals("replace", StringComparison.OrdinalIgnoreCase);
                var isAddMode = string.IsNullOrWhiteSpace(operation.Mode)
                                || operation.Mode.Equals("add", StringComparison.OrdinalIgnoreCase);

                if (!isAddMode && !isReplaceMode)
                {
                    errors.Add($"'{pluginFileName}' contains a cloneRow operation for {supportedTarget.FileName} with unsupported mode '{operation.Mode}'. Use 'add' (default) or 'replace'.");
                }

                if (isAddMode && !string.IsNullOrWhiteSpace(operation.RowIdentifier))
                {
                    errors.Add($"'{pluginFileName}' contains a cloneRow (mode='add') for {supportedTarget.FileName} with a 'rowIdentifier'. cloneRow does not support insertion at a specific index; omit 'rowIdentifier' so the cloned row is appended to the end, or use mode='replace' to overwrite an existing row.");
                }

                if (isReplaceMode && string.IsNullOrWhiteSpace(operation.RowIdentifier))
                {
                    errors.Add($"'{pluginFileName}' contains a cloneRow (mode='replace') for {supportedTarget.FileName} without 'rowIdentifier' for the row to overwrite.");
                }

                ValidateColumnAssignments(operation, GetColumnAssignments(operation), supportedTarget, parameters, pluginFileName, errors);
                continue;
            }

            if (isSwapRow)
            {
                if (operation.RowMatchers is { Count: > 0 })
                {
                    errors.Add($"'{pluginFileName}' contains a swapRow operation for {supportedTarget.FileName} with an array-form 'rowIdentifier'. swapRow always exchanges exactly two rows — use a single string or object on both 'rowIdentifier' and 'swapRowIdentifier'.");
                }

                if (string.IsNullOrWhiteSpace(operation.RowIdentifier))
                {
                    errors.Add($"'{pluginFileName}' contains a swapRow operation for {supportedTarget.FileName} without 'rowIdentifier'.");
                }

                if (string.IsNullOrWhiteSpace(operation.SwapRowIdentifier))
                {
                    errors.Add($"'{pluginFileName}' contains a swapRow operation for {supportedTarget.FileName} without 'swapRowIdentifier'.");
                }

                ValidateColumnAssignments(operation, GetColumnAssignments(operation), supportedTarget, parameters, pluginFileName, errors);
                if (operation.SwapColumns is { Count: > 0 } swapAssignments)
                {
                    ValidateColumnAssignments(operation, swapAssignments, supportedTarget, parameters, pluginFileName, errors);
                }
                continue;
            }

            var isAddRow = !string.IsNullOrWhiteSpace(operation.Operation)
                           && operation.Operation.Equals("addRow", StringComparison.OrdinalIgnoreCase);

            if (isAddRow)
            {
                if (operation.RowMatchers is { Count: > 0 })
                {
                    errors.Add($"'{pluginFileName}' contains an addRow operation for {supportedTarget.FileName} with an array-form 'rowIdentifier'. addRow inserts one new row — use a single numeric 0-based index, or omit 'rowIdentifier' to append at the end.");
                }
                else if (!string.IsNullOrWhiteSpace(operation.RowIdentifier)
                         && !int.TryParse(operation.RowIdentifier, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    errors.Add($"'{pluginFileName}' contains an addRow operation for {supportedTarget.FileName} with non-numeric rowIdentifier '{operation.RowIdentifier}'. Use a 0-based row index, or omit it to append at the end.");
                }
            }
            else if (operation.RowMatchers is { Count: > 0 } arrayMatchers)
            {
                // Array-form rowIdentifier (only honored on updateRow): validate each element using
                // the same rules the runtime applies. Scalar string elements must either be a valid
                // numeric index / "start-end" range on rowID-keyed files, or a non-empty default
                // identifier-column value otherwise. Object elements must reference real columns.
                for (var index = 0; index < arrayMatchers.Count; index++)
                {
                    var matcher = arrayMatchers[index];
                    if (matcher.Columns is { Count: > 0 } columnMap)
                    {
                        foreach (var pair in columnMap)
                        {
                            var columnExists = supportedTarget.EntryType
                                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                .Any(property => property.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));

                            if (!columnExists && supportedTarget.ResolveColumn != null)
                            {
                                columnExists = supportedTarget.ResolveColumn(pair.Key) != null;
                            }

                            if (!columnExists)
                            {
                                errors.Add($"'{pluginFileName}' references unknown {supportedTarget.FileName} rowIdentifier column '{pair.Key}' (rowIdentifier array element {index}).");
                            }
                        }

                        if (supportedTarget.UsesRowId && columnMap.Count < 2)
                        {
                            warnings.Add(
                                $"'{pluginFileName}' targets {supportedTarget.FileName}, which contains duplicate identifier values. " +
                                $"rowIdentifier array element {index} only lists {columnMap.Count} column(s); add at least two so the intended row is uniquely matched.");
                        }
                    }
                    else if (string.IsNullOrWhiteSpace(matcher.Value))
                    {
                        errors.Add($"'{pluginFileName}' contains an empty rowIdentifier array element at index {index} for {supportedTarget.FileName}.");
                    }
                    else if (supportedTarget.UsesRowId
                             && !int.TryParse(matcher.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                             && !TryParseRowRange(matcher.Value, out _, out _))
                    {
                        errors.Add($"'{pluginFileName}' contains a {supportedTarget.FileName} rowIdentifier array element '{matcher.Value}' (index {index}) that is not a numeric row ID or range. This file uses numeric row IDs (e.g. \"5\" or \"50-100\").");
                    }
                }
            }
            else if (operation.RowIdentifiers is { Count: > 0 } identifierMap)
            {
                // Multi-column rowIdentifier override is allowed; validate the listed columns and
                // warn the author when the override does not narrow the match enough on rowID files.
                foreach (var pair in identifierMap)
                {
                    var columnExists = supportedTarget.EntryType
                        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Any(property => property.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));

                    if (!columnExists && supportedTarget.ResolveColumn != null)
                    {
                        columnExists = supportedTarget.ResolveColumn(pair.Key) != null;
                    }

                    if (!columnExists)
                    {
                        errors.Add($"'{pluginFileName}' references unknown {supportedTarget.FileName} rowIdentifier column '{pair.Key}'.");
                    }
                }

                if (supportedTarget.UsesRowId && identifierMap.Count < 2)
                {
                    warnings.Add(
                        $"'{pluginFileName}' targets {supportedTarget.FileName}, which contains duplicate identifier values. " +
                        $"The rowIdentifier override only lists {identifierMap.Count} column(s); add at least two so the intended row is uniquely matched.");
                }
            }
            else if (string.IsNullOrWhiteSpace(operation.RowIdentifier))
            {
                errors.Add($"'{pluginFileName}' contains a {supportedTarget.FileName} operation with no rowIdentifier.");
            }
            else if (supportedTarget.UsesRowId
                     && !int.TryParse(operation.RowIdentifier, out _)
                     && !TryParseRowRange(operation.RowIdentifier, out _, out _))
            {
                errors.Add($"'{pluginFileName}' contains a {supportedTarget.FileName} operation with non-numeric rowIdentifier '{operation.RowIdentifier}'. This file uses numeric row IDs (e.g. \"5\" or a range like \"50-100\").");
            }

            var assignments = GetColumnAssignments(operation);
            if (assignments.Count == 0)
            {
                errors.Add($"'{pluginFileName}' contains a {supportedTarget.FileName} operation with no column. Provide a 'column' field or a non-empty 'columns' array.");
                continue;
            }

            foreach (var assignment in assignments)
            {
                if (string.IsNullOrWhiteSpace(assignment.Column))
                {
                    errors.Add($"'{pluginFileName}' contains a {supportedTarget.FileName} operation with a 'columns' entry that is missing its column name.");
                    continue;
                }

                var propertyExists = supportedTarget.EntryType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Any(property => property.Name.Equals(assignment.Column, StringComparison.OrdinalIgnoreCase));

                if (!propertyExists && supportedTarget.ResolveColumn != null)
                {
                    propertyExists = supportedTarget.ResolveColumn(assignment.Column!) != null;
                }

                if (!propertyExists)
                {
                    errors.Add($"'{pluginFileName}' references unknown {supportedTarget.FileName} column '{assignment.Column}'.");
                }

                var effectiveOperation = !string.IsNullOrWhiteSpace(assignment.Operation)
                    ? assignment.Operation
                    : operation.Operation;

                if (!string.IsNullOrWhiteSpace(effectiveOperation) &&
                    (effectiveOperation.Equals("multiplyExisting", StringComparison.OrdinalIgnoreCase) ||
                     effectiveOperation.Equals("addExisting", StringComparison.OrdinalIgnoreCase) ||
                     effectiveOperation.Equals("subtractExisting", StringComparison.OrdinalIgnoreCase) ||
                     effectiveOperation.Equals("divideExisting", StringComparison.OrdinalIgnoreCase) ||
                     effectiveOperation.Equals("append", StringComparison.OrdinalIgnoreCase)) &&
                    string.IsNullOrWhiteSpace(assignment.ParameterKey) &&
                    string.IsNullOrWhiteSpace(assignment.UpdatedValue) &&
                    string.IsNullOrWhiteSpace(operation.ParameterKey) &&
                    string.IsNullOrWhiteSpace(operation.UpdatedValue))
                {
                    errors.Add($"'{pluginFileName}' contains a {effectiveOperation} operation on column '{assignment.Column}' without parameterKey or updatedValue.");
                }

                if (!string.IsNullOrWhiteSpace(assignment.ParameterKey) &&
                    parameters.All(parameter => !parameter.Key.Equals(assignment.ParameterKey, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add($"'{pluginFileName}' references unknown parameter '{assignment.ParameterKey}'.");
                }
            }

            if (!string.IsNullOrWhiteSpace(operation.ParameterKey) &&
                parameters.All(parameter => !parameter.Key.Equals(operation.ParameterKey, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"'{pluginFileName}' references unknown parameter '{operation.ParameterKey}'.");
            }

            if (operation.Condition != null)
            {
                ValidateCondition(
                    operation.Condition,
                    parameters,
                    $"'{pluginFileName}' operation targeting {operation.File}",
                    errors);
            }
        }
    }

    // Validates a declarative plugin condition tree. Each node must use exactly one shape:
    // a parameterKey leaf with equals/notEquals, or one of the all/any/not combinators. Unknown
    // parameter keys and malformed shapes are reported as plugin validation errors so authors get
    // a clear message instead of a silent skip at apply-time.
    private static void ValidateCondition(
        PluginJsonCondition condition,
        IReadOnlyList<PluginParameterItem> parameters,
        string location,
        List<string> errors)
    {
        var hasParameterKey = !string.IsNullOrWhiteSpace(condition.ParameterKey);
        var hasAll = condition.All != null;
        var hasAny = condition.Any != null;
        var hasNot = condition.Not != null;

        var shapeCount = (hasParameterKey ? 1 : 0) + (hasAll ? 1 : 0) + (hasAny ? 1 : 0) + (hasNot ? 1 : 0);
        if (shapeCount == 0)
        {
            errors.Add($"{location}: condition is empty. Use parameterKey/equals (or notEquals), or all/any/not.");
            return;
        }

        if (shapeCount > 1)
        {
            errors.Add($"{location}: condition mixes shapes. Use exactly one of parameterKey, all, any, or not per node.");
            return;
        }

        if (hasParameterKey)
        {
            if (parameters.All(p => !p.Key.Equals(condition.ParameterKey, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"{location}: condition references unknown parameter '{condition.ParameterKey}'.");
            }

            var hasEquals = condition.EqualsValue != null;
            var hasNotEquals = condition.NotEqualsValue != null;
            if (!hasEquals && !hasNotEquals)
            {
                errors.Add($"{location}: condition for parameter '{condition.ParameterKey}' must specify either 'equals' or 'notEquals'.");
            }
            else if (hasEquals && hasNotEquals)
            {
                errors.Add($"{location}: condition for parameter '{condition.ParameterKey}' cannot specify both 'equals' and 'notEquals'.");
            }

            return;
        }

        if (hasAll)
        {
            if (condition.All!.Count == 0)
            {
                errors.Add($"{location}: 'all' condition must contain at least one nested condition.");
                return;
            }

            foreach (var nested in condition.All)
            {
                if (nested == null)
                {
                    errors.Add($"{location}: 'all' contains a null condition entry.");
                    continue;
                }

                ValidateCondition(nested, parameters, location, errors);
            }

            return;
        }

        if (hasAny)
        {
            if (condition.Any!.Count == 0)
            {
                errors.Add($"{location}: 'any' condition must contain at least one nested condition.");
                return;
            }

            foreach (var nested in condition.Any)
            {
                if (nested == null)
                {
                    errors.Add($"{location}: 'any' contains a null condition entry.");
                    continue;
                }

                ValidateCondition(nested, parameters, location, errors);
            }

            return;
        }

        // hasNot
        ValidateCondition(condition.Not!, parameters, location, errors);
    }

    // Pure-data evaluator: walks the condition tree and reports whether the action should run.
    // Comparisons are case-insensitive string equality on the effective parameter value (Value
    // when set, otherwise DefaultValue). Returns true for null/empty-shaped conditions so a
    // missing condition means "always apply"; missing parameter keys evaluate to false (validation
    // already surfaces this as an error before apply-time).
    private static bool EvaluateCondition(
        PluginJsonCondition? condition,
        IReadOnlyDictionary<string, string> parameters)
    {
        if (condition is null)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(condition.ParameterKey))
        {
            parameters.TryGetValue(condition.ParameterKey!, out var value);
            value ??= string.Empty;

            if (condition.EqualsValue != null)
            {
                return string.Equals(value, condition.EqualsValue, StringComparison.OrdinalIgnoreCase);
            }

            if (condition.NotEqualsValue != null)
            {
                return !string.Equals(value, condition.NotEqualsValue, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        if (condition.All != null)
        {
            return condition.All.All(c => EvaluateCondition(c, parameters));
        }

        if (condition.Any != null)
        {
            return condition.Any.Any(c => EvaluateCondition(c, parameters));
        }

        if (condition.Not != null)
        {
            return !EvaluateCondition(condition.Not, parameters);
        }

        // Empty/unknown shape: malformed conditions should already have been caught by validation;
        // err on the side of "do not apply" to avoid surprising authors with a silent passthrough.
        return false;
    }

    // Lenient boolean parser used by checkbox parameters. Accepts the common string forms used in
    // existing plugininfo.json files (true/false/1/0/yes/no/on/off/checked) so authors who hand-
    // edit defaults still get a valid normalized value persisted back to disk.
    private static string NormalizeCheckboxValue(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return "false";
        }

        var trimmed = rawValue.Trim();
        if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("1", StringComparison.Ordinal) ||
            trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("on", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("checked", StringComparison.OrdinalIgnoreCase))
        {
            return "true";
        }

        return "false";
    }

    // Validates a list of column assignments against the target file. Used by cloneRow/swapRow,
    // which accept optional assignments and therefore must not error when the list is empty.
    private static void ValidateColumnAssignments(
        PluginJsonOperation operation,
        IReadOnlyList<PluginJsonColumnAssignment> assignments,
        SupportedPluginTarget supportedTarget,
        IReadOnlyList<PluginParameterItem> parameters,
        string pluginFileName,
        List<string> errors)
    {
        foreach (var assignment in assignments)
        {
            if (string.IsNullOrWhiteSpace(assignment.Column))
            {
                errors.Add($"'{pluginFileName}' contains a {supportedTarget.FileName} operation with a 'columns' entry that is missing its column name.");
                continue;
            }

            var propertyExists = supportedTarget.EntryType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(property => property.Name.Equals(assignment.Column, StringComparison.OrdinalIgnoreCase));

            if (!propertyExists && supportedTarget.ResolveColumn != null)
            {
                propertyExists = supportedTarget.ResolveColumn(assignment.Column!) != null;
            }

            if (!propertyExists)
            {
                errors.Add($"'{pluginFileName}' references unknown {supportedTarget.FileName} column '{assignment.Column}'.");
            }

            var effectiveOperation = !string.IsNullOrWhiteSpace(assignment.Operation)
                ? assignment.Operation
                : operation.Operation;

            if (!string.IsNullOrWhiteSpace(effectiveOperation) &&
                (effectiveOperation.Equals("multiplyExisting", StringComparison.OrdinalIgnoreCase) ||
                 effectiveOperation.Equals("addExisting", StringComparison.OrdinalIgnoreCase) ||
                 effectiveOperation.Equals("subtractExisting", StringComparison.OrdinalIgnoreCase) ||
                 effectiveOperation.Equals("divideExisting", StringComparison.OrdinalIgnoreCase) ||
                 effectiveOperation.Equals("append", StringComparison.OrdinalIgnoreCase)) &&
                string.IsNullOrWhiteSpace(assignment.ParameterKey) &&
                string.IsNullOrWhiteSpace(assignment.UpdatedValue) &&
                string.IsNullOrWhiteSpace(operation.ParameterKey) &&
                string.IsNullOrWhiteSpace(operation.UpdatedValue))
            {
                errors.Add($"'{pluginFileName}' contains a {effectiveOperation} operation on column '{assignment.Column}' without parameterKey or updatedValue.");
            }

            if (!string.IsNullOrWhiteSpace(assignment.ParameterKey) &&
                parameters.All(parameter => !parameter.Key.Equals(assignment.ParameterKey, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"'{pluginFileName}' references unknown parameter '{assignment.ParameterKey}'.");
            }
        }
    }

    private static bool IsSupportedTargetFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return ParserRegistry.ContainsKey(fileName)
               || IsStringsTargetFile(fileName)
               || IsMissilesTargetFile(fileName)
               || IsMonstersTargetFile(fileName);
    }

    private static SupportedPluginTarget? GetSupportedTarget(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        if (ParserRegistry.TryGetValue(fileName, out var registration))
        {
            return new SupportedPluginTarget(
                fileName,
                registration.EntryType,
                registration.RowIdentifierPropertyName,
                registration.UsesRowId,
                registration.ResolveColumn,
                IsStringsTarget: false);
        }

        if (IsMissilesTargetFile(fileName))
        {
            return new SupportedPluginTarget(
                fileName,
                typeof(object),
                "Key",
                UsesRowId: false,
                IsStringsTarget: false,
                IsMissilesTarget: true);
        }

        if (IsMonstersTargetFile(fileName))
        {
            return new SupportedPluginTarget(
                fileName,
                typeof(object),
                "Key",
                UsesRowId: false,
                IsStringsTarget: false,
                IsMissilesTarget: false,
                IsMonstersTarget: true);
        }

        if (IsStringsTargetFile(fileName))
        {
            return new SupportedPluginTarget(fileName, typeof(object), "Key", UsesRowId: false, IsStringsTarget: true);
        }

        return null;
    }

    private static async Task<PluginInfo> LoadPluginInfoAsync(string pluginInfoPath)
    {
        await using var stream = File.OpenRead(pluginInfoPath);
        var pluginInfo = await JsonSerializer.DeserializeAsync<PluginInfo>(stream, JsonOptions);
        if (pluginInfo == null)
        {
            throw new InvalidDataException("plugininfo.json could not be parsed.");
        }

        return new PluginInfo
        {
            Name = pluginInfo.Name ?? string.Empty,
            Version = pluginInfo.Version ?? string.Empty,
            ModVersion = pluginInfo.ModVersion,
            Author = pluginInfo.Author,
            Description = pluginInfo.Description,
            Files = pluginInfo.Files ?? [],
            Parameters = pluginInfo.Parameters ?? [],
            Assets = pluginInfo.Assets ?? []
        };
    }

    private static async Task SavePluginInfoAsync(string pluginInfoPath, PluginInfo pluginInfo)
    {
        var json = JsonSerializer.Serialize(pluginInfo, SerializerOptions.CamelCase);
        await File.WriteAllTextAsync(pluginInfoPath, json);
    }

    private static void ValidatePluginInfo(PluginInfo pluginInfo, string pluginRootDirectory)
    {
        if (string.IsNullOrWhiteSpace(pluginInfo.Name))
        {
            throw new InvalidDataException("plugininfo.json must include a name.");
        }

        if (string.IsNullOrWhiteSpace(pluginInfo.Version))
        {
            throw new InvalidDataException("plugininfo.json must include a version.");
        }

        if (string.IsNullOrWhiteSpace(pluginInfo.ModVersion))
        {
            throw new InvalidDataException("plugininfo.json must include a modVersion.");
        }

        if (!ModVersionRegex.IsMatch(pluginInfo.ModVersion))
        {
            throw new InvalidDataException("plugininfo.json modVersion must be in #.#.# format (e.g. 1.0.0).");
        }

        if (pluginInfo.Files.Count == 0 && pluginInfo.Assets.Count == 0)
        {
            throw new InvalidDataException("plugininfo.json must include at least one plugin JSON file or asset.");
        }

        foreach (var parameter in pluginInfo.Parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Key))
            {
                throw new InvalidDataException("plugininfo.json contains a parameter with no key.");
            }

            if (string.IsNullOrWhiteSpace(parameter.Name))
            {
                throw new InvalidDataException($"Parameter '{parameter.Key}' must include a name.");
            }

            if (string.IsNullOrWhiteSpace(parameter.DefaultValue))
            {
                throw new InvalidDataException($"Parameter '{parameter.Key}' must include a defaultValue.");
            }
        }

        foreach (var relativePath in pluginInfo.Files)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new InvalidDataException("plugininfo.json contains an empty plugin JSON file path.");
            }

            var normalizedRelativePath = NormalizeRelativePath(relativePath);
            var absolutePath = Path.Combine(pluginRootDirectory, normalizedRelativePath);
            if (!File.Exists(absolutePath))
            {
                throw new FileNotFoundException(
                    $"The plugin JSON file '{normalizedRelativePath}' listed in plugininfo.json was not found.");
            }
        }

        foreach (var asset in pluginInfo.Assets)
        {
            if (string.IsNullOrWhiteSpace(asset.Source))
            {
                throw new InvalidDataException("plugininfo.json contains an asset entry with no source.");
            }

            if (string.IsNullOrWhiteSpace(asset.Target))
            {
                throw new InvalidDataException("plugininfo.json contains an asset entry with no target.");
            }

            var normalizedSource = NormalizeRelativePath(asset.Source);
            if (!normalizedSource.StartsWith(PluginAssetsDirectoryName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !normalizedSource.StartsWith(PluginAssetsDirectoryName + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Asset source '{asset.Source}' must live under the '{PluginAssetsDirectoryName}/' folder inside the plugin.");
            }

            var sourceAbsolutePath = Path.Combine(pluginRootDirectory, normalizedSource);
            if (!File.Exists(sourceAbsolutePath))
            {
                throw new FileNotFoundException(
                    $"The asset '{normalizedSource}' listed in plugininfo.json was not found.");
            }

            var normalizedTarget = NormalizeRelativePath(asset.Target);
            if (Path.IsPathRooted(normalizedTarget) || normalizedTarget.Split(Path.DirectorySeparatorChar).Contains(".."))
            {
                throw new InvalidDataException(
                    $"Asset target '{asset.Target}' must be a relative path inside the mod folder.");
            }
        }
    }

    private static async Task<IReadOnlyList<PluginJsonOperation>> LoadPluginOperationsAsync(string pluginFilePath)
    {
        var json = await File.ReadAllTextAsync(pluginFilePath);
        return ParsePluginOperations(json);
    }

    private static IReadOnlyList<PluginJsonOperation> ParsePluginOperations(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Object => DeserializeSingleOperation(json),
            JsonValueKind.Array => DeserializeOperationArray(json),
            _ => throw new InvalidDataException("Plugin JSON must be either an object or an array of objects.")
        };
    }

    private static IReadOnlyList<PluginJsonOperation> DeserializeSingleOperation(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [NormalizeOperation(DeserializeOperationElement(document.RootElement))];
    }

    private static IReadOnlyList<PluginJsonOperation> DeserializeOperationArray(string json)
    {
        using var document = JsonDocument.Parse(json);
        var operations = new List<PluginJsonOperation>(document.RootElement.GetArrayLength());
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Plugin JSON array entries must be objects.");
            }

            operations.Add(NormalizeOperation(DeserializeOperationElement(element)));
        }

        return operations;
    }

    private static PluginJsonOperation DeserializeOperationElement(JsonElement element)
    {
        // Excel (.txt) targets use the standard {file, rowIdentifier, column, operation, ...} schema.
        // Strings (.json) targets use a flat d2rr-style layout: {file, Key, enUS, zhTW, ...}
        // where every known language field present on the object is applied as a direct replacement.
        var file = element.TryGetProperty("file", out var fileProperty) && fileProperty.ValueKind == JsonValueKind.String
            ? fileProperty.GetString()
            : null;

        if (IsStringsTargetFile(file))
        {
            string? key = null;
            if (element.TryGetProperty("Key", out var keyProperty) && keyProperty.ValueKind == JsonValueKind.String)
            {
                key = keyProperty.GetString();
            }
            else if (element.TryGetProperty("key", out var keyPropertyLower) && keyPropertyLower.ValueKind == JsonValueKind.String)
            {
                key = keyPropertyLower.GetString();
            }

            var languageValues = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("file") || property.NameEquals("Key") || property.NameEquals("key") || property.NameEquals("id"))
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (!KnownStringLanguageColumns.Contains(property.Name))
                {
                    // Ignore unknown fields per the flat d2rr-style schema (e.g. extra metadata).
                    continue;
                }

                languageValues[property.Name] = property.Value.GetString() ?? string.Empty;
            }

            return new PluginJsonOperation(
                File: file,
                RowIdentifier: null,
                Column: null,
                Operation: null,
                ParameterKey: null,
                UpdatedValue: null,
                Key: key,
                LanguageValues: languageValues);
        }

        // Allow rowIdentifier to be a string (default), an object declaring one or more identifier
        // columns ({"key1":"col1","key2":"col2"}), or an array whose elements are any mix of those
        // two scalar shapes (the array form unions the matched rows; only updateRow honors it).
        // A dedicated "rowIdentifiers" property is also accepted as a plural alias for the object
        // form. Whenever the property is non-string we strip it from the element before
        // deserialization so the standard string-typed RowIdentifier remains valid, then re-attach
        // the parsed structure.
        var rowMatchers = TryReadRowMatcherList(element, "rowIdentifier");
        var rowIdentifiers = rowMatchers != null
            ? null
            : TryReadRowIdentifierMap(element, "rowIdentifier")
              ?? TryReadRowIdentifierMap(element, "rowIdentifiers");

        string elementJson;
        if (rowIdentifiers != null || rowMatchers != null)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("rowIdentifier") || property.NameEquals("rowIdentifiers"))
                    {
                        continue;
                    }

                    property.WriteTo(writer);
                }
                writer.WriteEndObject();
            }

            elementJson = Encoding.UTF8.GetString(buffer.ToArray());
        }
        else
        {
            elementJson = element.GetRawText();
        }

        var operation = JsonSerializer.Deserialize<PluginJsonOperation>(elementJson, JsonOptions);
        if (operation == null)
        {
            throw new InvalidDataException("Plugin JSON could not be parsed.");
        }

        if (rowIdentifiers != null)
        {
            operation = operation with { RowIdentifiers = rowIdentifiers };
        }

        if (rowMatchers != null)
        {
            operation = operation with { RowMatchers = rowMatchers };
        }

        return operation;
    }

    // Reads an array-shaped rowIdentifier into a canonical list of matchers. Each element is
    // either a scalar (string/number/bool — coerced to its invariant string form) or an object of
    // {column: expectedValue} pairs (same shape as the legacy object-form rowIdentifier). Returns
    // null when the property is missing or is not an array, so callers can fall back to the
    // string- or object-form parsers. Throws InvalidDataException with a clear element index when
    // an element is neither a scalar nor an object.
    private static IReadOnlyList<PluginRowMatcher>? TryReadRowMatcherList(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var matchers = new List<PluginRowMatcher>();
        var elementIndex = 0;
        foreach (var arrayElement in property.EnumerateArray())
        {
            switch (arrayElement.ValueKind)
            {
                case JsonValueKind.String:
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    var scalar = arrayElement.ValueKind == JsonValueKind.String
                        ? arrayElement.GetString() ?? string.Empty
                        : arrayElement.ToString();
                    matchers.Add(new PluginRowMatcher(scalar, null));
                    break;

                case JsonValueKind.Object:
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in arrayElement.EnumerateObject())
                    {
                        string? value = entry.Value.ValueKind switch
                        {
                            JsonValueKind.String => entry.Value.GetString(),
                            JsonValueKind.Number => entry.Value.ToString(),
                            JsonValueKind.True => "true",
                            JsonValueKind.False => "false",
                            _ => null
                        };

                        if (value == null)
                        {
                            continue;
                        }

                        dict[entry.Name] = value;
                    }

                    if (dict.Count == 0)
                    {
                        throw new InvalidDataException(
                            $"rowIdentifier array element at index {elementIndex} is an empty object. List at least one column to match.");
                    }

                    matchers.Add(new PluginRowMatcher(null, dict));
                    break;

                default:
                    throw new InvalidDataException(
                        $"rowIdentifier array element at index {elementIndex} must be a string, number, boolean, or object, got {arrayElement.ValueKind}.");
            }

            elementIndex++;
        }

        return matchers.Count == 0 ? null : matchers;
    }

    // Reads an object-shaped rowIdentifier override into a column-name -> expected-value dictionary.
    // Returns null when the property is missing or not an object so callers can fall back to the
    // legacy string-typed rowIdentifier path.
    private static IReadOnlyDictionary<string, string>? TryReadRowIdentifierMap(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in property.EnumerateObject())
        {
            string? value = entry.Value.ValueKind switch
            {
                JsonValueKind.String => entry.Value.GetString(),
                JsonValueKind.Number => entry.Value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };

            if (value == null)
            {
                continue;
            }

            dict[entry.Name] = value;
        }

        return dict;
    }

    private static PluginJsonOperation NormalizeOperation(PluginJsonOperation operation)
    {
        IReadOnlyList<PluginJsonColumnAssignment>? normalizedColumns = null;
        if (operation.Columns is { Count: > 0 } columns)
        {
            var list = new List<PluginJsonColumnAssignment>(columns.Count);
            foreach (var column in columns)
            {
                list.Add(new PluginJsonColumnAssignment(
                    Column: column.Column?.Trim() ?? string.Empty,
                    UpdatedValue: column.UpdatedValue,
                    ParameterKey: column.ParameterKey?.Trim(),
                    Operation: column.Operation?.Trim()));
            }
            normalizedColumns = list;
        }

        IReadOnlyDictionary<string, string>? normalizedRowIdentifiers = null;
        if (operation.RowIdentifiers is { Count: > 0 } rowIdentifiers)
        {
            var dict = new Dictionary<string, string>(rowIdentifiers.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in rowIdentifiers)
            {
                var key = pair.Key?.Trim();
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                dict[key] = pair.Value?.Trim() ?? string.Empty;
            }

            if (dict.Count > 0)
            {
                normalizedRowIdentifiers = dict;
            }
        }

        IReadOnlyList<PluginJsonColumnAssignment>? normalizedSwapColumns = null;
        if (operation.SwapColumns is { Count: > 0 } swapColumns)
        {
            var list = new List<PluginJsonColumnAssignment>(swapColumns.Count);
            foreach (var column in swapColumns)
            {
                list.Add(new PluginJsonColumnAssignment(
                    Column: column.Column?.Trim() ?? string.Empty,
                    UpdatedValue: column.UpdatedValue,
                    ParameterKey: column.ParameterKey?.Trim(),
                    Operation: column.Operation?.Trim()));
            }
            normalizedSwapColumns = list;
        }

        IReadOnlyList<PluginRowMatcher>? normalizedRowMatchers = null;
        if (operation.RowMatchers is { Count: > 0 } rowMatchers)
        {
            var list = new List<PluginRowMatcher>(rowMatchers.Count);
            foreach (var matcher in rowMatchers)
            {
                if (matcher.Columns is { Count: > 0 } columnMap)
                {
                    var dict = new Dictionary<string, string>(columnMap.Count, StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in columnMap)
                    {
                        var key = pair.Key?.Trim();
                        if (string.IsNullOrEmpty(key))
                        {
                            continue;
                        }

                        dict[key] = pair.Value?.Trim() ?? string.Empty;
                    }

                    if (dict.Count > 0)
                    {
                        list.Add(new PluginRowMatcher(null, dict));
                    }
                }
                else
                {
                    var trimmed = matcher.Value?.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                    {
                        list.Add(new PluginRowMatcher(trimmed, null));
                    }
                }
            }

            if (list.Count > 0)
            {
                normalizedRowMatchers = list;
            }
        }

        return operation with
        {
            File = NormalizeRelativePath(operation.File ?? string.Empty),
            RowIdentifier = operation.RowIdentifier?.Trim() ?? string.Empty,
            Column = operation.Column?.Trim() ?? string.Empty,
            Operation = operation.Operation?.Trim(),
            ParameterKey = operation.ParameterKey?.Trim(),
            Key = operation.Key?.Trim(),
            Columns = normalizedColumns,
            RowIdentifiers = normalizedRowIdentifiers,
            RowMatchers = normalizedRowMatchers,
            SourceRowIdentifier = operation.SourceRowIdentifier?.Trim(),
            Mode = operation.Mode?.Trim(),
            SwapRowIdentifier = operation.SwapRowIdentifier?.Trim(),
            SwapColumns = normalizedSwapColumns
        };
    }

    private static PluginRegistration GetRegistration(string pluginId)
    {
        MainWindow.Settings.CurrentProfile.Plugins ??= [];
        var registration = MainWindow.Settings.CurrentProfile.Plugins.FirstOrDefault(plugin => plugin.Id == pluginId);
        if (registration == null)
        {
            throw new InvalidOperationException("The selected plugin could not be found.");
        }

        return registration;
    }

    private static PluginRegistration? FindRegistrationByFolderName(string folderName)
    {
        MainWindow.Settings.CurrentProfile.Plugins ??= [];
        return MainWindow.Settings.CurrentProfile.Plugins.FirstOrDefault(plugin =>
            plugin.FolderName.Equals(folderName, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetBundledPluginDirectory(string folderName)
    {
        return Path.Combine(AppContext.BaseDirectory, BundledPluginsDirectoryName, folderName);
    }

    private static string GetPluginDirectory(PluginRegistration registration)
    {
        return Path.Combine(PluginsDirectoryPath, registration.FolderName);
    }

    private static string GetPluginFilePath(string pluginId, string relativePath)
    {
        var registration = GetRegistration(pluginId);
        return Path.Combine(GetPluginDirectory(registration), NormalizeRelativePath(relativePath));
    }

    private static string GetUniquePluginFolderName(string pluginName, string version)
    {
        var baseName = $"{SanitizePathSegment(pluginName)}-{SanitizePathSegment(version)}";
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = Guid.NewGuid().ToString("N");
        }

        var candidate = baseName;
        var suffix = 1;
        while (Directory.Exists(Path.Combine(PluginsDirectoryPath, candidate)))
        {
            suffix++;
            candidate = $"{baseName}-{suffix}";
        }

        return candidate;
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(normalized))
        {
            throw new InvalidDataException("Plugin file paths must be relative.");
        }

        normalized = normalized.TrimStart(Path.DirectorySeparatorChar);

        var segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
        {
            throw new InvalidDataException("Plugin file paths cannot traverse outside the plugin directory.");
        }

        if (segments.Length == 0)
        {
            return string.Empty;
        }

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static string FormatJsonError(string fileName, Exception ex)
    {
        if (ex is JsonException jsonEx)
        {
            var path = jsonEx.Path;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var propertyName = path.Contains('.')
                    ? path[(path.LastIndexOf('.') + 1)..]
                    : path;

                var innerMessage = jsonEx.InnerException?.Message;
                if (!string.IsNullOrWhiteSpace(innerMessage))
                {
                    return $"'{fileName}' has an invalid value for '{propertyName}': {innerMessage}";
                }

                return $"'{fileName}' has an invalid value for '{propertyName}'.";
            }

            var message = jsonEx.Message;
            var pipeIndex = message.IndexOf('|');
            if (pipeIndex > 0)
            {
                message = message[..pipeIndex].Trim();
            }

            return $"'{fileName}' has invalid JSON: {message}";
        }

        return $"'{fileName}' is invalid: {ex.Message}";
    }

    private static string SanitizePathSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_')
            {
                builder.Append(character);
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }

    private static void ReportProgress(IProgress<string>? progress, string message)
    {
        progress?.Report(message);
        LaunchDiagnostics.Log($"STATUS: {message}");
    }

    private static bool IsZipArchive(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 4)
        {
            return false;
        }

        Span<byte> signature = stackalloc byte[4];
        var bytesRead = stream.Read(signature);
        return bytesRead == 4 &&
               signature[0] == 0x50 &&
               signature[1] == 0x4B &&
               (signature[2] == 0x03 || signature[2] == 0x05 || signature[2] == 0x07) &&
               (signature[3] == 0x04 || signature[3] == 0x06 || signature[3] == 0x08);
    }

    private static async Task SaveGeneratedEntriesAsync<TEntry>(
        IList<TEntry> entries,
        string sourceFilePath,
        Func<IList<TEntry>, string, string, CancellationToken, Task<FileInfo>> saveEntriesAsync)
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "d2r-reimagined-launcher",
            GeneratedPluginsFolderName,
            Guid.NewGuid().ToString("N"));
        var generatedFile = await saveEntriesAsync(entries, sourceFilePath, outputDirectory, CancellationToken.None);
        File.Copy(generatedFile.FullName, sourceFilePath, overwrite: true);
    }

    private static async Task CopyDirectoryAsync(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var sourceFilePath in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourceFilePath);
            var destinationFilePath = Path.Combine(destinationDirectory, relativePath);
            var destinationFolder = Path.GetDirectoryName(destinationFilePath);

            if (!string.IsNullOrWhiteSpace(destinationFolder))
            {
                Directory.CreateDirectory(destinationFolder);
            }

            await using var sourceStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var destinationStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await sourceStream.CopyToAsync(destinationStream);
        }
    }

    private sealed record PluginState(
        string Name,
        string Version,
        string ModVersion,
        string Author,
        string Description,
        IReadOnlyList<PluginParameterItem> Parameters,
        IReadOnlyList<PluginCatalogFileItem> Files,
        IReadOnlyList<PluginAssetCopy> Assets,
        IReadOnlyList<string> Errors,
        IReadOnlyList<string> Warnings);

    private sealed record PluginAssetCopy(
        string SourceAbsolutePath,
        string TargetRelativePath,
        // Optional declarative condition copied from plugininfo.json. When null, the asset is
        // always applied. Evaluated against the plugin's effective parameters at apply-time.
        PluginJsonCondition? Condition = null);

    private sealed record SupportedPluginTarget(
        string FileName,
        Type EntryType,
        string RowIdentifierPropertyName,
        bool UsesRowId,
        Func<string, PropertyInfo?>? ResolveColumn = null,
        bool IsStringsTarget = false,
        bool IsMissilesTarget = false,
        bool IsMonstersTarget = false);

    private sealed record FileParserRegistration(
        Type EntryType,
        string RowIdentifierPropertyName,
        bool UsesRowId,
        Func<string, IReadOnlyList<PluginJsonOperation>, IReadOnlyDictionary<string, string>, Task> ApplyAsync,
        Func<string, PropertyInfo?> ResolveColumn);

    private static FileParserRegistration CreateRegistration<TEntry, TParser>(
        string fileName,
        string rowIdentifierPropertyName,
        Func<TEntry, string?> rowIdentifierSelector,
        bool usesRowId = false)
        where TEntry : class, new()
        where TParser : HeaderMappedTextFileParser<TEntry, TParser>, new()
    {
        var resolver = BuildColumnResolver<TEntry, TParser>();
        return new FileParserRegistration(
            typeof(TEntry),
            rowIdentifierPropertyName,
            usesRowId,
            (excelDirectory, operations, parameters) =>
                ApplyOperationsForTargetAsync(
                    excelDirectory, operations, parameters, fileName,
                    rowIdentifierPropertyName, rowIdentifierSelector,
                    path => HeaderMappedTextFileParser<TEntry, TParser>.GetEntries(path),
                    (entries, source, output, token) =>
                        HeaderMappedTextFileParser<TEntry, TParser>.SaveEntriesPreservingUnchanged(entries, source, output, token),
                    resolver,
                    usesRowId),
            resolver);
    }

    /// <summary>
    /// Builds a column-name resolver for the given parser type by reflecting its protected
    /// <c>PropertyColumnAliases</c> member. Mirrors the lookup logic of
    /// <c>HeaderMappedTextFileParser.GetOrBuildPropertyMap</c> so that raw D2R column headers
    /// (e.g. <c>dsc2calca1</c>) resolve to the corresponding <see cref="PropertyInfo"/>
    /// (e.g. <c>Dsc2CalculationA1</c>) without requiring library-side API additions.
    /// </summary>
    private static Func<string, PropertyInfo?> BuildColumnResolver<TEntry, TParser>()
        where TEntry : class
        where TParser : HeaderMappedTextFileParser<TEntry, TParser>, new()
    {
        var map = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);

        IReadOnlyDictionary<string, string[]> aliases =
            new Dictionary<string, string[]>(StringComparer.Ordinal);
        var aliasesProperty = typeof(TParser).GetProperty(
            "PropertyColumnAliases",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (aliasesProperty is not null)
        {
            try
            {
                var instance = new TParser();
                if (aliasesProperty.GetValue(instance) is IReadOnlyDictionary<string, string[]> value)
                {
                    aliases = value;
                }
            }
            catch
            {
                // Fall back to property-name-only resolution.
            }
        }

        var properties = typeof(TEntry)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0);

        foreach (var property in properties)
        {
            map.TryAdd(NormalizePluginColumnName(property.Name), property);
            if (aliases.TryGetValue(property.Name, out var propertyAliases))
            {
                foreach (var alias in propertyAliases)
                {
                    map.TryAdd(NormalizePluginColumnName(alias), property);
                }
            }
        }

        return column => string.IsNullOrWhiteSpace(column)
            ? null
            : map.TryGetValue(NormalizePluginColumnName(column), out var property) ? property : null;
    }

    private static string NormalizePluginColumnName(string name)
    {
        return new string(name
            .Trim()
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static Dictionary<string, FileParserRegistration> BuildParserRegistry()
    {
        var registry = new Dictionary<string, FileParserRegistration>(StringComparer.OrdinalIgnoreCase);

        void Register<TEntry, TParser>(
            string fileName,
            string rowIdentifierPropertyName,
            Func<TEntry, string?> rowIdentifierSelector,
            bool usesRowId = false)
            where TEntry : class, new()
            where TParser : HeaderMappedTextFileParser<TEntry, TParser>, new()
        {
            registry[fileName] = CreateRegistration<TEntry, TParser>(
                fileName, rowIdentifierPropertyName, rowIdentifierSelector, usesRowId);
        }

        Register<Armor, ArmorParser>("armor.txt", "Code", static e => e.Code);
        Register<AutoMagic, AutoMagicParser>("automagic.txt", "Name", static e => e.Name);
        Register<CharStats, CharStatsParser>("charstats.txt", "Class", static e => e.Class);
        Register<CubeMain, CubeMainParser>("cubemain.txt", "RowId", static e => e.Description, usesRowId: true);
        Register<CubeModifierType, CubeModifierTypeParser>("cubemod.txt", "CubeModifierTypeName", static e => e.CubeModifierTypeName);
        Register<DifficultyLevel, DifficultyLevelParser>("difficultylevels.txt", "Name", static e => e.Name);
        Register<Experience, ExperienceParser>("experience.txt", "Level", static e => e.Level);
        Register<Gamble, GambleParser>("gamble.txt", "Name", static e => e.Name);
        Register<Gem, GemParser>("gems.txt", "Name", static e => e.Name);
        Register<Hirelings, HirelingParser>("hireling.txt", "RowId", static e => e.Hireling, usesRowId: true);
        Register<Inventory, InventoryParser>("inventory.txt", "Class", static e => e.Class);
        Register<ItemType, ItemTypeParser>("itemtypes.txt", "Code", static e => e.Code);
        Register<LvlMaze, LvlMazeParser>("lvlmaze.txt", "RowId", static e => e.Name, usesRowId: true);
        Register<LevelsPreset, LvlPrestParser>("lvlprest.txt", "RowId", static e => e.Name, usesRowId: true);
        Register<LvlWarp, LvlWarpParser>("lvlwarp.txt", "Name", static e => e.Name);
        Register<MagicPrefix, MagicPrefixParser>("magicprefix.txt", "RowId", static e => e.Name, usesRowId: true);
        Register<MagicSuffix, MagicSuffixParser>("magicsuffix.txt", "RowId", static e => e.Name, usesRowId: true);
        Register<Misc, MiscParser>("misc.txt", "Code", static e => e.Code);
        Register<Missiles, MissilesParser>("missiles.txt", "MissileName", static e => e.MissileName);
        Register<MonEquip, MonEquipParser>("monequip.txt", "RowId", static e => e.Monster, usesRowId: true);
        Register<MonPreset, MonPresetParser>("monpreset.txt", "RowId", static e => e.Act?.ToString(), usesRowId: true);
        Register<MonProp, MonPropParser>("monprop.txt", "Id", static e => e.Id);
        Register<MonStat, MonStatsParser>("monstats.txt", "Id", static e => e.Id);
        Register<MonStats2, MonStats2Parser>("monstats2.txt", "Id", static e => e.Id);
        Register<MonType, MonTypeParser>("montype.txt", "Type", static e => e.Type);
        Register<MonUMod, MonUModParser>("monumod.txt", "UniqueModId", static e => e.UniqueModId);
        Register<Npc, NpcParser>("npc.txt", "NpcName", static e => e.NpcName);
        Register<PetType, PetTypeParser>("pettype.txt", "PetTypeId", static e => e.PetTypeId);
        Register<Property, PropertiesParser>("properties.txt", "Code", static e => e.Code);
        Register<PropertyGroup, PropertyGroupParser>("propertygroups.txt", "Code", static e => e.Code);
        Register<RuneWord, RunesParser>("runes.txt", "Name", static e => e.Name);
        Register<SetItem, SetItemParser>("setitems.txt", "Index", static e => e.Index);
        Register<Sets, SetsParser>("sets.txt", "Index", static e => e.Index);
        Register<Shrines, ShrinesParser>("shrines.txt", "Name", static e => e.Name);
        Register<SkillCalc, SkillCalcParser>("skillcalc.txt", "Code", static e => e.Code);
        Register<SkillDesc, SkillDescParser>("skilldesc.txt", "SkillName", static e => e.SkillName);
        Register<Skills, SkillsParser>("skills.txt", "Skill", static e => e.Skill);
        Register<Sounds, SoundsParser>("sounds.txt", "Sound", static e => e.Sound);
        Register<States, StatesParser>("states.txt", "StateId", static e => e.StateId);
        Register<StorePage, StorePageParser>("storepage.txt", "StorePageName", static e => e.StorePageName);
        Register<SuperUnique, SuperUniquesParser>("superuniques.txt", "Superunique", static e => e.Superunique);
        Register<TreasureClass, TreasureClassParser>("treasureclassex.txt", "TreasureClassName", static e => e.TreasureClassName);
        Register<UniqueItem, UniqueItemsParser>("uniqueitems.txt", "Index", static e => e.Index);
        Register<Weapon, WeaponParser>("weapons.txt", "Code", static e => e.Code);
        Register<ActInfo, ActInfoParser>("actinfo.txt", "Act", static e => e.Act?.ToString());
        Register<Automap, AutomapParser>("automap.txt", "RowId", static e => e.LevelName, usesRowId: true);
        Register<ItemUiCategory, ItemUiCategoryParser>("itemuicategories.txt", "Name", static e => e.Name);
        Register<LevelGroup, LevelGroupParser>("levelgroups.txt", "LevelGroupId", static e => e.LevelGroupId?.ToString());
        Register<Level, LevelParser>("levels.txt", "Name", static e => e.Name);
        Register<MonLvl, MonLvlParser>("monlvl.txt", "Level", static e => e.Level?.ToString());
        Register<MonPet, MonPetParser>("monpet.txt", "Monster", static e => e.Monster);
        Register<GameObject, ObjectsParser>("objects.txt", "RowId", static e => e.Name, usesRowId: true);
        Register<Overlay, OverlayParser>("overlay.txt", "OverlayName", static e => e.OverlayName);

        return registry;
    }

    private sealed class PluginInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string? ModVersion { get; set; }
        public string? Author { get; set; }
        public string? Description { get; set; }
        public List<string> Files { get; set; } = [];
        public List<PluginParameterDefinition> Parameters { get; set; } = [];
        public List<PluginAssetDefinition> Assets { get; set; } = [];
    }

    private sealed class PluginAssetDefinition
    {
        public string Source { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;

        // Optional declarative condition controlling whether the asset is copied during apply.
        // When omitted (default), the asset is always applied. See PluginJsonCondition for the
        // supported shapes: {parameterKey, equals|notEquals}, {all:[...]}, {any:[...]}, {not:...}.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PluginJsonCondition? Condition { get; set; }
    }

    private sealed class PluginParameterDefinition
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        // Optional parameter type: "text" (default, missing) renders as a textbox; "checkbox"
        // renders as a checkbox/switch and persists "true"/"false". Other types are reserved.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Type { get; set; }

        public string? Description { get; set; }

        // Optional display-only group label. UI renders parameters that share the same group
        // under a single heading; missing/null/empty means the parameter is ungrouped. This
        // metadata never participates in parameterKey lookups, condition evaluation, saving,
        // or plugin application; preserved across SavePluginInfoAsync round-trips.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Group { get; set; }

        public string DefaultValue { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    // Declarative condition used by operations and asset replacements. Pure data; never executed
    // as code. Exactly one shape per node: a parameterKey leaf with equals/notEquals, or one of
    // the all/any/not combinators wrapping further conditions.
    private sealed class PluginJsonCondition
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ParameterKey { get; set; }

        // C# `Equals` would shadow object.Equals and `equals` is a reserved-style identifier; use
        // a distinct property name and let JsonPropertyName preserve the public JSON shape.
        [JsonPropertyName("equals")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? EqualsValue { get; set; }

        [JsonPropertyName("notEquals")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NotEqualsValue { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<PluginJsonCondition>? All { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<PluginJsonCondition>? Any { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PluginJsonCondition? Not { get; set; }
    }

    // A single rowIdentifier matcher. Set Value for a scalar string match (default identifier
    // column, numeric index, or "start-end" range, depending on the file); set Columns for a
    // multi-column AND match (same semantics as the legacy object-form rowIdentifier). Authors
    // never write this type directly — it is the canonical shape produced by the JSON parser
    // for the array form of rowIdentifier (e.g. ["amazonjavazon", {"Class":"skeleton"}]).
    private sealed record PluginRowMatcher(
        string? Value,
        IReadOnlyDictionary<string, string>? Columns);

    private sealed record PluginJsonOperation(
        string? File,
        string? RowIdentifier,
        string? Column,
        string? Operation,
        string? ParameterKey,
        string? UpdatedValue,
        string? Key = null,
        IReadOnlyDictionary<string, string>? LanguageValues = null,
        IReadOnlyList<PluginJsonColumnAssignment>? Columns = null,
        // Optional override of the default rowIdentifier matching: when present, a row is considered
        // a match only when every key/value pair (column-name -> expected-value) matches the entry's
        // corresponding column. Authors can supply this either as an object literal under the
        // "rowIdentifier" property (e.g. {"key1":"col1","key2":"col2"}) or via a dedicated
        // "rowIdentifiers" property.
        IReadOnlyDictionary<string, string>? RowIdentifiers = null,
        // Canonical list of rowIdentifier matchers produced by the JSON parser when the author
        // supplied an *array* under "rowIdentifier" (e.g. ["a", "b", {"Col":"v"}]). Each element is
        // either a scalar string (parsed exactly like a top-level string rowIdentifier — supports
        // numeric index, "start-end" range, or default-column value) or a multi-column AND map.
        // Matched indices are OR-combined and de-duplicated. Only honored by updateRow operations;
        // addRow / cloneRow / swapRow reject this shape because they target a single row.
        [property: JsonIgnore]
        IReadOnlyList<PluginRowMatcher>? RowMatchers = null,
        // cloneRow: identifies the row to copy from. Accepts a numeric 0-based index or a value
        // matched against the file's default rowIdentifier column (case-insensitive).
        string? SourceRowIdentifier = null,
        // cloneRow: "add" (default) appends the cloned row to the end of the file (rowIdentifier
        // must be omitted; insertion at a specific index is not supported). "replace" overwrites
        // the row whose identifier (numeric index or default rowIdentifier column value) matches
        // rowIdentifier.
        string? Mode = null,
        // swapRow: identifies the second row to exchange with the row referenced by rowIdentifier.
        // Accepts a numeric 0-based index or a default rowIdentifier column value.
        string? SwapRowIdentifier = null,
        // swapRow: optional column overrides applied to the row at SwapRowIdentifier *after* the
        // swap (the row originally at rowIdentifier). The standard "columns"/"column" assignments
        // apply post-swap to the row at rowIdentifier (originally at SwapRowIdentifier).
        IReadOnlyList<PluginJsonColumnAssignment>? SwapColumns = null,
        // Optional declarative condition controlling whether the operation is applied. When
        // omitted (default), the operation is always applied. See PluginJsonCondition.
        PluginJsonCondition? Condition = null);

    // Per-column assignment used either for multi-column updates that share a single rowIdentifier,
    // or to specify the column/value pairs of a new row produced by the addRow operation.
    // When a per-column field is null/empty, the parent operation's matching field is used as the
    // fallback (Operation defaults to the parent's Operation, "replace" semantics for addRow).
    private sealed record PluginJsonColumnAssignment(
        string? Column,
        string? UpdatedValue = null,
        string? ParameterKey = null,
        string? Operation = null);
}
