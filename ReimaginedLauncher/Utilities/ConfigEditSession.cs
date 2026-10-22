using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ReimaginedLauncher.Utilities;

internal sealed class ConfigEditSession
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private byte[] _original;
    private readonly Encoding _encoding;
    private readonly byte[] _preamble;
    internal string Path { get; }
    internal string Text { get; private set; }

    internal ConfigEditSession(string path)
    {
        Path = path;
        _original = ReadBounded(path);
        (_encoding, _preamble) = _original.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })
            ? ((Encoding)new UTF8Encoding(false, true), new byte[] { 0xEF, 0xBB, 0xBF })
            : _original.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })
                ? ((Encoding)new UnicodeEncoding(false, false, true), new byte[] { 0xFF, 0xFE })
                : _original.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })
                    ? ((Encoding)new UnicodeEncoding(true, false, true), new byte[] { 0xFE, 0xFF })
                    : ((Encoding)new UTF8Encoding(false, true), Array.Empty<byte>());
        Text = _encoding.GetString(_original, _preamble.Length, _original.Length - _preamble.Length);
        if (Text.Contains('\0')) throw new InvalidDataException("This file is not a supported text configuration.");
    }

    internal string? Save(string text, Action validate)
    {
        validate();
        if (!ReadBounded(Path).AsSpan().SequenceEqual(_original))
            throw new IOException("This config changed outside the editor. Cancel and reopen it before saving.");
        if (text == Text) return null;
        if (System.IO.Path.GetExtension(Path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        var body = _encoding.GetBytes(text);
        if (body.Length + _preamble.Length > MaximumBytes) throw new IOException("Config files must be smaller than 2 MB.");
        var bytes = _preamble.Concat(body).ToArray();
        var root = System.IO.Path.GetDirectoryName(Path)!;
        var backupRoot = LocalPluginInstallService.SafePath(root, ".launcher-config-backups");
        Directory.CreateDirectory(backupRoot);
        var backupName = System.IO.Path.GetFileName(Path) + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff") + "." + Guid.NewGuid().ToString("N") + ".bak";
        var backup = LocalPluginInstallService.SafePath(backupRoot, backupName);
        var temp = LocalPluginInstallService.SafePath(root, ".config-edit-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            validate();
            if (!ReadBounded(Path).AsSpan().SequenceEqual(_original))
                throw new IOException("This config changed outside the editor. Cancel and reopen it before saving.");
            File.WriteAllBytes(backup, _original);
            File.Move(temp, Path, overwrite: true);
            _original = bytes;
            Text = text;
            foreach (var old in Directory.EnumerateFiles(backupRoot)
                         .Where(p => System.IO.Path.GetFileName(p).StartsWith(System.IO.Path.GetFileName(Path) + ".", StringComparison.Ordinal)
                             && p.EndsWith(".bak", StringComparison.Ordinal))
                         .OrderByDescending(p => p, StringComparer.Ordinal).Skip(3))
            {
                try { File.Delete(old); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return backup;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) throw new IOException("This config is too large for the built-in editor (2 MB maximum).");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
