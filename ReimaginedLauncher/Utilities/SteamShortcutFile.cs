using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ReimaginedLauncher.Utilities;

internal sealed record LauncherSteamShortcut(string Name, string Executable, string Arguments, string? LegacyName = null,
    bool? Hidden = null, IReadOnlyList<string>? LegacyArguments = null, string? LegacyExecutable = null, uint? StableAppId = null)
{
    /// <summary>Retains the registered AppImage identity when its generated bootstrap is the executable.</summary>
    public uint AppId => StableAppId ?? SteamShortcutFile.CalculateAppId(Executable, LegacyName ?? Name);
    public ulong GameId => ((ulong)AppId << 32) | 0x02000000;
}

internal static class SteamShortcutFile
{
    /// <summary>Calculates the unsigned non-Steam shortcut ID from its executable and stable name.</summary>
    internal static uint CalculateAppId(string executable, string name)
    {
        uint crc = uint.MaxValue;
        foreach (var value in Encoding.UTF8.GetBytes(executable + name))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
        }
        return ~crc | 0x80000000u;
    }

    /// <summary>Adds or migrates owned entries while retaining unrelated binary fields and rejecting ID collisions.</summary>
    internal static byte[] AppendOwnedShortcuts(byte[] original, params LauncherSteamShortcut[] shortcuts)
    {
        if (original.Length == 0) original = [0, .. Encoding.UTF8.GetBytes("shortcuts\0"), 8, 8];
        using var input = new MemoryStream(original);
        using var reader = new BinaryReader(input, Encoding.UTF8);
        if (reader.ReadByte() != 0 || ReadString(reader) != "shortcuts")
            throw new InvalidDataException("Unsupported Steam shortcut root; no changes were made.");
        var entries = new List<(Dictionary<string, object> Fields, long NameStart, long NameEnd, long HiddenStart, long HiddenEnd, long ArgumentsStart, long ArgumentsEnd, long ExeStart, long ExeEnd)>();
        var nextIndex = 0;
        while (Peek(reader) != 8)
        {
            if (reader.ReadByte() != 0) throw new InvalidDataException("Invalid Steam shortcut entry.");
            var index = ReadString(reader);
            if (!int.TryParse(index, out var number) || number < 0 || number == int.MaxValue)
                throw new InvalidDataException("Invalid Steam shortcut index.");
            nextIndex = Math.Max(nextIndex, number + 1);
            long nameStart = 0, nameEnd = 0, hiddenStart = 0, hiddenEnd = 0, argumentsStart = 0, argumentsEnd = 0, exeStart = 0, exeEnd = 0;
            var fields = ReadObject(reader, 0, (key, start, end) =>
            {
                if (key.Equals("AppName", StringComparison.OrdinalIgnoreCase))
                    (nameStart, nameEnd) = (start, end);
                if (key.Equals("IsHidden", StringComparison.OrdinalIgnoreCase))
                    (hiddenStart, hiddenEnd) = (start, end);
                if (key.Equals("LaunchOptions", StringComparison.OrdinalIgnoreCase))
                    (argumentsStart, argumentsEnd) = (start, end);
                if (key.Equals("Exe", StringComparison.OrdinalIgnoreCase))
                    (exeStart, exeEnd) = (start, end);
            });
            if (hiddenEnd == 0) hiddenStart = hiddenEnd = input.Position - 1;
            entries.Add((fields, nameStart, nameEnd, hiddenStart, hiddenEnd, argumentsStart, argumentsEnd, exeStart, exeEnd));
        }
        var appendAt = input.Position;
        if (reader.ReadByte() != 8 || reader.ReadByte() != 8 || input.Position != input.Length)
            throw new InvalidDataException("Unexpected Steam shortcut data; no changes were made.");
        using var output = new MemoryStream();
        var updates = new List<(long Start, long End, string? Name, uint? Hidden, string? Arguments, string? Executable)>();
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        foreach (var shortcut in shortcuts)
        {
            var existing = entries.Where(entry => entry.Fields.GetValueOrDefault("appid") is uint id && id == shortcut.AppId).ToArray();
            if (existing.Length > 0)
            {
                if (existing.Length != 1
                    || !(Equals(existing[0].Fields.GetValueOrDefault("AppName"), shortcut.Name)
                        || shortcut.LegacyName != null && Equals(existing[0].Fields.GetValueOrDefault("AppName"), shortcut.LegacyName))
                    || !(Equals(existing[0].Fields.GetValueOrDefault("Exe"), shortcut.Executable)
                        || shortcut.LegacyExecutable != null && Equals(existing[0].Fields.GetValueOrDefault("Exe"), shortcut.LegacyExecutable))
                    || !(Equals(existing[0].Fields.GetValueOrDefault("LaunchOptions"), shortcut.Arguments)
                        || existing[0].Fields.GetValueOrDefault("LaunchOptions") is string oldArguments
                        && shortcut.LegacyArguments?.Contains(oldArguments) == true))
                    throw new InvalidDataException("Steam shortcut ID collision; existing entries were preserved.");
                if (!Equals(existing[0].Fields.GetValueOrDefault("AppName"), shortcut.Name))
                    updates.Add((existing[0].NameStart, existing[0].NameEnd, shortcut.Name, null, null, null));
                if (!Equals(existing[0].Fields.GetValueOrDefault("LaunchOptions"), shortcut.Arguments))
                    updates.Add((existing[0].ArgumentsStart, existing[0].ArgumentsEnd, null, null, shortcut.Arguments, null));
                if (!Equals(existing[0].Fields.GetValueOrDefault("Exe"), shortcut.Executable))
                    updates.Add((existing[0].ExeStart, existing[0].ExeEnd, null, null, null, shortcut.Executable));
                if (shortcut.Hidden is { } hidden)
                {
                    var value = hidden ? 1u : 0u;
                    if (existing[0].Fields.TryGetValue("IsHidden", out var old) && old is not uint)
                        throw new InvalidDataException("Invalid shortcut visibility field; no changes were made.");
                    if (!Equals(old, value))
                        updates.Add((existing[0].HiddenStart, existing[0].HiddenEnd, null, value, null, null));
                }
                continue;
            }
            writer.Write((byte)0);
            WriteString(writer, (nextIndex++).ToString());
            WriteInt(writer, "appid", shortcut.AppId);
            WriteField(writer, "AppName", shortcut.Name);
            WriteField(writer, "Exe", shortcut.Executable);
            WriteField(writer, "StartDir", "\"" + Path.GetDirectoryName(shortcut.Executable.Trim('"')) + "\"");
            WriteField(writer, "LaunchOptions", shortcut.Arguments);
            foreach (var field in new[] { "icon", "ShortcutPath", "DevkitGameID", "FlatpakAppID" }) WriteField(writer, field, "");
            WriteInt(writer, "IsHidden", shortcut.Hidden == true ? 1u : 0u);
            foreach (var field in new[] { "OpenVR", "Devkit", "DevkitOverrideAppID", "LastPlayTime" }) WriteInt(writer, field, 0);
            WriteInt(writer, "AllowDesktopConfig", 1);
            WriteInt(writer, "AllowOverlay", 1);
            writer.Write((byte)0);
            WriteString(writer, "tags");
            WriteField(writer, "0", "Reimagined Launcher");
            writer.Write((byte)8);
            writer.Write((byte)8);
        }
        writer.Write((byte)8);
        writer.Write((byte)8);
        using var result = new MemoryStream();
        using var resultWriter = new BinaryWriter(result, Encoding.UTF8, true);
        var position = 0;
        foreach (var update in updates.OrderBy(update => update.Start))
        {
            result.Write(original.AsSpan(position, (int)update.Start - position));
            if (update.Name is { } name) WriteField(resultWriter, "AppName", name);
            else if (update.Arguments is { } arguments) WriteField(resultWriter, "LaunchOptions", arguments);
            else if (update.Executable is { } executable) WriteField(resultWriter, "Exe", executable);
            else WriteInt(resultWriter, "IsHidden", update.Hidden!.Value);
            position = (int)update.End;
        }
        result.Write(original.AsSpan(position, (int)appendAt - position));
        output.Position = 0;
        output.CopyTo(result);
        return result.ToArray();
    }

    /// <summary>Reads supported binary VDF fields with bounded nesting and duplicate-key rejection.</summary>
    private static Dictionary<string, object> ReadObject(BinaryReader reader, int depth,
        Action<string, long, long>? fieldRead = null)
    {
        if (depth > 32) throw new InvalidDataException("Steam shortcut nesting is too deep.");
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var start = reader.BaseStream.Position;
            var type = reader.ReadByte();
            if (type == 8) return result;
            var key = ReadString(reader);
            object value = type switch
            {
                0 => ReadObject(reader, depth + 1),
                1 => ReadString(reader),
                2 or 3 or 4 or 6 => reader.ReadUInt32(),
                7 => reader.ReadUInt64(),
                _ => throw new InvalidDataException("Unsupported binary Steam shortcut field; no changes were made.")
            };
            if (!result.TryAdd(key, value)) throw new InvalidDataException("Duplicate Steam shortcut field.");
            fieldRead?.Invoke(key, start, reader.BaseStream.Position);
        }
    }

    /// <summary>Reads the next binary field type without advancing the stream.</summary>
    private static byte Peek(BinaryReader reader)
    {
        var value = reader.ReadByte();
        reader.BaseStream.Position--;
        return value;
    }

    /// <summary>Reads a null-terminated binary VDF string with strict UTF-8 decoding.</summary>
    private static string ReadString(BinaryReader reader)
    {
        using var bytes = new MemoryStream();
        byte value;
        while ((value = reader.ReadByte()) != 0) bytes.WriteByte(value);
        return new UTF8Encoding(false, true).GetString(bytes.ToArray());
    }

    /// <summary>Writes a UTF-8 VDF string and rejects embedded null bytes.</summary>
    private static void WriteString(BinaryWriter writer, string value)
    {
        if (value.Contains('\0')) throw new InvalidDataException("Steam shortcut string contains a null byte.");
        writer.Write(Encoding.UTF8.GetBytes(value));
        writer.Write((byte)0);
    }

    /// <summary>Writes a named binary VDF string field.</summary>
    private static void WriteField(BinaryWriter writer, string name, string value)
    {
        writer.Write((byte)1);
        WriteString(writer, name);
        WriteString(writer, value);
    }

    /// <summary>Writes a named unsigned 32-bit binary VDF field.</summary>
    private static void WriteInt(BinaryWriter writer, string name, uint value)
    {
        writer.Write((byte)2);
        WriteString(writer, name);
        writer.Write(value);
    }
}
