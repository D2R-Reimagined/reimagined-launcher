using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ReimaginedLauncher.Utilities;

public static partial class D2RLoaderService
{
    internal static void SetDefaultMod(string installDirectory, LaunchExperience experience)
    {
        var path = Path.Combine(installDirectory, "d2rloader", "config", "d2rloader.toml");
        var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var updated = UpdateDefaultMod(existing, experience);
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
        var newline = toml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var assignment = $"default_mod = \"{ModInstallationPaths.ModName(experience)}\"";
        var section = Regex.Match(toml, @"(?m)^[ \t]*\[d2rloader\][ \t]*(?:#[^\r\n]*)?\r?$", RegexOptions.CultureInvariant);
        if (!section.Success)
            return toml + (toml.Length > 0 && !toml.EndsWith('\n') ? newline : string.Empty)
                   + "[d2rloader]" + newline + assignment + newline;

        var start = section.Index + section.Length;
        var nextSection = Regex.Match(toml[start..], @"(?m)^[ \t]*\[", RegexOptions.CultureInvariant);
        var end = nextSection.Success ? start + nextSection.Index : toml.Length;
        var body = toml[start..end];
        var setting = Regex.Match(body, @"(?m)^[ \t]*default_mod[ \t]*=[^\r\n]*", RegexOptions.CultureInvariant);
        if (setting.Success)
            return toml[..(start + setting.Index)] + assignment + toml[(start + setting.Index + setting.Length)..];
        return toml[..start] + newline + assignment + toml[start..];
    }
}
