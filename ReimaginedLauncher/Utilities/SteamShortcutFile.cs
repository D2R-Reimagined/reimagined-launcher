using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ReimaginedLauncher.Utilities;

internal sealed record LauncherSteamShortcut(string Name, string Executable, string Arguments, string? LegacyName = null)
{
    public uint AppId => SteamShortcutFile.CalculateAppId(Executable, LegacyName ?? Name);
    public ulong GameId => ((ulong)AppId << 32) | 0x02000000;
}

internal static class SteamShortcutFile
{
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

    internal static byte[] AppendOwnedShortcuts(byte[] original, params LauncherSteamShortcut[] shortcuts)
    {
        if (original.Length == 0) original = [0, .. Encoding.UTF8.GetBytes("shortcuts\0"), 8, 8];
        using var input = new MemoryStream(original);
        using var reader = new BinaryReader(input, Encoding.UTF8);
        if (reader.ReadByte() != 0 || ReadString(reader) != "shortcuts")
            throw new InvalidDataException("Unsupported Steam shortcut root; no changes were made.");
        var entries = new List<(Dictionary<string, object> Fields, long NameStart, long NameEnd)>();
        var nextIndex = 0;
        while (Peek(reader) != 8)
        {
            if (reader.ReadByte() != 0) throw new InvalidDataException("Invalid Steam shortcut entry.");
            var index = ReadString(reader);
            if (!int.TryParse(index, out var number) || number < 0 || number == int.MaxValue)
                throw new InvalidDataException("Invalid Steam shortcut index.");
            nextIndex = Math.Max(nextIndex, number + 1);
            long nameStart = 0, nameEnd = 0;
            var fields = ReadObject(reader, 0, (key, start, end) =>
            {
                if (key.Equals("AppName", StringComparison.OrdinalIgnoreCase))
                    (nameStart, nameEnd) = (start, end);
            });
            entries.Add((fields, nameStart, nameEnd));
        }
        var appendAt = input.Position;
        if (reader.ReadByte() != 8 || reader.ReadByte() != 8 || input.Position != input.Length)
            throw new InvalidDataException("Unexpected Steam shortcut data; no changes were made.");
        using var output = new MemoryStream();
        var renames = new List<(long Start, long End, string Name)>();
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        foreach (var shortcut in shortcuts)
        {
            var existing = entries.Where(entry => entry.Fields.GetValueOrDefault("appid") is uint id && id == shortcut.AppId).ToArray();
            if (existing.Length > 0)
            {
                if (existing.Length != 1
                    || !(Equals(existing[0].Fields.GetValueOrDefault("AppName"), shortcut.Name)
                        || shortcut.LegacyName != null && Equals(existing[0].Fields.GetValueOrDefault("AppName"), shortcut.LegacyName))
                    || !Equals(existing[0].Fields.GetValueOrDefault("Exe"), shortcut.Executable)
                    || !Equals(existing[0].Fields.GetValueOrDefault("LaunchOptions"), shortcut.Arguments))
                    throw new InvalidDataException("Steam shortcut ID collision; existing entries were preserved.");
                if (!Equals(existing[0].Fields.GetValueOrDefault("AppName"), shortcut.Name))
                    renames.Add((existing[0].NameStart, existing[0].NameEnd, shortcut.Name));
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
            foreach (var field in new[] { "IsHidden", "OpenVR", "Devkit", "DevkitOverrideAppID", "LastPlayTime" }) WriteInt(writer, field, 0);
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
        foreach (var rename in renames.OrderBy(rename => rename.Start))
        {
            result.Write(original.AsSpan(position, (int)rename.Start - position));
            WriteField(resultWriter, "AppName", rename.Name);
            position = (int)rename.End;
        }
        result.Write(original.AsSpan(position, (int)appendAt - position));
        output.Position = 0;
        output.CopyTo(result);
        return result.ToArray();
    }

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

    private static byte Peek(BinaryReader reader)
    {
        var value = reader.ReadByte();
        reader.BaseStream.Position--;
        return value;
    }

    private static string ReadString(BinaryReader reader)
    {
        using var bytes = new MemoryStream();
        byte value;
        while ((value = reader.ReadByte()) != 0) bytes.WriteByte(value);
        return new UTF8Encoding(false, true).GetString(bytes.ToArray());
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        if (value.Contains('\0')) throw new InvalidDataException("Steam shortcut string contains a null byte.");
        writer.Write(Encoding.UTF8.GetBytes(value));
        writer.Write((byte)0);
    }

    private static void WriteField(BinaryWriter writer, string name, string value)
    {
        writer.Write((byte)1);
        WriteString(writer, name);
        WriteString(writer, value);
    }

    private static void WriteInt(BinaryWriter writer, string name, uint value)
    {
        writer.Write((byte)2);
        WriteString(writer, name);
        writer.Write(value);
    }
}
