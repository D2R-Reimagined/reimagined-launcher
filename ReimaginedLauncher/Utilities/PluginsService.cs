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
    private const string SkillsFileName = "skills.txt";
    private const string SkillsRowIdentifierPropertyName = "Skill";
    private static readonly JsonSerializerOptions JsonOptions = SerializerOptions.PropertyNameCaseInsensitive;
    private static readonly Regex ParameterTokenRegex = new(@"\{\{\s*parameter:([a-zA-Z0-9_\-]+)\s*\}\}", RegexOptions.Compiled);

    public static string PluginsDirectoryPath => Path.Combine(SettingsManager.AppDirectoryPath, PluginsDirectoryName);

    public static async Task EnsureBundledPluginsInstalledAsync()
    {
        var bundledPluginsRoot = Path.Combine(AppContext.BaseDirectory, BundledPluginsDirectoryName);
        if (!Directory.Exists(bundledPluginsRoot))
        {
            return;
        }

        MainWindow.Settings.Plugins ??= [];
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

            var existingRegistration = MainWindow.Settings.Plugins
                .FirstOrDefault(plugin => plugin.FolderName.Equals(folderName, StringComparison.OrdinalIgnoreCase));

            var destinationDirectory = Path.Combine(PluginsDirectoryPath, folderName);
            if (existingRegistration != null || Directory.Exists(destinationDirectory))
            {
                continue;
            }

            await CopyDirectoryAsync(sourceDirectory, destinationDirectory);
            MainWindow.Settings.Plugins.Add(new PluginRegistration
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

        MainWindow.Settings.Plugins ??= [];
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
                errors.Add($"plugininfo.json is invalid: {ex.Message}");
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
        MainWindow.Settings.Plugins ??= [];
        var catalog = new List<PluginCatalogItem>(MainWindow.Settings.Plugins.Count);

        for (var index = 0; index < MainWindow.Settings.Plugins.Count; index++)
        {
            var registration = MainWindow.Settings.Plugins[index];
            var pluginState = await LoadPluginStateAsync(registration);
            catalog.Add(new PluginCatalogItem
            {
                Id = registration.Id,
                Name = pluginState.Name,
                Version = pluginState.Version,
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
        MainWindow.Settings.Plugins ??= [];

        var registration = FindRegistrationByFolderName(folderName);
        var destinationDirectory = Path.Combine(PluginsDirectoryPath, folderName);
        if (!Directory.Exists(destinationDirectory))
        {
            await CopyDirectoryAsync(sourceDirectory, destinationDirectory);
        }

        if (registration == null)
        {
            MainWindow.Settings.Plugins.Add(new PluginRegistration
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
        MainWindow.Settings.Plugins ??= [];

        foreach (var registration in MainWindow.Settings.Plugins)
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

            MainWindow.Settings.Plugins ??= [];
            MainWindow.Settings.Plugins.Add(new PluginRegistration
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
        MainWindow.Settings.Plugins ??= [];
        var currentIndex = MainWindow.Settings.Plugins.FindIndex(plugin => plugin.Id == pluginId);
        if (currentIndex < 0)
        {
            throw new InvalidOperationException("The selected plugin could not be found.");
        }

        var nextIndex = currentIndex + direction;
        if (nextIndex < 0 || nextIndex >= MainWindow.Settings.Plugins.Count)
        {
            return;
        }

        (MainWindow.Settings.Plugins[currentIndex], MainWindow.Settings.Plugins[nextIndex]) =
            (MainWindow.Settings.Plugins[nextIndex], MainWindow.Settings.Plugins[currentIndex]);

        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    public static async Task DeletePluginAsync(string pluginId)
    {
        MainWindow.Settings.Plugins ??= [];
        var registration = GetRegistration(pluginId);
        MainWindow.Settings.Plugins.Remove(registration);
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
        MainWindow.Settings.Plugins ??= [];

        foreach (var registration in MainWindow.Settings.Plugins.Where(plugin => plugin.IsEnabled))
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
        return "Supported now: skills.txt, with rowIdentifier matched against the skill column. Multiply-existing operations can reference parameters declared in plugininfo.json.";
    }

    private static async Task ApplyOperationsAsync(
        string excelDirectory,
        IReadOnlyList<PluginJsonOperation> operations,
        IReadOnlyDictionary<string, string> parameters)
    {
        var skillsOperations = operations
            .Where(operation => string.Equals(operation.File, SkillsFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (skillsOperations.Count == 0)
        {
            return;
        }

        var skillsFilePath = Path.Combine(excelDirectory, SkillsFileName);
        if (!File.Exists(skillsFilePath))
        {
            throw new FileNotFoundException($"skills.txt was not found in the target excel directory: {excelDirectory}");
        }

        var entries = (await SkillsParser.GetEntries(skillsFilePath)).ToList();
        if (entries.Count == 0)
        {
            throw new InvalidDataException("skills.txt did not contain any editable rows for plugin execution.");
        }

        foreach (var operation in skillsOperations)
        {
            var matchingIndices = Enumerable.Range(0, entries.Count)
                .Where(i => string.Equals(entries[i].Skill, operation.RowIdentifier, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matchingIndices.Count == 0)
            {
                throw new InvalidDataException(
                    $"Could not find row '{operation.RowIdentifier}' in skills.txt using the {SkillsRowIdentifierPropertyName} column.");
            }

            foreach (var entryIndex in matchingIndices)
            {
                entries[entryIndex] = UpdateRecord(
                    entries[entryIndex],
                    operation.Column ?? string.Empty,
                    ResolveOperationValue(entries[entryIndex], operation, parameters));
            }
        }

        await SaveGeneratedEntriesAsync(
            entries,
            skillsFilePath,
            (updatedEntries, filePath, outputDirectory, cancellationToken) =>
                SkillsParser.SaveEntries(updatedEntries, filePath, outputDirectory, cancellationToken));
    }

    private static Skills UpdateRecord(Skills entry, string column, string? updatedValue)
    {
        var property = typeof(Skills).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(candidate => candidate.Name.Equals(column, StringComparison.OrdinalIgnoreCase));

        if (property == null)
        {
            throw new InvalidDataException($"The column '{column}' is not supported for skills.txt.");
        }

        var clonedEntry = entry with { };
        var convertedValue = ConvertValue(updatedValue, property.PropertyType, column);
        property.SetValue(clonedEntry, convertedValue);
        return clonedEntry;
    }

    private static string? ResolveOperationValue(
        Skills entry,
        PluginJsonOperation operation,
        IReadOnlyDictionary<string, string> parameters)
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
            var property = typeof(Skills).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(candidate => candidate.Name.Equals(column, StringComparison.OrdinalIgnoreCase));

            if (property == null)
            {
                throw new InvalidDataException($"The column '{column}' is not supported for skills.txt.");
            }

            var currentValue = property.GetValue(entry)?.ToString();
            if (!decimal.TryParse(currentValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var currentNumber))
            {
                throw new InvalidDataException(
                    $"The existing value '{currentValue}' in column '{column}' is not numeric and cannot be multiplied.");
            }

            var multiplierText = ResolveMultiplierValue(operation, parameters, resolvedUpdatedValue);
            if (!decimal.TryParse(multiplierText, NumberStyles.Float, CultureInfo.InvariantCulture, out var multiplier))
            {
                throw new InvalidDataException(
                    $"The multiplier value '{multiplierText}' is not a valid decimal number.");
            }

            return FormatDecimalValue(currentNumber * multiplier);
        }

        throw new InvalidDataException($"Unsupported plugin operation '{operationType}'.");
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
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
            {
                return parsedInt;
            }

            throw new InvalidDataException($"The value '{value}' is not a valid integer for column '{column}'.");
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
        var description = string.Empty;

        if (!Directory.Exists(pluginDirectory))
        {
            errors.Add("Imported plugin files are missing from disk.");
            return new PluginState(name, version, description, parameters, files, errors);
        }

        var pluginInfoPath = Path.Combine(pluginDirectory, PluginInfoFileName);
        if (!File.Exists(pluginInfoPath))
        {
            errors.Add("plugininfo.json is missing.");
            return new PluginState(name, version, description, parameters, files, errors);
        }

        try
        {
            var pluginInfo = await LoadPluginInfoAsync(pluginInfoPath);
            name = pluginInfo.Name;
            version = pluginInfo.Version;
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
                return new PluginState(name, version, description, parameters, files, errors);
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
                    errors.Add($"'{normalizedRelativePath}' is invalid: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"plugininfo.json is invalid: {ex.Message}");
        }

        return new PluginState(name, version, description, parameters, files, errors);
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

            if (!operation.File.Equals(SkillsFileName, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"'{pluginFileName}' targets unsupported file '{operation.File}'. Only {SkillsFileName} is supported right now.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(operation.RowIdentifier))
            {
                errors.Add($"'{pluginFileName}' contains a skills.txt operation with no rowIdentifier.");
            }

            if (string.IsNullOrWhiteSpace(operation.Column))
            {
                errors.Add($"'{pluginFileName}' contains a skills.txt operation with no column.");
                continue;
            }

            var propertyExists = typeof(Skills).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(property => property.Name.Equals(operation.Column, StringComparison.OrdinalIgnoreCase));

            if (!propertyExists)
            {
                errors.Add($"'{pluginFileName}' references unknown skills.txt column '{operation.Column}'.");
            }

            if (!string.IsNullOrWhiteSpace(operation.Operation) &&
                operation.Operation.Equals("multiplyExisting", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(operation.ParameterKey) &&
                string.IsNullOrWhiteSpace(operation.UpdatedValue))
            {
                errors.Add($"'{pluginFileName}' contains a multiplyExisting operation without parameterKey or updatedValue.");
            }

            if (!string.IsNullOrWhiteSpace(operation.ParameterKey) &&
                parameters.All(parameter => !parameter.Key.Equals(operation.ParameterKey, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"'{pluginFileName}' references unknown parameter '{operation.ParameterKey}'.");
            }
        }
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
        var operation = JsonSerializer.Deserialize<PluginJsonOperation>(json, JsonOptions);
        if (operation == null)
        {
            throw new InvalidDataException("Plugin JSON could not be parsed.");
        }

        return [NormalizeOperation(operation)];
    }

    private static IReadOnlyList<PluginJsonOperation> DeserializeOperationArray(string json)
    {
        var operations = JsonSerializer.Deserialize<List<PluginJsonOperation>>(json, JsonOptions);
        if (operations == null)
        {
            throw new InvalidDataException("Plugin JSON array could not be parsed.");
        }

        return operations.Select(NormalizeOperation).ToList();
    }

    private static PluginJsonOperation NormalizeOperation(PluginJsonOperation operation)
    {
        return operation with
        {
            File = NormalizeRelativePath(operation.File ?? string.Empty),
            RowIdentifier = operation.RowIdentifier?.Trim() ?? string.Empty,
            Column = operation.Column?.Trim() ?? string.Empty,
            Operation = operation.Operation?.Trim(),
            ParameterKey = operation.ParameterKey?.Trim()
        };
    }

    private static PluginRegistration GetRegistration(string pluginId)
    {
        MainWindow.Settings.Plugins ??= [];
        var registration = MainWindow.Settings.Plugins.FirstOrDefault(plugin => plugin.Id == pluginId);
        if (registration == null)
        {
            throw new InvalidOperationException("The selected plugin could not be found.");
        }

        return registration;
    }

    private static PluginRegistration? FindRegistrationByFolderName(string folderName)
    {
        MainWindow.Settings.Plugins ??= [];
        return MainWindow.Settings.Plugins.FirstOrDefault(plugin =>
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
        string Description,
        IReadOnlyList<PluginParameterItem> Parameters,
        IReadOnlyList<PluginCatalogFileItem> Files,
        IReadOnlyList<string> Errors);

    private sealed class PluginInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
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
        string? UpdatedValue);
}
