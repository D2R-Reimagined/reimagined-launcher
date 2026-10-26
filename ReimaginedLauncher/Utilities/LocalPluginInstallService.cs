using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace ReimaginedLauncher.Utilities;

internal sealed record LocalInstallFile(string RelativePath, byte[] Contents, string? PreviousHash)
{
    public bool IsConfig => RelativePath.StartsWith("config/", StringComparison.Ordinal);
    public bool Exists => PreviousHash is not null;
}
internal sealed record LocalPluginPlan(string Root, IReadOnlyList<LocalInstallFile> Files, IReadOnlyList<string> Skipped);
internal sealed record D2RLoaderModDataAsset(string PluginId, string PluginName, string SourceAbsolutePath, string TargetRelativePath);

internal static class LocalPluginInstallService
{
    internal static readonly SemaphoreSlim InstallGate = new(1, 1);
    private const long MaximumPackageBytes = 256L * 1024 * 1024;

    internal static string ResolveRoot(InstallationProfile profile)
    {
        if (profile.Type == InstallationType.D2RMM || string.IsNullOrWhiteSpace(profile.InstallDirectory)
            || !File.Exists(Path.Combine(profile.InstallDirectory, "D2R.exe")))
            throw new InvalidOperationException("Select a valid Diablo II: Resurrected installation first.");
        var root = Path.GetFullPath(Path.Combine(CanonicalInstallDirectory(profile.InstallDirectory), "mods", ModInstallationPaths.ModName(profile.LaunchExperience), "d2rloader"));
        if (!Directory.Exists(Path.GetDirectoryName(root)))
            throw new DirectoryNotFoundException("Install the selected mod before installing a plugin or filter.");
        EnsureNoLinks(root);
        return root;
    }

    internal static LocalPluginPlan Preview(string file, InstallationProfile profile)
    {
        RefuseLadder(profile.LaunchExperience);
        var root = ResolveRoot(profile);
        var modMpq = ModMpqName(root);
        var files = new List<LocalInstallFile>();
        var skipped = new List<string>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        void Add(string name, byte[] bytes, string? pluginStem)
        {
            var path = MapPath(name, modMpq, pluginStem);
            if (path is null) { skipped.Add(name); return; }
            if (path.StartsWith("plugins/", StringComparison.Ordinal) && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var parkedFolder = SafePath(root, "launcher-disabled/plugins");
                var parked = Directory.Exists(parkedFolder) ? Directory.GetFiles(parkedFolder).Where(file =>
                    Path.GetFileName(file).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)).ToArray() : [];
                if (parked.Length > 1 || parked.Length == 1 && File.Exists(SafePath(root, path)))
                    throw new IOException("Active and disabled plugin copies conflict. Resolve them before reinstalling: " + name);
                if (parked.Length == 1) path = "launcher-disabled/plugins/" + Path.GetFileName(parked[0]);
            }
            if (!destinations.Add(path)) throw new InvalidDataException($"Multiple package files target {path}.");
            var target = SafePath(root, path);
            if (Directory.Exists(target)) throw new InvalidDataException($"A folder occupies {path}.");
            files.Add(new LocalInstallFile(path, bytes, File.Exists(target) ? Hash(target) : null));
        }
        if (Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            if (new FileInfo(file).Length > MaximumPackageBytes) throw new InvalidDataException("The plugin is too large.");
            Add(Path.GetFileName(file), File.ReadAllBytes(file), Path.GetFileNameWithoutExtension(file));
        }
        else if (Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(file);
            if (archive.Entries.Count > 2048) throw new InvalidDataException("The package contains too many files.");
            var primaryDllStem = archive.Entries
                .Where(entry => !string.IsNullOrEmpty(entry.Name) && Path.GetExtension(entry.Name).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                .Select(entry => Path.GetFileNameWithoutExtension(entry.Name))
                .FirstOrDefault(stem => !string.IsNullOrWhiteSpace(stem));
            foreach (var entry in archive.Entries)
            {
                ValidateRelative(entry.FullName.TrimEnd('/', '\\'));
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                    || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Links are not allowed in plugin packages.");
                if (string.IsNullOrEmpty(entry.Name)) continue;
                total = checked(total + entry.Length);
                if (total > MaximumPackageBytes) throw new InvalidDataException("The expanded package is too large.");
                using var input = entry.Open();
                using var output = new MemoryStream();
                var buffer = new byte[81920]; int count;
                while ((count = input.Read(buffer)) != 0)
                {
                    if (output.Length + count > entry.Length) throw new InvalidDataException("Invalid archive entry size.");
                    output.Write(buffer, 0, count);
                }
                Add(entry.FullName, output.ToArray(), primaryDllStem);
            }
        }
        else throw new InvalidDataException("Choose a DLL or ZIP file.");
        if (!files.Any(item => item.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The package contains no plugin DLLs.");
        if (files.Any(item => item.RelativePath.StartsWith("plugin-assets/", StringComparison.Ordinal)))
        {
            var modDir = Path.GetDirectoryName(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar))!;
            if (!Directory.Exists(Path.Combine(modDir, modMpq, "data", "global", "excel")))
                throw new DirectoryNotFoundException("Install the selected mod before installing a plugin with mod data files.");
        }
        return new LocalPluginPlan(root, files, skipped);
    }

    private static string? MapPath(string name, string? modMpq = null, string? pluginStem = null)
    {
        name = name.Replace('\\', '/');
        ValidateRelative(name);
        var parts = name.Split('/');
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is ".dll" or ".mpq") return "plugins/" + parts[^1];
        var configIndex = Array.FindIndex(parts, part => part.Equals("config", StringComparison.OrdinalIgnoreCase));
        if (configIndex >= 0 && configIndex < parts.Length - 1)
            return "config/" + string.Join('/', parts.Skip(configIndex + 1));
        var dataIndex = Array.FindIndex(parts, part => part.Equals("data", StringComparison.OrdinalIgnoreCase));
        if (dataIndex >= 0 && dataIndex < parts.Length - 1)
        {
            if (parts.Skip(dataIndex + 1).Any(IsLauncherCleanSegment))
                return null;
            if (dataIndex > 0 && parts[dataIndex - 1].EndsWith(".mpq", StringComparison.OrdinalIgnoreCase))
            {
                if (modMpq is not null && parts[dataIndex - 1].Equals(modMpq, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pluginStem))
                    return "plugin-assets/" + pluginStem + "/data/" + string.Join('/', parts.Skip(dataIndex + 1));
            }
            else if (!parts.Take(dataIndex).Any(part => part.EndsWith(".mpq", StringComparison.OrdinalIgnoreCase)))
                return "data/" + string.Join('/', parts.Skip(dataIndex + 1));
        }
        if (ext is ".toml" or ".ini" or ".cfg") return "config/" + parts[^1];
        if (ext == ".json") throw new InvalidDataException($"Cannot determine whether {name} is a config or patch. Package configurations beneath config/; JSON patches are not supported by this installer.");
        return null;
    }

