using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public sealed record LadderDisplayInfo(
    string Name,
    DateTimeOffset StartDateUtc,
    DateTimeOffset EndDateUtc)
{
    public string NameDisplayText => Regex.Replace(Name, @"\s+", " ").Trim();

    public string RuntimeDisplayText =>
        $"Runs {StartDateUtc.ToLocalTime().ToString("MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture)} - " +
        $"{EndDateUtc.ToLocalTime().ToString("MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture)} local";
}

public static partial class LadderCharacterSelectService
{
    internal const string GeneratedNameWidgetName = "ReimaginedLadderNameInfo";
    internal const string GeneratedRuntimeWidgetName = "ReimaginedLadderRuntimeInfo";
    private const string LegacyGeneratedWidgetName = "ReimaginedLadderInfo";
    private const string CharacterSelectPanelName = "CharacterSelectPanel";
    private const string CleanSuffix = "_launcher_clean";

    public static async Task<int> PrepareAsync(
        IEnumerable<string> layoutPaths,
        LadderDisplayInfo? ladder,
        CancellationToken cancellationToken = default)
    {
        var preparedCount = 0;
        foreach (var layoutPath in layoutPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(layoutPath))
            {
                LaunchDiagnostics.Log($"Character-select layout was not found and could not be prepared: {layoutPath}");
                continue;
            }

            await PrepareLayoutAsync(layoutPath, ladder, cancellationToken);
            preparedCount++;
        }

