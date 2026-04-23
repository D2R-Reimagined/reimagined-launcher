using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
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
    private static readonly Regex RowRangeRegex = new(@"^\s*(\d+)\s*-\s*(\d+)\s*$", RegexOptions.Compiled);

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
                Errors = pluginState.Errors
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

        var pluginDirectory = GetPluginDirectory(registration);
        if (Directory.Exists(pluginDirectory))
        {
            Directory.Delete(pluginDirectory, recursive: true);
        }
    }

    public static async Task<PluginEditorDocument> LoadEditorDocumentAsync(string pluginId, string relativePath)
    {
        var pluginState = await LoadPluginStateAsync(GetRegistration(pluginId));
        var absolutePath = GetPluginFilePath(pluginId, relativePath);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException("The selected plugin JSON file could not be found.", relativePath);
        }

        return new PluginEditorDocument
        {
            PluginId = pluginId,
            PluginName = pluginState.Name,
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

        var normalizedValue = value.Trim();
        var currentValue = string.IsNullOrWhiteSpace(parameter.Value) ? parameter.DefaultValue : parameter.Value;
        if (string.Equals(currentValue, normalizedValue, StringComparison.Ordinal))
        {
            return false;
        }

        parameter.Value = normalizedValue;
        await SavePluginInfoAsync(pluginInfoPath, pluginInfo);
        return true;
    }

    public static async Task ApplyEnabledPluginsAsync(string excelDirectory, IProgress<string>? progress = null)
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

            ReportProgress(progress, $"Applying plugin {pluginState.Name}...");

            try
            {
                foreach (var pluginFile in pluginState.Files)
                {
                    var operations = await LoadPluginOperationsAsync(GetPluginFilePath(registration.Id, pluginFile.RelativePath));
                    var parameters = pluginState.Parameters.ToDictionary(
                        parameter => parameter.Key,
                        parameter => parameter.Value,
                        StringComparer.OrdinalIgnoreCase);
                    await ApplyOperationsAsync(excelDirectory, operations, parameters);
                }
            }
            catch (Exception ex)
            {
                LaunchDiagnostics.LogException($"Failed to apply plugin '{pluginState.Name}'", ex);
                Notifications.SendNotification($"Plugin '{pluginState.Name}' failed: {ex.Message}", "Warning");
            }
        }
    }

    public static string GetSupportedTargetsSummary()
    {
        return "All .txt files in the base excel folder are supported except itemstatcost.txt. Most files match rows by a unique column; files with duplicate values in their identifier column use a numeric row ID (0-based data row index) instead. Multiply-existing and append operations can reference parameters declared in plugininfo.json. String JSON files from data/local/lng/strings (e.g. item-runes.json) are also supported using the same flat d2rr-style layout: each entry lists the target file, the D2R Key, and one or more language fields (enUS, zhTW, deDE, esES, frFR, itIT, koKR, plPL, esMX, jaJP, ptBR, ruRU, zhCN); only the listed languages are replaced and any other languages on that entry are left untouched.";
    }

    private static async Task ApplyOperationsAsync(
        string excelDirectory,
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
            if (ParserRegistry.TryGetValue(fileName, out var registration))
            {
                await registration.ApplyAsync(excelDirectory, operations, parameters);
                continue;
            }

            if (IsStringsTargetFile(fileName))
            {
                resolvedStringsDirectory ??= ResolveStringsDirectory(excelDirectory)
                    ?? throw new DirectoryNotFoundException(
                        $"Could not resolve the strings directory ({StringsDirectoryRelativePath}) relative to '{excelDirectory}'.");

                await ApplyStringsOperationsForTargetAsync(resolvedStringsDirectory, operations, fileName);
            }
        }
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

        return fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveStringsDirectory(string excelDirectory)
    {
        if (string.IsNullOrWhiteSpace(excelDirectory))
        {
            return null;
        }

        var current = new DirectoryInfo(excelDirectory);
        while (current != null && !string.Equals(current.Name, "data", StringComparison.OrdinalIgnoreCase))
        {
            current = current.Parent;
        }

        if (current == null)
        {
            return null;
        }

        return Path.Combine(current.FullName, "local", "lng", "strings");
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
        where TEntry : class
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
            List<int> matchingIndices;

            if (TryParseRowRange(operation.RowIdentifier, out var rangeStart, out var rangeEnd))
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

            foreach (var entryIndex in matchingIndices)
            {
                entries[entryIndex] = UpdateRecord(
                    entries[entryIndex],
                    operation.Column ?? string.Empty,
                    ResolveOperationValue(entries[entryIndex], operation, parameters, fileName, resolveColumn),
                    fileName,
                    resolveColumn,
                    operation.RowIdentifier);
            }
        }

        await SaveGeneratedEntriesAsync(entries, filePath, saveEntriesAsync);
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

        if (operationType.Equals("multiplyExisting", StringComparison.OrdinalIgnoreCase))
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
                    $"The existing value '{currentValue}' in column '{column}' is not numeric and cannot be multiplied (file '{fileName}', row '{operation.RowIdentifier ?? "<unknown>"}').");
            }

            var multiplierText = ResolveMultiplierValue(operation, parameters, resolvedUpdatedValue);
            if (!decimal.TryParse(multiplierText, NumberStyles.Float, CultureInfo.InvariantCulture, out var multiplier))
            {
                throw new InvalidDataException(
                    $"The multiplier value '{multiplierText}' is not a valid decimal number (file '{fileName}', row '{operation.RowIdentifier ?? "<unknown>"}', column '{column}').");
            }

            return FormatDecimalValue(currentNumber * multiplier);
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

    private static string ResolveMultiplierValue(
        PluginJsonOperation operation,
        IReadOnlyDictionary<string, string> parameters,
        string? resolvedUpdatedValue)
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

        throw new InvalidDataException("multiplyExisting operations require either parameterKey or updatedValue.");
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
        var files = new List<PluginCatalogFileItem>();
        var parameters = new List<PluginParameterItem>();
        var name = registration.FolderName;
        var version = "Unknown";
        var modVersion = string.Empty;
        var author = string.Empty;
        var description = string.Empty;

        if (!Directory.Exists(pluginDirectory))
        {
            errors.Add("Imported plugin files are missing from disk.");
            return new PluginState(name, version, modVersion, author, description, parameters, files, errors);
        }

        var pluginInfoPath = Path.Combine(pluginDirectory, PluginInfoFileName);
        if (!File.Exists(pluginInfoPath))
        {
            errors.Add("plugininfo.json is missing.");
            return new PluginState(name, version, modVersion, author, description, parameters, files, errors);
        }

        try
        {
            var pluginInfo = await LoadPluginInfoAsync(pluginInfoPath);
            name = pluginInfo.Name;
            version = pluginInfo.Version;
            modVersion = pluginInfo.ModVersion ?? string.Empty;
            author = pluginInfo.Author ?? string.Empty;
            description = pluginInfo.Description ?? string.Empty;
            parameters = pluginInfo.Parameters.Select(parameter => new PluginParameterItem
            {
                PluginId = registration.Id,
                Key = parameter.Key,
                DisplayName = parameter.Name,
                Description = parameter.Description ?? string.Empty,
                DefaultValue = parameter.DefaultValue,
                Value = string.IsNullOrWhiteSpace(parameter.Value) ? parameter.DefaultValue : parameter.Value
            }).ToList();

            if (pluginInfo.Files.Count == 0)
            {
                errors.Add("plugininfo.json does not list any plugin JSON files.");
                return new PluginState(name, version, modVersion, author, description, parameters, files, errors);
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
                    ValidateOperations(operations, normalizedRelativePath, parameters, errors);
                }
                catch (Exception ex)
                {
                    errors.Add(FormatJsonError(normalizedRelativePath, ex));
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add(FormatJsonError("plugininfo.json", ex));
        }

        return new PluginState(name, version, modVersion, author, description, parameters, files, errors);
    }

    private static void ValidateOperations(
        IReadOnlyList<PluginJsonOperation> operations,
        string pluginFileName,
        IReadOnlyList<PluginParameterItem> parameters,
        List<string> errors)
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

            if (string.IsNullOrWhiteSpace(operation.RowIdentifier))
            {
                errors.Add($"'{pluginFileName}' contains a {supportedTarget.FileName} operation with no rowIdentifier.");
            }
            else if (supportedTarget.UsesRowId
                     && !int.TryParse(operation.RowIdentifier, out _)
                     && !TryParseRowRange(operation.RowIdentifier, out _, out _))
            {
                errors.Add($"'{pluginFileName}' contains a {supportedTarget.FileName} operation with non-numeric rowIdentifier '{operation.RowIdentifier}'. This file uses numeric row IDs (e.g. \"5\" or a range like \"50-100\").");
            }

            if (string.IsNullOrWhiteSpace(operation.Column))
            {
                errors.Add($"'{pluginFileName}' contains a {supportedTarget.FileName} operation with no column.");
                continue;
            }

            var propertyExists = supportedTarget.EntryType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(property => property.Name.Equals(operation.Column, StringComparison.OrdinalIgnoreCase));

            if (!propertyExists && supportedTarget.ResolveColumn != null)
            {
                propertyExists = supportedTarget.ResolveColumn(operation.Column!) != null;
            }

            if (!propertyExists)
            {
                errors.Add($"'{pluginFileName}' references unknown {supportedTarget.FileName} column '{operation.Column}'.");
            }

            if (!string.IsNullOrWhiteSpace(operation.Operation) &&
                (operation.Operation.Equals("multiplyExisting", StringComparison.OrdinalIgnoreCase) ||
                 operation.Operation.Equals("append", StringComparison.OrdinalIgnoreCase)) &&
                string.IsNullOrWhiteSpace(operation.ParameterKey) &&
                string.IsNullOrWhiteSpace(operation.UpdatedValue))
            {
                errors.Add($"'{pluginFileName}' contains a {operation.Operation} operation without parameterKey or updatedValue.");
            }

            if (!string.IsNullOrWhiteSpace(operation.ParameterKey) &&
                parameters.All(parameter => !parameter.Key.Equals(operation.ParameterKey, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"'{pluginFileName}' references unknown parameter '{operation.ParameterKey}'.");
            }
        }
    }

    private static bool IsSupportedTargetFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return ParserRegistry.ContainsKey(fileName) || IsStringsTargetFile(fileName);
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
            Parameters = pluginInfo.Parameters ?? []
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

        if (pluginInfo.Files.Count == 0)
        {
            throw new InvalidDataException("plugininfo.json must include at least one plugin JSON file.");
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

        var operation = JsonSerializer.Deserialize<PluginJsonOperation>(element.GetRawText(), JsonOptions);
        if (operation == null)
        {
            throw new InvalidDataException("Plugin JSON could not be parsed.");
        }

        return operation;
    }

    private static PluginJsonOperation NormalizeOperation(PluginJsonOperation operation)
    {
        return operation with
        {
            File = NormalizeRelativePath(operation.File ?? string.Empty),
            RowIdentifier = operation.RowIdentifier?.Trim() ?? string.Empty,
            Column = operation.Column?.Trim() ?? string.Empty,
            Operation = operation.Operation?.Trim(),
            ParameterKey = operation.ParameterKey?.Trim(),
            Key = operation.Key?.Trim()
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
        IReadOnlyList<string> Errors);

    private sealed record SupportedPluginTarget(
        string FileName,
        Type EntryType,
        string RowIdentifierPropertyName,
        bool UsesRowId,
        Func<string, PropertyInfo?>? ResolveColumn = null,
        bool IsStringsTarget = false);

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
        where TEntry : class
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
            where TEntry : class
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
    }

    private sealed class PluginParameterDefinition
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string DefaultValue { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    private sealed record PluginJsonOperation(
        string? File,
        string? RowIdentifier,
        string? Column,
        string? Operation,
        string? ParameterKey,
        string? UpdatedValue,
        string? Key = null,
        IReadOnlyDictionary<string, string>? LanguageValues = null);
}
