using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities.Json;

public static partial class TooltipStyleJsonService
{
    private const string TooltipStyleKey = "\"TooltipStyle\"";

    [GeneratedRegex("(\"backgroundColor\"\\s*:\\s*)\\[[^\\]]*\\]")]
    private static partial Regex BackgroundColorRegex();

    [GeneratedRegex("(\"inGameBackgroundColor\"\\s*:\\s*)\\[[^\\]]*\\]")]
    private static partial Regex InGameBackgroundColorRegex();

    public static async Task<int> MakeTooltipBackgroundOpaqueAsync(string layoutsProfileHdFilePath)
    {
        var json = await File.ReadAllTextAsync(layoutsProfileHdFilePath);
        var tooltipStyleRange = FindObjectRange(json, TooltipStyleKey);
        if (tooltipStyleRange is null)
        {
            return 0;
        }

        var replacements = 0;
        var tooltipStyle = json.Substring(tooltipStyleRange.Value.Start, tooltipStyleRange.Value.Length);
        var updatedTooltipStyle = BackgroundColorRegex().Replace(tooltipStyle, match =>
        {
            replacements++;
            return $"{match.Groups[1].Value}[ 0, 0, 0, 1 ]";
        }, 1);

        updatedTooltipStyle = InGameBackgroundColorRegex().Replace(updatedTooltipStyle, match =>
        {
            replacements++;
            return $"{match.Groups[1].Value}[ 0, 0, 0, 1 ]";
        }, 1);

        if (replacements == 0)
        {
            return 0;
        }

        var updatedJson = string.Concat(
            json[..tooltipStyleRange.Value.Start],
            updatedTooltipStyle,
            json[(tooltipStyleRange.Value.Start + tooltipStyleRange.Value.Length)..]);

        await File.WriteAllTextAsync(layoutsProfileHdFilePath, updatedJson);
        return replacements;
    }

    private static (int Start, int Length)? FindObjectRange(string json, string propertyName)
    {
        var propertyIndex = json.IndexOf(propertyName, System.StringComparison.Ordinal);
        if (propertyIndex < 0)
        {
            return null;
        }

        var objectStart = json.IndexOf('{', propertyIndex);
        if (objectStart < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var i = objectStart; i < json.Length; i++)
        {
            var current = json[i];
            var next = i + 1 < json.Length ? json[i + 1] : '\0';

            if (inLineComment)
            {
                if (current == '\n')
                {
                    inLineComment = false;
                }

                continue;
            }

            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (inString)
            {
                if (current == '\\')
                {
                    i++;
                    continue;
                }

                if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '/' && next == '/')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (current == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            if (current == '{')
            {
                depth++;
                continue;
            }

            if (current != '}')
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return (objectStart, i - objectStart + 1);
            }
        }

        return null;
    }
}