        return preparedCount;
    }

    internal static string GetCleanLayoutPath(string layoutPath)
    {
        var directory = Path.GetDirectoryName(layoutPath)
                        ?? throw new DirectoryNotFoundException("Character-select layout directory could not be resolved.");
        return Path.Combine(
            directory,
            $"{Path.GetFileNameWithoutExtension(layoutPath)}{CleanSuffix}{Path.GetExtension(layoutPath)}");
    }

    private static async Task PrepareLayoutAsync(
        string layoutPath,
        LadderDisplayInfo? ladder,
        CancellationToken cancellationToken)
    {
        var cleanLayoutPath = GetCleanLayoutPath(layoutPath);
        if (!File.Exists(cleanLayoutPath))
        {
            File.Copy(layoutPath, cleanLayoutPath, overwrite: false);
        }

        File.Copy(cleanLayoutPath, layoutPath, overwrite: true);
        if (ladder is null)
        {
            return;
        }

        var layout = await File.ReadAllTextAsync(layoutPath, cancellationToken);
        layout = RemoveGeneratedWidgets(layout);
        if (!TryFindNamedObject(layout, CharacterSelectPanelName, out var panelStart, out var panelEnd))
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(layoutPath)} does not contain the {CharacterSelectPanelName} root widget.");
        }

        var childrenMatch = ChildrenPropertyRegex().Match(layout, panelStart, panelEnd - panelStart + 1);
        if (!childrenMatch.Success)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(layoutPath)} does not contain the root character-select children collection.");
        }

        var childrenStart = layout.IndexOf('[', childrenMatch.Index + childrenMatch.Length - 1);
        var childrenEnd = FindCollectionEnd(layout, childrenStart, '[', ']');
        if (childrenStart < 0 || childrenEnd < 0 || childrenEnd > panelEnd)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(layoutPath)} has an invalid root character-select children collection.");
        }

        var closingLineStart = layout.LastIndexOf('\n', childrenEnd);
        closingLineStart = closingLineStart < 0 ? 0 : closingLineStart + 1;
        var closingIndentationLength = 0;
        while (closingLineStart + closingIndentationLength < layout.Length
               && layout[closingLineStart + closingIndentationLength] is ' ' or '\t')
        {
            closingIndentationLength++;
        }

        var closingIndentation = layout.Substring(closingLineStart, closingIndentationLength);
        var childIndentation = $"{closingIndentation}    ";
        var widgets = BuildWidgets(ladder)
            .Replace("\n", $"\n{childIndentation}", StringComparison.Ordinal);

        var lastContentIndex = childrenEnd - 1;
        while (lastContentIndex > childrenStart && char.IsWhiteSpace(layout[lastContentIndex]))
        {
            lastContentIndex--;
        }

        var separator = lastContentIndex == childrenStart || layout[lastContentIndex] == ',' ? string.Empty : ",";
        layout = layout.Insert(
            childrenEnd,
            $"{separator}{Environment.NewLine}{childIndentation}{widgets}{Environment.NewLine}{closingIndentation}");
        await File.WriteAllTextAsync(layoutPath, layout, cancellationToken);
    }

    private static string BuildWidgets(LadderDisplayInfo ladder)
    {
        var encodedName = EncodeText(ladder.NameDisplayText);
        var encodedRuntime = EncodeText(ladder.RuntimeDisplayText);
        return $$"""
                 {
                     "type": "TextBoxWidget", "name": "{{GeneratedNameWidgetName}}",
                     "fields": {
                         "rect": { "x": -2690, "y": 160, "width": 1600, "height": 80 },
                         "text": {{encodedName}},
                         "style": {
                             "fontColor": "$FontColorGold",
                             "alignment": { "h": "center", "v": "center" },
                             "pointSize": "$MediumLargeFontSize",
                             "spacing": "$ReducedSpacing",
                             "dropShadow": "$DefaultDropShadow"
                         }
                     }
                 },
                 {
                     "type": "TextBoxWidget", "name": "{{GeneratedRuntimeWidgetName}}",
                     "fields": {
                         "rect": { "x": -2690, "y": 240, "width": 1600, "height": 60 },
                         "text": {{encodedRuntime}},
                         "style": {
                             "fontColor": "$FontColorGold",
                             "alignment": { "h": "center", "v": "center" },
                             "pointSize": "$XMediumFontSize",
                             "spacing": "$ReducedSpacing",
                             "dropShadow": "$DefaultDropShadow"
                         }
                     }
                 }
                 """;
    }

    private static string EncodeText(string text)
    {
        return JsonSerializer.Serialize(text, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    private static string RemoveGeneratedWidgets(string layout)
    {
        foreach (var widgetName in new[]
                 {
                     LegacyGeneratedWidgetName,
                     GeneratedNameWidgetName,
                     GeneratedRuntimeWidgetName
                 })
        {
            while (TryFindNamedObject(layout, widgetName, out var start, out var end))
            {
                var removeStart = start;
                var removeEnd = end + 1;

                var cursor = removeEnd;
                while (cursor < layout.Length && char.IsWhiteSpace(layout[cursor])) cursor++;
                if (cursor < layout.Length && layout[cursor] == ',')
                {
                    removeEnd = cursor + 1;
                }
                else
                {
                    cursor = removeStart - 1;
                    while (cursor >= 0 && char.IsWhiteSpace(layout[cursor])) cursor--;
                    if (cursor >= 0 && layout[cursor] == ',') removeStart = cursor;
                }

                layout = layout.Remove(removeStart, removeEnd - removeStart);
            }
        }

        return layout;
    }

    private static bool TryFindNamedObject(string layout, string name, out int objectStart, out int objectEnd)
    {
        var match = NamePropertyRegex().Match(layout);
        while (match.Success)
        {
            if (string.Equals(match.Groups["name"].Value, name, StringComparison.Ordinal))
            {
                objectStart = layout.LastIndexOf('{', match.Index);
                if (objectStart >= 0)
                {
                    objectEnd = FindObjectEnd(layout, objectStart);
                    if (objectEnd >= 0) return true;
                }
            }

            match = match.NextMatch();
        }

        objectStart = -1;
        objectEnd = -1;
        return false;
    }

    private static int FindObjectEnd(string source, int objectStart)
    {
        return FindCollectionEnd(source, objectStart, '{', '}');
    }

    private static int FindCollectionEnd(string source, int collectionStart, char open, char close)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = collectionStart; index < source.Length; index++)
        {
            var character = source[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character == open)
            {
                depth++;
            }
            else if (character == close && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    [GeneratedRegex("\\\"name\\\"\\s*:\\s*\\\"(?<name>[^\\\"]+)\\\"", RegexOptions.CultureInvariant)]
    private static partial Regex NamePropertyRegex();

    [GeneratedRegex("\\\"children\\\"\\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex ChildrenPropertyRegex();
}
