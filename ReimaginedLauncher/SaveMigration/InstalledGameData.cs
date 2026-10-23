using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using D2SSharp.Data;
using D2SSharp.Enums;

namespace ReimaginedLauncher.SaveMigration;

public sealed class InstalledGameData
{
    private static readonly string[] RequiredFiles =
    [
        "itemstatcost.txt",
        "uniqueitems.txt",
        "setitems.txt",
        "magicprefix.txt",
        "magicsuffix.txt",
        "armor.txt",
        "weapons.txt",
        "misc.txt",
        "itemtypes.txt"
    ];

    private InstalledGameData(string sourceDirectory, string copiedDirectory, string? version, string fingerprint,
        GameDataSnapshot snapshot)
    {
        SourceDirectory = Path.GetFullPath(sourceDirectory);
        CopiedDirectory = Path.GetFullPath(copiedDirectory);
        Version = version;
        Fingerprint = fingerprint;
        Snapshot = snapshot;
    }

    public string SourceDirectory { get; }
    public string CopiedDirectory { get; }
    public string? Version { get; }
    public string Fingerprint { get; }

    public string Label => string.IsNullOrWhiteSpace(Version)
        ? "Current installed Reimagined"
        : $"Current installed Reimagined {Version}";

    public GameDataSnapshot Snapshot { get; }

    public bool IsUnchanged()
    {
        try
        {
            return ComputeFingerprint(SourceDirectory) == Fingerprint;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException)
        {
            return false;
        }
    }

    public bool HasSameTables(string directory)
    {
        try
        {
            var installedFiles = FindRequiredFiles(CopiedDirectory);
            var otherFiles = FindRequiredFiles(directory);
            return RequiredFiles.All(fileName => File.ReadAllBytes(installedFiles[fileName])
                .AsSpan().SequenceEqual(File.ReadAllBytes(otherFiles[fileName])));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException)
        {
            return false;
        }
    }

    public static InstalledGameData Load(string excelDirectory, string copyDirectory, string? version = null)
    {
        if (string.IsNullOrWhiteSpace(excelDirectory) || !Directory.Exists(excelDirectory))
        {
            throw InvalidInstalledData(excelDirectory,
                "the installed Reimagined Excel data directory could not be found");
        }

        try
        {
            var sourceFiles = FindRequiredFiles(excelDirectory);
            Directory.CreateDirectory(copyDirectory);
            foreach (var fileName in RequiredFiles)
            {
                File.WriteAllBytes(Path.Combine(copyDirectory, fileName), File.ReadAllBytes(sourceFiles[fileName]));
            }

            var fingerprint = ComputeFingerprint(copyDirectory);
            if (fingerprint != ComputeFingerprint(excelDirectory))
            {
                throw InvalidInstalledData(excelDirectory,
                    "the installed TXT files changed while they were being loaded; reopen the conversion window");
            }

            var snapshot = GameDataSnapshot.Load(
                string.IsNullOrWhiteSpace(version)
                    ? "Current installed Reimagined"
                    : $"Current installed Reimagined {version}",
                copyDirectory);
            foreach (var kind in TableSchema.AllKinds)
            {
                _ = TableSchema.Extract(kind, snapshot[kind]);
            }

            ValidateD2SSharpTables(copyDirectory);
            return new InstalledGameData(excelDirectory, copyDirectory, version?.Trim(), fingerprint, snapshot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            throw InvalidInstalledData(excelDirectory, exception.Message, exception);
        }
    }

    private static void ValidateD2SSharpTables(string directory)
    {
        var externalData = new TxtFileExternalData(directory, SaveMigrator.TargetVersion);
        var statCount = TableSchema.Extract(TableKind.ItemStatCost,
            TableSnapshot.Load(Path.Combine(directory, TableSchema.FileName(TableKind.ItemStatCost)))).Count;
        if (statCount == 0)
        {
            throw new InvalidDataException("itemstatcost.txt contains no stat definitions");
        }

        _ = externalData.GetStatInfo((StatId)(statCount - 1), SaveMigrator.TargetVersion);
    }

    private static Dictionary<string, string> FindRequiredFiles(string directory)
    {
        var files = Directory.EnumerateFiles(directory)
            .ToDictionary(path => Path.GetFileName(path), path => path, StringComparer.OrdinalIgnoreCase);
        var missing = RequiredFiles.Where(fileName => !files.ContainsKey(fileName)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidDataException($"missing required TXT file(s): {string.Join(", ", missing)}");
        }

        return RequiredFiles.ToDictionary(fileName => fileName, fileName => files[fileName],
            StringComparer.OrdinalIgnoreCase);
    }

    private static string ComputeFingerprint(string directory)
    {
        var files = FindRequiredFiles(directory);
        return ComputeFingerprint(files);
    }

    private static string ComputeFingerprint(Dictionary<string, string> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var fileName in RequiredFiles)
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(fileName));
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(files[fileName]));
            hash.AppendData([0]);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static InvalidDataException InvalidInstalledData(string directory, string detail,
        Exception? innerException = null) =>
        new($"The installed Reimagined save data at '{directory}' is unavailable or incomplete: {detail}. "
            + "Install or repair the normal Reimagined mod and reopen Save Conversion. No saves were changed.",
            innerException);
}