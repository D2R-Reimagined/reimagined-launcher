using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ReimaginedLauncher.Generators;

namespace ReimaginedLauncher.Utilities.Json;

public class WidgetNode
{
    [JsonPropertyName("type")] public string Type { get; set; }

    [JsonPropertyName("name")] public string Name { get; set; }

    [JsonPropertyName("fields")] public Dictionary<string, JsonElement> Fields { get; set; }

    [JsonPropertyName("children")] public List<WidgetNode> Children { get; set; }
}

public class CharacterSelectPanelService
{
    [JsonPropertyName("type")] public string Type { get; set; }

    [JsonPropertyName("name")] public string Name { get; set; }

    [JsonPropertyName("fields")] public Dictionary<string, JsonElement> Fields { get; set; }

    [JsonPropertyName("children")] public List<WidgetNode> Children { get; set; }

    public static CharacterSelectPanelService? FromJson(string layoutsDirectory)
    {
        Console.WriteLine($"Loading JSON from: {layoutsDirectory}");
        var path = Path.Combine(layoutsDirectory, "characterselectpanelhd.json");
        if (!File.Exists(path))
        {
            Console.WriteLine("File not found!");
            return null;
        }
        
        var json = File.ReadAllText(path);
        
        return JsonSerializer.Deserialize<CharacterSelectPanelService>(json,
            SerializerOptions.PropertyNameCaseInsensitive);
    }

    public string GetModVersion()
    {
        var version = SearchVersionInChildren(Children);
        return version ?? "Unknown";
    }

    private string? SearchVersionInChildren(List<WidgetNode>? children)
    {
        if (children == null) return null;

        foreach (var child in children)
        {
            if (child.Fields != null)
            {
                JsonElement elem;
                // try all the likely names
                if ( child.Fields.TryGetValue("text", out elem)
                     || child.Fields.TryGetValue("textString", out elem)
                     || child.Fields.TryGetValue("Text", out elem)      // just in case
                     || child.Fields.TryGetValue("TextString", out elem))
                {
                    if (elem.ValueKind == JsonValueKind.String)
                    {
                        var txt = elem.GetString()!;
                        var m = Regex.Match(txt, @"D2R\s+Reimagined\s+v\s*([\d.]+)");
                        if (m.Success)
                            return m.Groups[1].Value;
                    }
                }
            }

            // recurse
            var found = SearchVersionInChildren(child.Children);
            if (found != null)
                return found;
        }

        return null;
    }

}