using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ReimaginedLauncher.Utilities;

public static partial class D2RLoaderService
{
    internal static void SetDefaultMod(string installDirectory, LaunchExperience experience)
        => UpdateLoaderConfig(installDirectory, existing => UpdateDefaultMod(existing, experience));

    /// <summary>
    /// D2RLoader's developer log details, which a d2rds reads back from the log to
    /// report its environment to the lobby (no listing, no Host, without them).
    /// The local d2rds rig turns them on; client and server share this file.
    /// </summary>
    internal static void EnableExtensionDetailLogs(string installDirectory)
        => UpdateLoaderConfig(installDirectory, UpdateExtensionDetailLogs);

    internal static string UpdateExtensionDetailLogs(string toml)
    {
        const string section = "d2rloader.developer.logs";
        return UpdateTomlSetting(UpdateTomlSetting(toml, section, "enabled", "true"), section, "extension_details", "true");
    }

    private static void UpdateLoaderConfig(string installDirectory, Func<string, string> update)
    {
        var path = Path.Combine(installDirectory, "d2rloader", "config", "d2rloader.toml");
        var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var updated = update(existing);
        if (updated == existing) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            File.WriteAllText(temporary, updated, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static string UpdateDefaultMod(string toml, LaunchExperience experience)
    {
        var updated = UpdateLoaderSetting(toml, "default_mod", $"\"{ModInstallationPaths.ModName(experience)}\"");
        return experience is LaunchExperience.Ladder or LaunchExperience.Online
            ? UpdateLoaderSetting(updated, "show_tcpip_button", "true")
            : updated;
    }

    private static string UpdateLoaderSetting(string toml, string key, string value)
        => UpdateTomlSetting(toml, "d2rloader", key, value);

    private static string UpdateTomlSetting(string toml, string sectionName, string key, string value)
    {
        var newline = toml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var assignment = $"{key} = {value}";
        var section = Regex.Match(toml, $@"(?m)^[ \t]*\[{Regex.Escape(sectionName)}\][ \t]*(?:#[^\r\n]*)?\r?$", RegexOptions.CultureInvariant);
        if (!section.Success)
            return toml + (toml.Length > 0 && !toml.EndsWith('\n') ? newline : string.Empty)
                   + $"[{sectionName}]" + newline + assignment + newline;

        var start = section.Index + section.Length - (section.Value.EndsWith('\r') ? 1 : 0);
        var nextSection = Regex.Match(toml[start..], @"(?m)^[ \t]*\[", RegexOptions.CultureInvariant);
        var end = nextSection.Success ? start + nextSection.Index : toml.Length;
        var body = toml[start..end];
        var setting = Regex.Match(body, $@"(?m)^[ \t]*{Regex.Escape(key)}[ \t]*=[^\r\n]*", RegexOptions.CultureInvariant);
        if (setting.Success)
            return toml[..(start + setting.Index)] + assignment + toml[(start + setting.Index + setting.Length)..];
        return toml[..start] + newline + assignment + toml[start..];
    }
}
