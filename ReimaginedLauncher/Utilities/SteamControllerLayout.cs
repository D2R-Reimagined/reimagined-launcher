using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReimaginedLauncher.Utilities;

internal static class SteamControllerLayout
{
    private sealed record Token(string Value, int Start, int End, bool Quoted);
    private sealed record Entry(string Name, int ValueStart, int End, bool Object);

    internal static int SetUpLauncher(string steamRoot, string userId, string oldName, string newName, bool resetMouseOnly = false)
        => MigrateLauncherName(Path.Combine(steamRoot, "steamapps", "common", "Steam Controller Configs", userId, "config"),
            oldName, newName, File.Exists(Path.Combine(steamRoot, "controller_base", "templates", "controller_neptune_mouse.vdf")), resetMouseOnly);

    internal static int MigrateLauncherName(string configDirectory, string oldName, string newName, bool seedMouseOnly = false,
        bool resetMouseOnly = false)
    {
        if (!Directory.Exists(configDirectory))
        {
            if (!seedMouseOnly) return 0;
            Directory.CreateDirectory(configDirectory);
        }
        var migrated = 0;
        var paths = Directory.GetFiles(configDirectory, "configset_*.vdf").ToList();
        var genericDeckConfig = Path.Combine(configDirectory, "configset_controller_neptune.vdf");
        if (seedMouseOnly && !paths.Contains(genericDeckConfig)) paths.Add(genericDeckConfig);
        foreach (var path in paths)
        {
            try
            {
                var original = File.Exists(path) ? File.ReadAllBytes(path) : Encoding.UTF8.GetBytes("\"controller_config\"\n{\n}\n");
                var encoding = new UTF8Encoding(false, true);
                var text = encoding.GetString(original);
                var preferences = Path.Combine(configDirectory, Path.GetFileName(path).Replace("configset_", "preferences_"));
                var isDeckController = path == genericDeckConfig
                    || Regex.IsMatch(text, "\"template\"\\s*\"controller_neptune_[^\"]+\\.vdf\"", RegexOptions.IgnoreCase)
                    || File.Exists(preferences) && Regex.IsMatch(File.ReadAllText(preferences),
                        "\"name\"\\s*\"Steam Deck Controller\\s*\"", RegexOptions.IgnoreCase);
                var updated = CarryForward(text, oldName, newName, seedMouseOnly && isDeckController,
                    resetMouseOnly && seedMouseOnly && isDeckController);
                if (updated == text) continue;
                if (File.Exists(path)) File.Copy(path, path + ".reimagined-" + Guid.NewGuid().ToString("N") + ".bak");
                SteamGameHandoff.WriteShortcutFile(path, encoding.GetBytes(updated));
                migrated++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                Console.WriteLine("A saved controller layout could not be migrated; select Mouse Only in Steam if needed.");
            }
        }
        return migrated;
    }

    internal static string CarryForward(string text, string oldName, string newName, bool seedMouseOnly = false,
        bool resetMouseOnly = false)
    {
        var displayName = newName;
        newName = newName.Replace(":", "").ToLowerInvariant();
        var tokens = new List<Token>();
        var consumed = 0;
        foreach (Match match in Regex.Matches(text, "\"(?:\\\\.|[^\"\\\\])*\"|[{}]|//[^\\r\\n]*|\\s+|\\uFEFF"))
        {
            if (match.Index != consumed) throw new InvalidDataException("Unsupported controller configuration.");
            consumed = match.Index + match.Length;
            if (match.Value.StartsWith("//") || string.IsNullOrWhiteSpace(match.Value) || match.Value == "\uFEFF") continue;
            var quoted = match.Value.StartsWith('"');
            tokens.Add(new Token(quoted ? match.Value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\") : match.Value,
                match.Index, consumed, quoted));
        }
        if (consumed != text.Length || tokens.Count < 3 || !tokens[0].Quoted
            || !tokens[0].Value.Equals("controller_config", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported controller configuration root.");
        var position = 1;
        var entries = ReadObject(tokens, ref position, 0, out var closing);
        if (position != tokens.Count) throw new InvalidDataException("Unexpected controller configuration data.");
        const string mouseLayout = "{\n\t\t\"template\"\t\t\"controller_neptune_mouse.vdf\"\n\t}";
        var existing = entries.SingleOrDefault(entry => entry.Name.Equals(newName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (resetMouseOnly && seedMouseOnly && existing.Object)
                return text[..existing.ValueStart] + mouseLayout + text[existing.End..];
            return text;
        }
        var legacy = entries.SingleOrDefault(entry => entry.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase));
        if (legacy == null && displayName != newName)
            legacy = entries.SingleOrDefault(entry => entry.Name.Equals(displayName, StringComparison.OrdinalIgnoreCase));
        if (resetMouseOnly && seedMouseOnly) legacy = null;
        if (legacy != null && !legacy.Object || legacy == null && !seedMouseOnly) return text;
        if (newName.Contains('"') || newName.Contains('\\')) throw new ArgumentException("Invalid controller entry name.");
        var layout = legacy == null ? mouseLayout
            : text[legacy.ValueStart..legacy.End];
        var insertion = "\n\t\"" + newName.ToLowerInvariant() + "\"\n\t" + layout + "\n";
        return text.Insert(closing, insertion);
    }

    private static List<Entry> ReadObject(List<Token> tokens, ref int position, int depth, out int closing)
    {
        if (depth > 32 || position >= tokens.Count || tokens[position++].Value != "{")
            throw new InvalidDataException("Invalid controller configuration object.");
        var entries = new List<Entry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (position < tokens.Count && (tokens[position].Quoted || tokens[position].Value != "}"))
        {
            var key = tokens[position++];
            if (!key.Quoted || !names.Add(key.Value) || position >= tokens.Count)
                throw new InvalidDataException("Invalid controller configuration entry.");
            var value = tokens[position];
            var isObject = !value.Quoted && value.Value == "{";
            if (isObject) ReadObject(tokens, ref position, depth + 1, out _);
            else if (value.Quoted) position++;
            else throw new InvalidDataException("Invalid controller configuration value.");
            entries.Add(new Entry(key.Value, value.Start, tokens[position - 1].End, isObject));
        }
        if (position >= tokens.Count) throw new InvalidDataException("Truncated controller configuration.");
        closing = tokens[position++].Start;
        return entries;
    }
}
