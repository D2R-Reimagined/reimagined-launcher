using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ReimaginedLauncher.Utilities;

public sealed class PluginCatalogItem : INotifyPropertyChanged
{
    private bool _isParametersExpanded;

    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string ModVersion { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsEnabled { get; init; }
    public int Order { get; init; }
    public IReadOnlyList<PluginParameterItem> Parameters { get; init; } = [];

    // Display-only grouping projection over Parameters. Groups appear in the order they first
    // occur in Parameters; within a group the original parameter order is preserved. When no
    // parameter declares a Group, a single ungrouped bucket is produced with HasHeading = false
    // so the legacy flat layout is rendered. This is purely a UI convenience: parameter lookup,
    // condition evaluation, saving, and apply behavior all continue to use the flat Parameters
    // list.
    public IReadOnlyList<PluginParameterGroup> ParameterGroups
    {
        get
        {
            var groups = new List<PluginParameterGroup>();
            var byKey = new Dictionary<string, List<PluginParameterItem>>(System.StringComparer.Ordinal);
            var anyGrouped = Parameters.Any(p => !string.IsNullOrWhiteSpace(p.Group));

            foreach (var parameter in Parameters)
            {
                var key = parameter.Group ?? string.Empty;
                if (!byKey.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    byKey[key] = bucket;
                    groups.Add(new PluginParameterGroup
                    {
                        Title = key,
                        HasHeading = anyGrouped && !string.IsNullOrWhiteSpace(key),
                        Parameters = bucket
                    });
                }
                bucket.Add(parameter);
            }

            return groups;
        }
    }

    public IReadOnlyList<PluginCatalogFileItem> Files { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool HasErrors => Errors.Count > 0;
    public bool HasWarnings => Warnings.Count > 0;
    public bool HasParameters => Parameters.Count > 0;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasModVersion => !string.IsNullOrWhiteSpace(ModVersion);
    public bool HasAuthor => !string.IsNullOrWhiteSpace(Author);
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

    // Parameter type from plugininfo.json. Empty/null is treated as "text" for backward
    // compatibility with plugins authored before the type system existed.
    public string Type { get; init; } = string.Empty;

    // Optional display-only group label from plugininfo.json. When set, the parameter is
    // rendered under a section heading in the Plugins page; this does not affect parameter
    // lookup, condition evaluation, saving, or plugin application. Empty/null/whitespace
    // means the parameter is ungrouped and renders in the default flat area.
    public string Group { get; init; } = string.Empty;

    // True when the parameter should render as a checkbox/switch and persist "true"/"false".
    public bool IsCheckboxParameter =>
        string.Equals(Type, "checkbox", System.StringComparison.OrdinalIgnoreCase);

    // True when the parameter should render as the legacy text editor.
    public bool IsTextParameter => !IsCheckboxParameter;

    // Convenience for binding the checkbox IsChecked one-way; we treat "true"/"1"/"yes"/"on"
    // (case-insensitive) as checked, matching the lenient parser used by SaveParameterValueAsync.
    public bool IsChecked =>
        Value is { Length: > 0 } v && (
            v.Equals("true", System.StringComparison.OrdinalIgnoreCase) ||
            v.Equals("1", System.StringComparison.Ordinal) ||
            v.Equals("yes", System.StringComparison.OrdinalIgnoreCase) ||
            v.Equals("on", System.StringComparison.OrdinalIgnoreCase) ||
            v.Equals("checked", System.StringComparison.OrdinalIgnoreCase));
}

public sealed class PluginParameterGroup
{
    // Heading text shown above the group's parameters. Empty for the implicit ungrouped bucket.
    public string Title { get; init; } = string.Empty;

    // True when the heading should be rendered. False for the single implicit bucket produced
    // when no parameter declares a group, so the existing flat layout is preserved.
    public bool HasHeading { get; init; }

    public IReadOnlyList<PluginParameterItem> Parameters { get; init; } = [];
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
