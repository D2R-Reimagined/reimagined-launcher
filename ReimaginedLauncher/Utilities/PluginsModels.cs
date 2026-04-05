using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ReimaginedLauncher.Utilities;

public sealed class PluginCatalogItem : INotifyPropertyChanged
{
    private bool _isParametersExpanded;

    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsEnabled { get; init; }
    public int Order { get; init; }
    public IReadOnlyList<PluginParameterItem> Parameters { get; init; } = [];
    public IReadOnlyList<PluginCatalogFileItem> Files { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public bool HasErrors => Errors.Count > 0;
    public bool HasParameters => Parameters.Count > 0;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool IsParametersExpanded
    {
        get => _isParametersExpanded;
        set
        {
            if (_isParametersExpanded == value)
            {
                return;
            }

            _isParametersExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ParametersToggleText));
            OnPropertyChanged(nameof(ParametersActionText));
        }
    }

    public string ParametersToggleText => IsParametersExpanded
        ? $"Hide Parameters ({Parameters.Count})"
        : $"Show Parameters ({Parameters.Count})";
    public string ParametersActionText => IsParametersExpanded ? "Collapse" : "Expand";

    public string StatusText => HasErrors
        ? $"{Errors.Count} error(s)"
        : IsEnabled
            ? "Enabled"
            : "Disabled";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class OfficialPluginCatalogItem
{
    public string FolderName { get; init; } = string.Empty;
    public string PluginId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsInstalled { get; init; }
    public bool IsEnabled { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public bool HasErrors => Errors.Count > 0;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool CanInstallOrEnable => !HasErrors && (!IsInstalled || !IsEnabled);
    public string ActionText => !IsInstalled
        ? "Install"
        : IsEnabled
            ? "Installed"
            : "Enable";
    public string StatusText => HasErrors
        ? $"{Errors.Count} error(s)"
        : !IsInstalled
            ? "Not installed"
            : IsEnabled
                ? "Enabled"
                : "Disabled";
}

public sealed class PluginParameterItem
{
    public string PluginId { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string DefaultValue { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

public sealed class PluginCatalogFileItem
{
    public string PluginId { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

public sealed class PluginEditorDocument
{
    public string PluginId { get; init; } = string.Empty;
    public string PluginName { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
}

public sealed class PluginImportPreview
{
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
}

public sealed class InstalledPluginLookupResult
{
    public string PluginId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
}
