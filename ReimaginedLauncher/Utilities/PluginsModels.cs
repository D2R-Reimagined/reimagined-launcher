using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ReimaginedLauncher.Utilities;

public sealed class PluginCatalogItem : INotifyPropertyChanged
{
    private bool _isEnabled;
    private bool _isParametersExpanded;
    private IReadOnlyList<PluginParameterItem> _parameters = [];

    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string ModVersion { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;

            _isEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }
    public int Order { get; init; }
    public string? DiscussionUrl { get; init; }
    public string? UserPluginVersion { get; init; }
    public string? LatestPluginVersion { get; set; }
    public bool IsUserPlugin => !string.IsNullOrWhiteSpace(DiscussionUrl);
    public bool HasUserPluginVersion => !string.IsNullOrWhiteSpace(UserPluginVersion);
    // True when the discussion advertises a plug version newer/different than the installed one.
    public bool HasPluginUpdate => !string.IsNullOrWhiteSpace(LatestPluginVersion);

    // Setting the parameter list wires up live visibility: each parameter's IsVisible is recomputed
    // from its VisibleWhen condition whenever any sibling parameter's Value changes, so dropdowns and
    // checkboxes can show/hide dependent controls without rebuilding the whole catalog.
    public IReadOnlyList<PluginParameterItem> Parameters
    {
        get => _parameters;
        init
        {
            _parameters = value;
            foreach (var parameter in _parameters)
            {
                parameter.PropertyChanged += OnParameterPropertyChanged;
            }

            RecomputeParameterVisibility();
        }
    }

    private void OnParameterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PluginParameterItem.Value))
        {
            RecomputeParameterVisibility();
        }
    }

    private void RecomputeParameterVisibility()
    {
        var values = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in _parameters)
        {
            values[parameter.Key] = parameter.Value;
        }

        foreach (var parameter in _parameters)
        {
            parameter.IsVisible = parameter.VisibleWhen is null || parameter.VisibleWhen.Evaluate(values);
        }
    }

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

public sealed class PluginParameterItem : INotifyPropertyChanged
{
    private string _value = string.Empty;
    private bool _isVisible = true;

    public string PluginId { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string DefaultValue { get; init; } = string.Empty;

    // Effective parameter value. Mutable + observable so dropdown/checkbox edits can update visibility
    // of dependent parameters live without a full catalog rebuild.
    public string Value
    {
        get => _value;
        init => _value = value;
    }

    // Applies an in-memory value edit and notifies bindings (IsChecked) and the owning catalog item
    // (visibility recompute). Used by the Plugins view after persisting a dropdown/checkbox change.
    public void UpdateValue(string newValue)
    {
        if (string.Equals(_value, newValue, System.StringComparison.Ordinal))
        {
            return;
        }

        _value = newValue;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(IsChecked));
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
            {
                return;
            }

            _isVisible = value;
            OnPropertyChanged(nameof(IsVisible));
        }
    }

    // Parameter type from plugininfo.json. Empty/null is treated as "text" for backward
    // compatibility with plugins authored before the type system existed.
    public string Type { get; init; } = string.Empty;

    // Allowed values for a dropdown ('dropdown' type) parameter; empty for other types.
    public IReadOnlyList<string> Options { get; init; } = [];

    // Optional condition gating this parameter's visibility in the UI. Null means always visible.
    public PluginParameterCondition? VisibleWhen { get; init; }

    // A parameter that only appears based on another parameter's value (visibleWhen) is treated as
    // subordinate to it, and is indented in the UI to show that dependency hierarchy.
    public bool IsSubordinate => VisibleWhen != null;

    // Per-item bottom spacing plus a left indent for subordinate (visibleWhen-gated) parameters.
    public Avalonia.Thickness ItemMargin =>
        IsSubordinate ? new Avalonia.Thickness(20, 0, 0, 8) : new Avalonia.Thickness(0, 0, 0, 8);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    // Optional display-only group label from plugininfo.json. When set, the parameter is
    // rendered under a section heading in the Plugins page; this does not affect parameter
    // lookup, condition evaluation, saving, or plugin application. Empty/null/whitespace
    // means the parameter is ungrouped and renders in the default flat area.
    public string Group { get; init; } = string.Empty;

    // True when the parameter should render as a checkbox/switch and persist "true"/"false".
    public bool IsCheckboxParameter =>
        string.Equals(Type, "checkbox", System.StringComparison.OrdinalIgnoreCase);

    // True when the parameter should render as a dropdown/combobox selecting one of Options.
    public bool IsDropdownParameter =>
        string.Equals(Type, "dropdown", System.StringComparison.OrdinalIgnoreCase);

    // True when the parameter should render as the legacy text editor.
    public bool IsTextParameter => !IsCheckboxParameter && !IsDropdownParameter;

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

// UI-facing copy of a declarative plugin condition (mirrors the service's internal PluginJsonCondition).
// Pure data; Evaluate performs case-insensitive string comparison against effective parameter values
// and is used only to drive live parameter visibility (visibleWhen) on the Plugins page. Apply-time
// behavior continues to use the service's own evaluator.
public sealed class PluginParameterCondition
{
    public string? ParameterKey { get; init; }
    public string? EqualsValue { get; init; }
    public string? NotEqualsValue { get; init; }
    public IReadOnlyList<PluginParameterCondition>? All { get; init; }
    public IReadOnlyList<PluginParameterCondition>? Any { get; init; }
    public PluginParameterCondition? Not { get; init; }

    public bool Evaluate(IReadOnlyDictionary<string, string> values)
    {
        if (!string.IsNullOrWhiteSpace(ParameterKey))
        {
            values.TryGetValue(ParameterKey!, out var value);
            value ??= string.Empty;

            if (EqualsValue != null)
            {
                return string.Equals(value, EqualsValue, System.StringComparison.OrdinalIgnoreCase);
            }

            if (NotEqualsValue != null)
            {
                return !string.Equals(value, NotEqualsValue, System.StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        if (All != null)
        {
            return All.All(c => c.Evaluate(values));
        }

        if (Any != null)
        {
            return Any.Any(c => c.Evaluate(values));
        }

        if (Not != null)
        {
            return !Not.Evaluate(values);
        }

        return false;
    }
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
