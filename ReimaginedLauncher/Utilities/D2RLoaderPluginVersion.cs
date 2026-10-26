using System;
using System.IO;
using System.Reflection.PortableExecutable;

namespace ReimaginedLauncher.Utilities;

internal static class D2RLoaderPluginVersion
{
    public static string? Read(string path)
        => ReadMetadata(path).Version;

    public static string? ReadAuthor(string path)
        => ReadMetadata(path).Author;

    public static (string? Version, string? Author) ReadMetadata(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var header = pe.PEHeaders.PEHeader;
            if (header is null || header.Magic != PEMagic.PE32Plus
                || pe.PEHeaders.CoffHeader.Machine != Machine.Amd64)
                return (null, null);

            var exports = header.ExportTableDirectory;
            if (exports.RelativeVirtualAddress == 0 || exports.Size < 40) return (null, null);
            var directory = pe.GetSectionData(exports.RelativeVirtualAddress).GetReader(0, 40);
            directory.Offset = 20;
            var functionCount = directory.ReadUInt32();
            var nameCount = directory.ReadUInt32();
            var functions = directory.ReadInt32();
            var names = directory.ReadInt32();
            var ordinals = directory.ReadInt32();
            if (nameCount > 65536) return (null, null);

            for (var i = 0; i < nameCount; i++)
            {
                var nameRva = pe.GetSectionData(checked(names + i * 4)).GetReader(0, 4).ReadInt32();
                if (ReadString(pe, nameRva) != "D2RLoaderGetPluginInfo") continue;
                var ordinal = pe.GetSectionData(checked(ordinals + i * 2)).GetReader(0, 2).ReadUInt16();
                if (ordinal >= functionCount) return (null, null);
                var getter = pe.GetSectionData(checked(functions + ordinal * 4)).GetReader(0, 4).ReadInt32();
                if (getter >= exports.RelativeVirtualAddress
                    && (long)getter < (long)exports.RelativeVirtualAddress + exports.Size) return (null, null);

                // Optimized x64 metadata getters return a static PluginInfo via LEA RAX,[RIP+disp32]; RET.
                var code = pe.GetSectionData(getter).GetReader(0, 8);
                if (code.ReadByte() != 0x48 || code.ReadByte() != 0x8d || code.ReadByte() != 0x05)
                    return (null, null);
                var infoRva = checked(getter + 7 + code.ReadInt32());
                if (code.ReadByte() != 0xc3) return (null, null);
                return (
                    ReadInfoField(pe, header.ImageBase, infoRva, 24),
                    ReadInfoField(pe, header.ImageBase, infoRva, 32));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException
                                   or ArgumentOutOfRangeException or OverflowException)
        {
        }

        return (null, null);
    }

    private static string? ReadInfoField(PEReader pe, ulong imageBase, int infoRva, int fieldOffset)
    {
        var info = pe.GetSectionData(infoRva).GetReader(0, fieldOffset + 8);
        if (info.ReadUInt32() < fieldOffset + 8) return null;
        info.Offset = fieldOffset;
        var address = info.ReadUInt64();
        if (address < imageBase || address - imageBase > int.MaxValue) return null;
        var value = ReadString(pe, (int)(address - imageBase));
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? ReadString(PEReader pe, int rva)
    {
        var section = pe.GetSectionData(rva);
        var reader = section.GetReader(0, Math.Min(section.Length, 256));
        var length = 0;
        while (reader.RemainingBytes > 0)
        {
            if (reader.ReadByte() == 0)
            {
                reader.Offset = 0;
                return reader.ReadUTF8(length);
            }
            length++;
        }
        return null;
    }
}
