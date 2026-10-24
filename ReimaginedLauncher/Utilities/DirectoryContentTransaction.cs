using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ReimaginedLauncher.Utilities;

internal sealed class DirectoryContentTransaction(string source, string target, string backup, CancellationToken cancellationToken)
{
    private sealed record Change(string Relative, bool Existed, string? InstalledHash);
    private readonly List<Change> changes = [];
    private readonly StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal void Apply()
    {
        var old = Snapshot(target);
        var incoming = Snapshot(source);
        // Reject file/directory collisions before backing up or changing files.
        foreach (var file in incoming.Keys)
        {
            if (Directory.Exists(PathAt(target, file))) throw new IOException("A directory occupies a package file: " + file);
            for (var parent = Path.GetDirectoryName(file); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                if (File.Exists(PathAt(target, parent))) throw new IOException("A file occupies a package directory: " + parent);
        }
        Directory.CreateDirectory(backup);
        foreach (var (relative, hash) in old)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = PathAt(target, relative);
            var copy = PathAt(backup, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(original, copy, false);
            if (LocalPluginInstallService.Hash(copy) != hash || LocalPluginInstallService.Hash(original) != hash)
                throw new IOException("A file changed while backing up the open mod folder: " + relative);
        }
        foreach (var (relative, newHash) in incoming)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existed = old.TryGetValue(relative, out var previousHash);
            var destination = PathAt(target, relative);
            AssertUnchanged(destination, previousHash);
            if (existed && newHash == previousHash) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(PathAt(source, relative), temp, false);
                if (LocalPluginInstallService.Hash(temp) != newHash) throw new IOException("A staged package file changed: " + relative);
                AssertUnchanged(destination, previousHash);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temp, destination, existed);
                changes.Add(new Change(relative, existed, newHash));
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        foreach (var (relative, oldHash) in old.Where(file => !incoming.ContainsKey(file.Key)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = PathAt(target, relative);
            AssertUnchanged(destination, oldHash);
            File.Delete(destination);
            changes.Add(new Change(relative, true, null));
        }
        // Keep the directory skeleton intact so open Explorer folder handles remain valid.
    }

    internal void Rollback()
    {
        var failures = new List<Exception>();
        foreach (var change in changes.AsEnumerable().Reverse())
        {
            try
            {
                var destination = PathAt(target, change.Relative);
                AssertUnchanged(destination, change.InstalledHash);
                if (change.Existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var restore = destination + "." + Guid.NewGuid().ToString("N") + ".restore";
                    try { File.Copy(PathAt(backup, change.Relative), restore); File.Move(restore, destination, change.InstalledHash is not null); }
                    finally { if (File.Exists(restore)) File.Delete(restore); }
                }
                else File.Delete(destination);
            }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (failures.Count != 0) throw new AggregateException("Some files require recovery from " + backup, failures);
    }

    private Dictionary<string, string> Snapshot(string root)
    {
        LocalPluginInstallService.EnsureNoLinks(root);
        var result = new Dictionary<string, string>(comparer);
        void Visit(string folder)
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(folder).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                LocalPluginInstallService.EnsureNoLinks(item);
                if (Directory.Exists(item)) Visit(item);
                else result.Add(Path.GetRelativePath(root, item), LocalPluginInstallService.Hash(item));
            }
        }
        Visit(root);
        return result;
    }
    private static void AssertUnchanged(string file, string? expected)
    {
        if ((File.Exists(file) ? LocalPluginInstallService.Hash(file) : null) != expected)
            throw new IOException("A package destination changed during installation: " + file);
    }
    private static string PathAt(string root, string relative)
        => LocalPluginInstallService.SafePath(root, relative.Replace(Path.DirectorySeparatorChar, '/'));
}