    private static bool IsLauncherCleanSegment(string part)
    {
        var stem = Path.GetFileNameWithoutExtension(part);
        return part.EndsWith("_launcher_clean", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("_launcher_clean", StringComparison.OrdinalIgnoreCase);
    }

    private static string ModMpqName(string root)
    {
        var mod = Path.GetDirectoryName(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(mod) ? "Reimagined.mpq" : Path.GetFileName(mod) + ".mpq";
    }

    internal static IReadOnlyList<D2RLoaderModDataAsset> GetEnabledModDataAssets(string modRoot)
    {
        if (string.IsNullOrWhiteSpace(modRoot)) return [];
        var profile = MainWindow.Settings?.CurrentProfile;
        if (profile is not null && profile.LaunchExperience != LaunchExperience.Online) return [];

        var modDir = Path.GetDirectoryName(Path.GetFullPath(modRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(modDir)) return [];

        var loaderRoot = Path.Combine(modDir, "d2rloader");
        var assetsRoot = Path.Combine(loaderRoot, "plugin-assets");
        var pluginsDir = Path.Combine(loaderRoot, "plugins");
        if (!Directory.Exists(assetsRoot) || !Directory.Exists(pluginsDir)) return [];

        var enabledStems = new HashSet<string>(
            Directory.EnumerateFiles(pluginsDir, "*.dll")
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .Where(stem => !string.IsNullOrWhiteSpace(stem))!,
            StringComparer.OrdinalIgnoreCase);
        if (enabledStems.Count == 0) return [];

        var assets = new List<D2RLoaderModDataAsset>();
        foreach (var pluginDir in Directory.GetDirectories(assetsRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var stem = Path.GetFileName(pluginDir);
            if (string.IsNullOrWhiteSpace(stem) || !enabledStems.Contains(stem)) continue;

            var dataDir = Path.Combine(pluginDir, "data");
            if (!Directory.Exists(dataDir)) continue;

            foreach (var file in Directory.GetFiles(dataDir, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var rel = Path.GetRelativePath(dataDir, file).Replace('\\', '/');
                assets.Add(new D2RLoaderModDataAsset(
                    "d2rloader:" + stem.ToLowerInvariant(),
                    stem + ".dll",
                    file,
                    "data/" + rel));
            }
        }

        return assets;
    }

    internal static void RefuseLadder(LaunchExperience experience)
    {
        if (experience == LaunchExperience.Ladder)
            throw new InvalidOperationException("Manual plugin installation is unavailable for Ladder. Its package contents are managed by the launcher.");
    }

    internal static string Install(LocalPluginPlan plan, bool replaceConfigs, Action checkContext)
    {
        checkContext();
        var selected = plan.Files.Where(file => replaceConfigs || !file.IsConfig || !File.Exists(SafePath(plan.Root, file.RelativePath))).ToArray();
        return Commit(plan.Root, selected, checkContext);
    }

    internal static string Commit(string root, IReadOnlyList<LocalInstallFile> files, Action checkContext, string backupFolder = ".launcher-backups")
    {
        var backup = Path.Combine(BackupDirectory(root, backupFolder), DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N"));
        var staged = new List<(LocalInstallFile File, string Target, string Temp)>();
        var committed = new List<(LocalInstallFile File, string Target)>();
        try
        {
            foreach (var file in files)
            {
                checkContext();
                var target = SafePath(root, file.RelativePath);
                var current = File.Exists(target) ? Hash(target) : null;
                if (current != file.PreviousHash) throw new IOException($"{file.RelativePath} changed since preview. Open the package again.");
                if (Directory.Exists(target)) throw new IOException($"A folder occupies {file.RelativePath}.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                staged.Add((file, target, temp));
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(file.Contents); stream.Flush(true);
                }
                if (file.Exists)
                {
                    var copy = SafePath(backup, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    File.Copy(target, copy, false);
                    if (Hash(copy) != file.PreviousHash) throw new IOException("A file changed while being backed up. Nothing was replaced.");
                }
            }
            foreach (var item in staged)
            {
                checkContext();
                SafePath(root, item.File.RelativePath);
                if ((File.Exists(item.Target) ? Hash(item.Target) : null) != item.File.PreviousHash)
                    throw new IOException("A destination changed during installation.");
                File.Move(item.Temp, item.Target, item.File.Exists);
                committed.Add((item.File, item.Target));
            }
            var targetSet = new HashSet<string>(files.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase);
            var assetPrefixes = files.Select(f => f.RelativePath)
                .Where(p => p.StartsWith("plugin-assets/", StringComparison.OrdinalIgnoreCase))
                .Select(p => string.Join('/', p.Split('/').Take(2)))
                .Concat(files.Select(f => f.RelativePath)
                    .Where(p => (p.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)
                                 || p.StartsWith("launcher-disabled/plugins/", StringComparison.OrdinalIgnoreCase))
                                && p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    .Select(p => "plugin-assets/" + Path.GetFileNameWithoutExtension(p)))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var assetPrefix in assetPrefixes)
            {
                var assetDir = SafePath(root, assetPrefix);
                if (!Directory.Exists(assetDir)) continue;
                foreach (var existing in Directory.GetFiles(assetDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(root, existing).Replace('\\', '/');
                    if (targetSet.Contains(rel)) continue;
                    var copy = SafePath(backup, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    File.Move(existing, copy, true);
                }
            }
            return backup;
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            foreach (var item in committed.AsEnumerable().Reverse())
            {
                try
                {
                    SafePath(root, item.File.RelativePath);
                    if (!File.Exists(item.Target) || Hash(item.Target) != Convert.ToHexString(SHA256.HashData(item.File.Contents)))
                        throw new IOException($"{item.File.RelativePath} changed after installation; restore its backup manually.");
                    if (item.File.Exists)
                    {
                        var restore = item.Target + "." + Guid.NewGuid().ToString("N") + ".restore";
                        try { File.Copy(SafePath(backup, item.File.RelativePath), restore); File.Move(restore, item.Target, true); }
                        finally { if (File.Exists(restore)) File.Delete(restore); }
                    }
                    else File.Delete(item.Target);
                }
                catch (Exception rollback) { failures.Add(rollback); }
            }
            if (failures.Count > 1) throw new AggregateException($"Installation failed; some files require recovery from {backup}.", failures);
            throw;
        }
        finally
        {
            foreach (var item in staged) { if (File.Exists(item.Temp)) File.Delete(item.Temp); }
        }
    }

    internal static string BackupDirectory(string root, string category)
    {
        var mod = Path.GetDirectoryName(root)!;
        var mods = Path.GetDirectoryName(mod)!;
        if (!Path.GetFileName(root).Equals("d2rloader", StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(mods).Equals("mods", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The target is not a mod-scoped D2RLoader folder.");
        var game = Path.GetDirectoryName(mods)!;
        return SafePath(game, ".reimagined-launcher/" + category.TrimStart('.') + "/" + Path.GetFileName(mod));
    }

    internal static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    internal static string SafePath(string root, string relative)
    {
        ValidateRelative(relative);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("A package path escapes its destination.");
        EnsureNoLinks(path);
        return path;
    }
    private static void ValidateRelative(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') || name.StartsWith('\\')) throw new InvalidDataException("Invalid package path.");
        foreach (var part in name.Replace('\\', '/').Split('/'))
        {
            if (part is "" or "." or ".." || part.IndexOfAny([':', '\0', '<', '>', '"', '|', '?', '*']) >= 0
                || part.EndsWith('.') || part.EndsWith(' ')) throw new InvalidDataException("Unsafe package path: " + name);
            var stem = part.Split('.')[0];
            if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Reserved package filename: " + name);
        }
    }
    private static string CanonicalInstallDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var directory = new DirectoryInfo(current);
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                current = directory.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Could not resolve the selected installation directory.");
        }
        return current;
    }

    internal static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Installation through symbolic links or junctions is not supported: " + current);
        }
    }
}
