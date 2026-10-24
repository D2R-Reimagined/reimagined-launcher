using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System;
using System.IO;
using System.Linq;

namespace ReimaginedLauncher.Utilities;

/// <summary>UnHoarder v3, matching the website validator and distributed 1.1.0 limits.</summary>
internal static class LootFilterValidation
{
    public const int MaxBytes = 4 * 1024 * 1024;
    private static readonly string[] Rarities = ["inferior", "normal", "superior", "magic", "set", "rare", "unique"];
    private static readonly Regex Rgba = new(@"^RGBA\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d*\.?\d+)\s*\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    public static string Validate(JsonElement document)
    {
        Object(document, "Filter", ["version", "rules"]);
        Require(document.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var v) && v == 3, "Filter version must be 3.");
        Require(document.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array && rules.GetArrayLength() <= 4096, "Filter requires at most 4,096 rules.");
        var index = 0;
        foreach (var rule in rules.EnumerateArray())
        {
            var path = $"Rule {++index}";
            Object(rule, path, ["show", "hide"]);
            Require(rule.EnumerateObject().Count() == 1, $"{path} needs exactly one show or hide wrapper.");
            var block = rule.EnumerateObject().First().Value;
            Object(block, path, ["ruleName", "conditions", "continue", "name", "tooltip", "dropSound", "minimapIcon"]);
            foreach (var property in block.EnumerateObject())
            {
                var value = property.Value;
                var field = $"{path}.{property.Name}";
                switch (property.Name)
                {
                    case "ruleName": Text(value, field); break;
                    case "continue": Boolean(value, field); break;
                    case "conditions": Conditions(value, field); break;
                    case "name":
                        var name = Text(value, field); var lines = name.Split('\n');
                        Require(name.Length is >= 1 and <= 79 && name.All(c => c is >= ' ' and <= '~' or '\n') && lines.Length <= 3 && lines.All(line => line.Length is >= 1 and <= 55), $"{field} requires 1–79 ASCII characters, at most 3 non-empty lines of at most 55 characters.");
                        break;
                    case "tooltip":
                        Object(value, field, ["backgroundColor", "textColor"], true);
                        foreach (var color in value.EnumerateObject()) Color(color.Value, field);
                        break;
                    case "dropSound":
                        Require(Enumerable.Range(1, 16).Select(n => $"Filter{n:00}").Contains(Text(value, field)), $"{field} must be Filter01–Filter16.");
                        break;
                    case "minimapIcon":
                        Object(value, field, ["shape", "borderColor", "fillColor", "size"]);
                        Require(value.TryGetProperty("shape", out var shape) && new[] { "circle", "diamond", "triangle", "star" }.Contains(Text(shape, field)), $"{field} requires a supported shape.");
                        Require(value.TryGetProperty("borderColor", out var border) && value.TryGetProperty("fillColor", out _), $"{field} requires border and fill colors.");
                        Color(border, field); Color(value.GetProperty("fillColor"), field);
                        if (value.TryGetProperty("size", out var size)) Require(size.ValueKind == JsonValueKind.Number && size.TryGetInt32(out _), $"{field}.size must be an integer.");
                        break;
                }
            }
        }
        // Match the downloadable format's two-space indentation and Unicode byte size.
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Require(Encoding.UTF8.GetByteCount(json) <= MaxBytes, "Filter exceeds the 4 MiB limit.");
        return json;
    }
    private static void Conditions(JsonElement value, string path)
    {
        Object(value, path, ["code", "baseName", "itemType", "rarity", "quantity", "itemLevel", "sockets", "ethereal", "identified"], true);
        foreach (var property in value.EnumerateObject())
        {
            var field = $"{path}.{property.Name}"; var item = property.Value;
            if (property.Name is "ethereal" or "identified") { Boolean(item, field); continue; }
            if (property.Name is "quantity" or "itemLevel" or "sockets")
            {
                Object(item, field, ["eq", "gt", "gte", "lt", "lte"], true);
                Require(!(item.TryGetProperty("gt", out _) && item.TryGetProperty("gte", out _)) && !(item.TryGetProperty("lt", out _) && item.TryGetProperty("lte", out _)), $"{field} can have only one minimum and one maximum comparator.");
                var min = property.Name == "itemLevel" ? 1 : 0;
                var max = property.Name == "quantity" ? 65535 : property.Name == "sockets" ? 15 : 99;
                foreach (var bound in item.EnumerateObject()) Require(bound.Value.ValueKind == JsonValueKind.Number && bound.Value.TryGetInt32(out var n) && n >= min && n <= max, $"{field} requires integer bounds {min}–{max}.");
                continue;
            }
            var values = item.ValueKind == JsonValueKind.Array ? item.EnumerateArray().ToArray() : [item];
            Require(values.Length >= 1 && values.Length <= (property.Name == "rarity" ? 7 : 64), $"{field} has too many or no values.");
            foreach (var entry in values)
            {
                var text = Text(entry, field);
                Require(text.Length > 0, $"{field} cannot be empty.");
                if (property.Name == "rarity") Require(Rarities.Contains(text), $"{field} contains an unsupported rarity.");
                else if (property.Name == "code") Require(text.Length <= 4 && text.Any(c => c != ' ') && text.All(c => c is >= ' ' and <= '~'), $"{field} requires 1–4 printable ASCII characters.");
                else Require(text.Length <= 127 && !text.Any(c => c is '\t' or '\r' or '\n'), $"{field} requires at most 127 characters with no tabs or newlines.");
            }
        }
    }
    private static void Object(JsonElement value, string path, string[] keys, bool nonempty = false)
    {
        Require(value.ValueKind == JsonValueKind.Object, $"{path} must be an object.");
        var properties = value.EnumerateObject().ToArray();
        Require((!nonempty || properties.Length > 0) && properties.All(p => keys.Contains(p.Name)) && properties.Select(p => p.Name).Distinct().Count() == properties.Length, $"{path} has missing, duplicate or unsupported fields.");
    }
    private static string Text(JsonElement value, string path) { Require(value.ValueKind == JsonValueKind.String, $"{path} must be text."); return value.GetString()!; }
    private static void Boolean(JsonElement value, string path) => Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, $"{path} must be true or false.");
    private static void Color(JsonElement value, string path)
    {
        var match = Rgba.Match(Text(value, path));
        Require(match.Success && Enumerable.Range(1, 3).All(i => int.TryParse(match.Groups[i].Value, out var n) && n is >= 0 and <= 255)
            && double.TryParse(match.Groups[4].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var alpha) && alpha is >= 0 and <= 1, $"{path} requires RGBA with RGB 0–255 and opacity 0–1.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
