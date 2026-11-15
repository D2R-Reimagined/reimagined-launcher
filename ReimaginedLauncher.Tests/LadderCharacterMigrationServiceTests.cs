using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using D2SSharp;
using D2SSharp.Model;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public class LadderCharacterMigrationServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _savedGamesDir;
    private readonly string _modsSaveRoot;
    private readonly string _targetSaveDir;
    private readonly string _backupRootDir;

    public LadderCharacterMigrationServiceTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "reimagined-char-migration-tests-" + Guid.NewGuid().ToString("N"));
        _savedGamesDir = Path.Combine(_tempDir, "SavedGames");
        _modsSaveRoot = Path.Combine(_savedGamesDir, "Diablo II Resurrected", "mods");
        _targetSaveDir = Path.Combine(_modsSaveRoot, "ReimaginedThree");
        _backupRootDir = Path.Combine(_modsSaveRoot, ".reimagined-launcher", LadderCharacterMigrationService.MigrationBackupFolderName);

        Directory.CreateDirectory(_modsSaveRoot);
        Directory.CreateDirectory(_targetSaveDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(_tempDir, "*", SearchOption.AllDirectories))
            {
                var attrs = File.GetAttributes(entry);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(entry, attrs & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task InspectAndMigrate_DiscoversRegistryAndServerSaves_RecoversBackupsAndPending_ExcludesPreviewAnd403Stub_AndPreservesSources()
    {
        var customLadderId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var customSourceDir = Path.Combine(_modsSaveRoot, "CustomLadderSaves");
        Directory.CreateDirectory(customSourceDir);
        WriteNamedV105Save(Path.Combine(customSourceDir, "SoManyBones.d2s"), "SoManyBones");

        var registryPath = LadderSavePathRegistry.RegistryPath(_modsSaveRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
        File.WriteAllText(
            registryPath,
            JsonSerializer.Serialize(new Dictionary<Guid, string>
            {
                [customLadderId] = "CustomLadderSaves"
            }));

        var displacedLadderId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var displacedFolder = Path.Combine(_modsSaveRoot, $"ReimaginedThree-Hallowing-{displacedLadderId:N}");
        var pendingDir = Path.Combine(displacedFolder, ".server-saves", "pending-upload");
        var backupsDir = Path.Combine(displacedFolder, ".server-saves", "backups");
        Directory.CreateDirectory(pendingDir);
        Directory.CreateDirectory(backupsDir);

        WriteNamedV105Save(Path.Combine(backupsDir, "BackupHero.20261110-120000.d2s"), "BackupHero");

        var pendingPath = Path.Combine(pendingDir, "unsynced-temp.d2s");
        WriteNamedV105Save(pendingPath, "PendingHero");
        File.WriteAllText(pendingPath + ".origin", "PendingHero.d2s\n");

        WriteCreationStubV105(Path.Combine(displacedFolder, "JustCreated.d2s"));
        WriteCorruptChecksumSave(Path.Combine(displacedFolder, "CorruptHero.d2s"));

        var previewDir = Path.Combine(_modsSaveRoot, "ReimaginedPreview-deadbeef");
        Directory.CreateDirectory(previewDir);
        WriteNamedV105Save(Path.Combine(previewDir, "PreviewChar.d2s"), "PreviewChar");

        var nestedTargetSubdir = Path.Combine(_targetSaveDir, "nested");
        Directory.CreateDirectory(nestedTargetSubdir);
        File.WriteAllText(Path.Combine(nestedTargetSubdir, "note.txt"), "existing nested file");

        var hints = new[]
        {
            new KnownLadderSaveHint(customLadderId, "Season Custom", false),
            new KnownLadderSaveHint(displacedLadderId, "Hallowing Ladder", true)
        };

        var inspection = LadderCharacterMigrationService.Inspect(
            LadderToNormalCopyService.DefaultSavePath,
            hints,
            _savedGamesDir);

        Assert.Equal(
            NormalizePath(LadderCharacterMigrationService.ResolveTargetSaveDirectory(LadderToNormalCopyService.DefaultSavePath, _savedGamesDir)!),
            NormalizePath(inspection.TargetSaveDirectory!));
        Assert.True(inspection.TargetHasExistingSaves);
        Assert.DoesNotContain(inspection.Sources, s => s.FolderName.StartsWith("ReimaginedPreview-", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(inspection.Sources, s => s.FolderName.Equals("ReimaginedThree", StringComparison.OrdinalIgnoreCase));

        var customSource = Assert.Single(inspection.Sources, s => s.FolderName == "CustomLadderSaves");
        Assert.Equal(customLadderId, customSource.LadderId);
        Assert.Contains("Season Custom", customSource.DisplayName);

        var displacedSource = Assert.Single(inspection.Sources, s => s.FolderName.StartsWith("ReimaginedThree-Hallowing-"));
        Assert.Equal(displacedLadderId, displacedSource.LadderId);
        Assert.Contains("Active Ladder", displacedSource.DisplayName);

        var backupCandidate = Assert.Single(displacedSource.Characters, c => c.CharacterName == "BackupHero");
        Assert.True(backupCandidate.IsMigratable);
        Assert.Equal(LadderSaveCopyOrigin.LocalBackup, Assert.Single(backupCandidate.Copies).Origin);

        var pendingCandidate = Assert.Single(displacedSource.Characters, c => c.CharacterName == "PendingHero");
        Assert.True(pendingCandidate.IsMigratable);
        Assert.Equal(LadderSaveCopyOrigin.PendingUpload, Assert.Single(pendingCandidate.Copies).Origin);

        var stubCandidate = Assert.Single(displacedSource.Characters, c => c.CharacterName == "JustCreated");
        Assert.False(stubCandidate.IsMigratable);
        Assert.Contains("creation stub", stubCandidate.BlockedReason, StringComparison.OrdinalIgnoreCase);

        var corruptCandidate = Assert.Single(displacedSource.Characters, c => c.CharacterName == "CorruptHero");
        Assert.False(corruptCandidate.IsMigratable);
        Assert.Contains("checksum", corruptCandidate.BlockedReason, StringComparison.OrdinalIgnoreCase);

        var sourceHashesBefore = SnapshotHashes(displacedFolder);
        var backupSnap = backupCandidate.Copies[0];
        var pendingSnap = pendingCandidate.Copies[0];

        var result = await LadderCharacterMigrationService.MigrateAsync(
            new LadderCharacterMigrationRequest(
                displacedSource.FullPath,
                inspection.TargetSaveDirectory!,
                inspection.BackupRootDirectory!,
                null,
                [
                    new LadderCharacterMigrationSelection(
                        backupSnap.SourcePath,
                        backupSnap.SourceSha256,
                        backupSnap.CharacterName,
                        backupSnap.CharacterName,
                        AllowOverwriteExisting: false,
                        ExpectedExistingTargetSha256: null,
                        PairedBindPath: null,
                        CompanionPaths: []),
                    new LadderCharacterMigrationSelection(
                        pendingSnap.SourcePath,
                        pendingSnap.SourceSha256,
                        pendingSnap.CharacterName,
                        pendingSnap.CharacterName,
                        AllowOverwriteExisting: false,
                        ExpectedExistingTargetSha256: null,
                        PairedBindPath: null,
                        CompanionPaths: [])
                ],
                []));

        Assert.Equal(2, result.MigratedCharacterCount);
        Assert.True(File.Exists(Path.Combine(_targetSaveDir, "BackupHero.d2s")));
        Assert.True(File.Exists(Path.Combine(_targetSaveDir, "PendingHero.d2s")));
        Assert.Equal(sourceHashesBefore, SnapshotHashes(displacedFolder));
    }

    [Fact]
    public async Task CollisionSafeguards_DetectsExactAndEmbeddedConflicts_BlocksUnsafeOverwrites_AndPreservesChecksumOnV105Rename()
    {
        var ladderDir = Path.Combine(_modsSaveRoot, "ReimaginedThree-Hallowing-abcdef01");
        Directory.CreateDirectory(ladderDir);

        WriteNamedV105Save(Path.Combine(_targetSaveDir, "Hero.d2s"), "Hero");
        WriteNamedV105Save(Path.Combine(_targetSaveDir, "AliasFile.d2s"), "Banker");
        File.Copy(ResolveFixturePath("BruhII.d2s"), Path.Combine(_targetSaveDir, "BruhII.d2s"));

        var ladderHeroPath = Path.Combine(ladderDir, "Hero.d2s");
        WriteNamedV105Save(ladderHeroPath, "Hero");
        var ladderBankerPath = Path.Combine(ladderDir, "Banker.d2s");
        WriteNamedV105Save(ladderBankerPath, "Banker");
        File.Copy(ResolveFixturePath("BruhII.d2s"), Path.Combine(ladderDir, "BruhII.d2s"));

        var inspection = LadderCharacterMigrationService.Inspect(
            LadderToNormalCopyService.DefaultSavePath,
            null,
            _savedGamesDir);
        var source = Assert.Single(inspection.Sources);

        var heroCandidate = Assert.Single(source.Characters, c => c.CharacterName == "Hero");
        Assert.NotNull(heroCandidate.ExistingConflict);
        Assert.True(heroCandidate.ExistingConflict!.IsFilenameMatch);
        Assert.False(string.IsNullOrWhiteSpace(heroCandidate.SuggestedSafeRename));
        Assert.Null(SaveConversionService.ValidateCharacterName(heroCandidate.SuggestedSafeRename!));

        var bankerCandidate = Assert.Single(source.Characters, c => c.CharacterName == "Banker");
        Assert.NotNull(bankerCandidate.ExistingConflict);
        Assert.False(bankerCandidate.ExistingConflict!.IsFilenameMatch);
        Assert.Equal("AliasFile.d2s", bankerCandidate.ExistingConflict.FileName);

        var v99Candidate = Assert.Single(source.Characters, c => c.CharacterName == "BruhII");
        Assert.NotNull(v99Candidate.ExistingConflict);
        Assert.Null(v99Candidate.SuggestedSafeRename);

        var heroCopy = heroCandidate.Copies[0];
        var bankerCopy = bankerCandidate.Copies[0];
        var v99Copy = v99Candidate.Copies[0];

        Assert.Throws<InvalidOperationException>(() => LadderCharacterMigrationService.ValidateRequest(
            new LadderCharacterMigrationRequest(
                source.FullPath,
                _targetSaveDir,
                _backupRootDir,
                null,
                [
                    new LadderCharacterMigrationSelection(
                        heroCopy.SourcePath,
                        heroCopy.SourceSha256,
                        "Hero",
                        "Hero",
                        AllowOverwriteExisting: false,
                        ExpectedExistingTargetSha256: null,
                        null,
                        [])
                ],
                [])));

        Assert.Throws<InvalidOperationException>(() => LadderCharacterMigrationService.ValidateRequest(
            new LadderCharacterMigrationRequest(
                source.FullPath,
                _targetSaveDir,
                _backupRootDir,
                null,
                [
                    new LadderCharacterMigrationSelection(
                        heroCopy.SourcePath,
                        heroCopy.SourceSha256,
                        "Hero",
                        "Hero",
                        AllowOverwriteExisting: true,
                        ExpectedExistingTargetSha256: new string('0', 64),
                        null,
                        [])
                ],
                [])));

        Assert.Throws<InvalidOperationException>(() => LadderCharacterMigrationService.ValidateRequest(
            new LadderCharacterMigrationRequest(
                source.FullPath,
                _targetSaveDir,
                _backupRootDir,
                null,
                [
                    new LadderCharacterMigrationSelection(
                        bankerCopy.SourcePath,
                        bankerCopy.SourceSha256,
                        "Banker",
                        "Banker",
                        AllowOverwriteExisting: true,
                        ExpectedExistingTargetSha256: bankerCandidate.ExistingConflict.Sha256,
                        null,
                        [])
                ],
                [])));

        Assert.Throws<InvalidOperationException>(() => LadderCharacterMigrationService.ValidateRequest(
            new LadderCharacterMigrationRequest(
                source.FullPath,
                _targetSaveDir,
                _backupRootDir,
                null,
                [
                    new LadderCharacterMigrationSelection(
                        v99Copy.SourcePath,
                        v99Copy.SourceSha256,
                        "BruhII",
                        "BruhII-L",
                        AllowOverwriteExisting: false,
                        ExpectedExistingTargetSha256: null,
                        null,
                        [])
                ],
                [])));

        var existingHeroBytes = File.ReadAllBytes(Path.Combine(_targetSaveDir, "Hero.d2s"));
        var targetRename = heroCandidate.SuggestedSafeRename!;
        var migrationResult = await LadderCharacterMigrationService.MigrateAsync(
            new LadderCharacterMigrationRequest(
                source.FullPath,
                _targetSaveDir,
                _backupRootDir,
                null,
                [
                    new LadderCharacterMigrationSelection(
                        heroCopy.SourcePath,
                        heroCopy.SourceSha256,
                        "Hero",
                        targetRename,
                        AllowOverwriteExisting: false,
                        ExpectedExistingTargetSha256: null,
                        null,
                        [])
                ],
                []));

        Assert.Equal(1, migrationResult.MigratedCharacterCount);
        Assert.Equal(1, migrationResult.RenamedCharacterCount);
        Assert.Equal(0, migrationResult.OverwrittenCharacterCount);
        Assert.Equal(existingHeroBytes, File.ReadAllBytes(Path.Combine(_targetSaveDir, "Hero.d2s")));

        var renamedPath = Path.Combine(_targetSaveDir, targetRename + ".d2s");
        Assert.True(File.Exists(renamedPath));
        var sourceBytes = File.ReadAllBytes(ladderHeroPath);
        var renamedBytes = File.ReadAllBytes(renamedPath);
        Assert.Equal(sourceBytes.Length, renamedBytes.Length);
        Assert.True(ChecksumCalculator.Verify(renamedBytes));
        Assert.Equal(targetRename, D2SaveLayoutV104.From(renamedBytes).Name);

        const int checksumStart = 12;
        const int checksumEnd = 16;
        const int nameStart = 16 + 0x9F + 0x7C;
        const int nameEnd = nameStart + 96;
        for (var i = 0; i < renamedBytes.Length; i++)
        {
            var expectedChanged = i is (>= checksumStart and < checksumEnd) or (>= nameStart and < nameEnd);
            if (!expectedChanged)
            {
                Assert.Equal(sourceBytes[i], renamedBytes[i]);
            }
        }
    }

    [Fact]
    public async Task MigrateAsync_CreatesVerifiedBackupAndZip_HandlesReadOnlyOverwrite_PrunesOldBackups_AndBlocksOverlappingRoots()
    {
        var ladderDir = Path.Combine(_modsSaveRoot, "ReimaginedThree-Hallowing-12345678");
        Directory.CreateDirectory(ladderDir);

        var oldHeroPath = Path.Combine(_targetSaveDir, "Hero.d2s");
        WriteNamedV105Save(oldHeroPath, "Hero", ResolveFixturePath("FuckBenFuckingF.d2s"));
        File.SetAttributes(oldHeroPath, File.GetAttributes(oldHeroPath) | FileAttributes.ReadOnly);

        var staleMapPath = Path.Combine(_targetSaveDir, "Hero.map");
        File.WriteAllBytes(staleMapPath, [1, 2, 3, 4]);
        File.SetAttributes(staleMapPath, File.GetAttributes(staleMapPath) | FileAttributes.ReadOnly);

        var keepMePath = Path.Combine(_targetSaveDir, "KeepMe.d2s");
        WriteNamedV105Save(keepMePath, "KeepMe");
        var keepMeBefore = File.ReadAllBytes(keepMePath);
        var oldHeroBefore = File.ReadAllBytes(oldHeroPath);

        var newHeroPath = Path.Combine(ladderDir, "Hero.d2s");
        WriteNamedV105Save(newHeroPath, "Hero", ResolveFixturePath("SoManyBones.d2s"));
        var newHeroBytes = File.ReadAllBytes(newHeroPath);

        Directory.CreateDirectory(_backupRootDir);
        for (var i = 0; i < 12; i++)
        {
            var oldBatch = Path.Combine(_backupRootDir, $"20250101-00000{i:D2}-ReimaginedThree");
            Directory.CreateDirectory(oldBatch);
            File.WriteAllText(Path.Combine(oldBatch, "restore-instructions.txt"), "old backup");
        }

        var inspection = LadderCharacterMigrationService.Inspect(
            LadderToNormalCopyService.DefaultSavePath,
            null,
            _savedGamesDir);
        var source = Assert.Single(inspection.Sources);
        var heroCandidate = Assert.Single(source.Characters, c => c.CharacterName == "Hero");
        var heroCopy = heroCandidate.Copies[0];

        Assert.Throws<InvalidOperationException>(() => LadderCharacterMigrationService.ValidateRequest(
            new LadderCharacterMigrationRequest(
                source.FullPath,
                _targetSaveDir,
                Path.Combine(_targetSaveDir, "nested-backups"),
                null,
                [
                    new LadderCharacterMigrationSelection(
                        heroCopy.SourcePath,
                        heroCopy.SourceSha256,
                        "Hero",
                        "Hero",
                        AllowOverwriteExisting: true,
                        ExpectedExistingTargetSha256: heroCandidate.ExistingConflict!.Sha256,
                        null,
                        [])
                ],
                [])));

        var launcherBackupDir = Path.Combine(_tempDir, "LauncherZipBackups");
        var result = await LadderCharacterMigrationService.MigrateAsync(
            new LadderCharacterMigrationRequest(
                source.FullPath,
                _targetSaveDir,
                _backupRootDir,
                launcherBackupDir,
                [
                    new LadderCharacterMigrationSelection(
                        heroCopy.SourcePath,
                        heroCopy.SourceSha256,
                        "Hero",
                        "Hero",
                        AllowOverwriteExisting: true,
                        ExpectedExistingTargetSha256: heroCandidate.ExistingConflict!.Sha256,
                        null,
                        [])
                ],
                []));

        Assert.Equal(1, result.OverwrittenCharacterCount);
        Assert.Equal(newHeroBytes, File.ReadAllBytes(oldHeroPath));
        Assert.False(File.Exists(staleMapPath));
        Assert.Equal(keepMeBefore, File.ReadAllBytes(keepMePath));

        Assert.NotNull(result.BackupDirectory);
        Assert.True(File.Exists(Path.Combine(result.BackupDirectory!, "restore-instructions.txt")));
        var snapshotHero = Path.Combine(result.BackupDirectory!, "ReimaginedThree", "Hero.d2s");
        Assert.Equal(oldHeroBefore, File.ReadAllBytes(snapshotHero));

        Assert.NotNull(result.LauncherZipBackupPath);
        Assert.True(File.Exists(result.LauncherZipBackupPath!));
        using (var archive = ZipFile.OpenRead(result.LauncherZipBackupPath!))
        {
            Assert.NotNull(archive.GetEntry("Hero.d2s"));
            Assert.NotNull(archive.GetEntry("KeepMe.d2s"));
        }

        var remainingBackups = Directory.GetDirectories(_backupRootDir);
        Assert.Equal(LadderCharacterMigrationService.RetainedMigrationBackupCount, remainingBackups.Length);
        Assert.Contains(remainingBackups, dir => dir.Equals(result.BackupDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MigrateAsync_MidBatchFailure_RollsBackCreatedAndOverwrittenReadOnlyFilesFromBackup()
    {
        var ladderDir = Path.Combine(_modsSaveRoot, "ReimaginedThree-Hallowing-fedcba98");
        Directory.CreateDirectory(ladderDir);

        var targetHero = Path.Combine(_targetSaveDir, "Hero.d2s");
        WriteNamedV105Save(targetHero, "Hero", ResolveFixturePath("FuckBenFuckingF.d2s"));
        var originalHeroBytes = File.ReadAllBytes(targetHero);
        File.SetAttributes(targetHero, File.GetAttributes(targetHero) | FileAttributes.ReadOnly);

        var targetMap = Path.Combine(_targetSaveDir, "Hero.map");
        var originalMapBytes = new byte[] { 9, 8, 7, 6 };
        File.WriteAllBytes(targetMap, originalMapBytes);

        WriteNamedV105Save(Path.Combine(ladderDir, "Hero.d2s"), "Hero", ResolveFixturePath("SoManyBones.d2s"));
        WriteNamedV105Save(Path.Combine(ladderDir, "FreshChar.d2s"), "FreshChar", ResolveFixturePath("Popsicle.d2s"));

        var inspection = LadderCharacterMigrationService.Inspect(
            LadderToNormalCopyService.DefaultSavePath,
            null,
            _savedGamesDir);
        var source = Assert.Single(inspection.Sources);
        var heroCandidate = Assert.Single(source.Characters, c => c.CharacterName == "Hero");
        var freshCandidate = Assert.Single(source.Characters, c => c.CharacterName == "FreshChar");

        var selections = new[]
        {
            new LadderCharacterMigrationSelection(
                heroCandidate.Copies[0].SourcePath,
                heroCandidate.Copies[0].SourceSha256,
                "Hero",
                "Hero",
                AllowOverwriteExisting: true,
                ExpectedExistingTargetSha256: heroCandidate.ExistingConflict!.Sha256,
                null,
                []),
            new LadderCharacterMigrationSelection(
                freshCandidate.Copies[0].SourcePath,
                freshCandidate.Copies[0].SourceSha256,
                "FreshChar",
                "FreshChar",
                AllowOverwriteExisting: false,
                ExpectedExistingTargetSha256: null,
                null,
                [])
        };

        var checkCallCount = 0;
        void EnsureSafeSimulatingMidBatchFailure()
        {
            checkCallCount++;
            if (checkCallCount == 4)
            {
                throw new InvalidOperationException("Simulated D2R launch mid-batch.");
            }
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => LadderCharacterMigrationService.MigrateAsync(
            new LadderCharacterMigrationRequest(
                source.FullPath,
                _targetSaveDir,
                _backupRootDir,
                null,
                selections,
                []),
            EnsureSafeSimulatingMidBatchFailure));

        Assert.Equal(originalHeroBytes, File.ReadAllBytes(targetHero));
        Assert.Equal(originalMapBytes, File.ReadAllBytes(targetMap));
        Assert.False(File.Exists(Path.Combine(_targetSaveDir, "FreshChar.d2s")));
    }

    [Fact]
    public async Task SharedStashProtection_AllowsEmptyDestination_BlocksExistingSoftcoreStash_AndRejectsMalformedD2i()
    {
        var ladderDir = Path.Combine(_modsSaveRoot, "ReimaginedThree-Hallowing-99887766");
        Directory.CreateDirectory(ladderDir);

        var validStashBytes = File.ReadAllBytes(ResolveFixturePath("ModernSharedStashSoftCoreV2.d2i"));
        var ladderStashPath = Path.Combine(ladderDir, "ModernSharedStashSoftCoreV2.d2i");
        File.WriteAllBytes(ladderStashPath, validStashBytes);

        var initialInspect = LadderCharacterMigrationService.Inspect(
            LadderToNormalCopyService.DefaultSavePath,
            null,
            _savedGamesDir);
        var initialSource = Assert.Single(initialInspect.Sources);
        var stashOption = Assert.Single(initialSource.SharedStashes);
        Assert.False(stashOption.ExistsInDestination);
        Assert.Equal(7, stashOption.TabCount);

        var copyResult = await LadderCharacterMigrationService.MigrateAsync(
            new LadderCharacterMigrationRequest(
                initialSource.FullPath,
                _targetSaveDir,
                _backupRootDir,
                null,
                [],
                [
                    new LadderSharedStashSelection(
                        stashOption.FileName,
                        stashOption.SourcePath,
                        stashOption.SourceSha256)
                ]));

        Assert.Equal(1, copyResult.CopiedSharedStashCount);
        Assert.Equal(validStashBytes, File.ReadAllBytes(Path.Combine(_targetSaveDir, "ModernSharedStashSoftCoreV2.d2i")));

        var conflictInspect = LadderCharacterMigrationService.Inspect(
            LadderToNormalCopyService.DefaultSavePath,
            null,
            _savedGamesDir);
        var conflictSource = Assert.Single(conflictInspect.Sources);
        Assert.True(conflictInspect.TargetHasAnySharedStash);
        Assert.True(Assert.Single(conflictSource.SharedStashes).ExistsInDestination);

        await Assert.ThrowsAsync<InvalidOperationException>(() => LadderCharacterMigrationService.MigrateAsync(
            new LadderCharacterMigrationRequest(
                conflictSource.FullPath,
                _targetSaveDir,
                _backupRootDir,
                null,
                [],
                [
                    new LadderSharedStashSelection(
                        stashOption.FileName,
                        stashOption.SourcePath,
                        stashOption.SourceSha256)
                ])));

        File.Delete(Path.Combine(_targetSaveDir, "ModernSharedStashSoftCoreV2.d2i"));
        File.WriteAllBytes(Path.Combine(_targetSaveDir, "SharedStashSoftCoreV2.d2i"), validStashBytes);

        var legacyNameInspect = LadderCharacterMigrationService.Inspect(
            LadderToNormalCopyService.DefaultSavePath,
            null,
            _savedGamesDir);
        var legacySource = Assert.Single(legacyNameInspect.Sources);
        Assert.True(Assert.Single(legacySource.SharedStashes).ExistsInDestination);
        Assert.Throws<InvalidOperationException>(() => LadderCharacterMigrationService.ValidateRequest(
            new LadderCharacterMigrationRequest(
                legacySource.FullPath,
                _targetSaveDir,
                _backupRootDir,
                null,
                [],
                [
                    new LadderSharedStashSelection(
                        stashOption.FileName,
                        stashOption.SourcePath,
                        stashOption.SourceSha256)
                ])));

        File.Delete(Path.Combine(_targetSaveDir, "SharedStashSoftCoreV2.d2i"));
        var badStashBytes = validStashBytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(badStashBytes.AsSpan(16, 4), (uint)(badStashBytes.Length + 50));
        File.WriteAllBytes(ladderStashPath, badStashBytes);

        var malformedInspect = LadderCharacterMigrationService.Inspect(
            LadderToNormalCopyService.DefaultSavePath,
            null,
            _savedGamesDir);
        var malformedSource = Assert.Single(malformedInspect.Sources);
        Assert.Empty(malformedSource.SharedStashes);

        Assert.Throws<InvalidOperationException>(() => LadderCharacterMigrationService.ValidateRequest(
            new LadderCharacterMigrationRequest(
                malformedSource.FullPath,
                _targetSaveDir,
                _backupRootDir,
                null,
                [],
                [
                    new LadderSharedStashSelection(
                        "ModernSharedStashSoftCoreV2.d2i",
                        ladderStashPath,
                        Convert.ToHexStringLower(SHA256.HashData(badStashBytes)))
                ])));

        File.Delete(ladderStashPath);
        var backupStashDir = Path.Combine(ladderDir, ".server-saves", "backups");
        Directory.CreateDirectory(backupStashDir);
        File.WriteAllBytes(Path.Combine(backupStashDir, "ModernSharedStashSoftCoreV2.20261110-120000.d2i"), validStashBytes);

        var backupStashInspect = LadderCharacterMigrationService.Inspect(
            LadderToNormalCopyService.DefaultSavePath,
            null,
            _savedGamesDir);
        var backupStashOption = Assert.Single(Assert.Single(backupStashInspect.Sources).SharedStashes);
        Assert.DoesNotContain(@"\\?\", backupStashOption.SourcePath);
        Assert.False(backupStashOption.ExistsInDestination);
        var backupStashResult = await LadderCharacterMigrationService.MigrateAsync(
            new LadderCharacterMigrationRequest(
                ladderDir,
                _targetSaveDir,
                _backupRootDir,
                null,
                [],
                [
                    new LadderSharedStashSelection(
                        backupStashOption.FileName,
                        backupStashOption.SourcePath,
                        backupStashOption.SourceSha256)
                ]));
        Assert.Equal(1, backupStashResult.CopiedSharedStashCount);
        Assert.Equal(validStashBytes, File.ReadAllBytes(Path.Combine(_targetSaveDir, "ModernSharedStashSoftCoreV2.d2i")));
    }

    [Fact]
    public async Task SidecarCompanions_PairsMatchingBind_CopiesKeyAndCtloOnRename_CleansStaleTargetSidecars_AndRejectsInvalidExtensions()
    {
        var ladderDir = Path.Combine(_modsSaveRoot, "ReimaginedThree-Hallowing-55667788");
        Directory.CreateDirectory(ladderDir);

        var summonerPath = Path.Combine(ladderDir, "Summoner.d2s");
        WriteNamedV105Save(summonerPath, "Summoner");
        var summonerLen = (int)new FileInfo(summonerPath).Length;

        var validBindPath = Path.Combine(ladderDir, "Summoner.bind");
        var validBindBytes = BuildValidBindBytes(summonerLen);
        File.WriteAllBytes(validBindPath, validBindBytes);

        var keyBytes = Encoding.UTF8.GetBytes("key-bindings");
        var ctloBytes = Encoding.UTF8.GetBytes("controller-bindings");
        File.WriteAllBytes(Path.Combine(ladderDir, "Summoner.key"), keyBytes);
        File.WriteAllBytes(Path.Combine(ladderDir, "Summoner.ctlo"), ctloBytes);

        var mismatchPath = Path.Combine(ladderDir, "MismatchBind.d2s");
        WriteNamedV105Save(mismatchPath, "MismatchBind", ResolveFixturePath("Popsicle.d2s"));
        File.WriteAllBytes(Path.Combine(ladderDir, "MismatchBind.bind"), BuildValidBindBytes(12345));

        WriteNamedV105Save(Path.Combine(_targetSaveDir, "Summoner.d2s"), "Summoner");

        var inspection = LadderCharacterMigrationService.Inspect(
            LadderToNormalCopyService.DefaultSavePath,
            null,
            _savedGamesDir);
        var source = Assert.Single(inspection.Sources);

        var summonerCandidate = Assert.Single(source.Characters, c => c.CharacterName == "Summoner");
        var summonerCopy = summonerCandidate.Copies[0];
        Assert.Equal(validBindPath, summonerCopy.PairedBindPath);
        Assert.Equal(2, summonerCopy.CompanionPaths.Count);

        var mismatchCandidate = Assert.Single(source.Characters, c => c.CharacterName == "MismatchBind");
        Assert.Null(mismatchCandidate.Copies[0].PairedBindPath);

        var badExtensionFile = Path.Combine(ladderDir, "evil.exe");
        File.WriteAllText(badExtensionFile, "nope");
        Assert.Throws<InvalidOperationException>(() => LadderCharacterMigrationService.ValidateRequest(
            new LadderCharacterMigrationRequest(
                source.FullPath,
                _targetSaveDir,
                _backupRootDir,
                null,
                [
                    new LadderCharacterMigrationSelection(
                        summonerCopy.SourcePath,
                        summonerCopy.SourceSha256,
                        "Summoner",
                        "Summoner-L",
                        AllowOverwriteExisting: false,
                        ExpectedExistingTargetSha256: null,
                        summonerCopy.PairedBindPath,
                        [badExtensionFile])
                ],
                [])));

        Assert.Throws<InvalidOperationException>(() => LadderCharacterMigrationService.ValidateRequest(
            new LadderCharacterMigrationRequest(
                source.FullPath,
                _targetSaveDir,
                _backupRootDir,
                null,
                [
                    new LadderCharacterMigrationSelection(
                        mismatchCandidate.Copies[0].SourcePath,
                        mismatchCandidate.Copies[0].SourceSha256,
                        "MismatchBind",
                        "MismatchBind",
                        AllowOverwriteExisting: false,
                        ExpectedExistingTargetSha256: null,
                        Path.Combine(ladderDir, "MismatchBind.bind"),
                        [])
                ],
                [])));

        var targetName = summonerCandidate.SuggestedSafeRename!;
        var renameResult = await LadderCharacterMigrationService.MigrateAsync(
            new LadderCharacterMigrationRequest(
                source.FullPath,
                _targetSaveDir,
                _backupRootDir,
                null,
                [
                    new LadderCharacterMigrationSelection(
                        summonerCopy.SourcePath,
                        summonerCopy.SourceSha256,
                        "Summoner",
                        targetName,
                        AllowOverwriteExisting: false,
                        ExpectedExistingTargetSha256: null,
                        summonerCopy.PairedBindPath,
                        summonerCopy.CompanionPaths)
                ],
                []));

        Assert.Equal(1, renameResult.RenamedCharacterCount);
        Assert.Equal(validBindBytes, File.ReadAllBytes(Path.Combine(_targetSaveDir, targetName + ".bind")));
        Assert.Equal(keyBytes, File.ReadAllBytes(Path.Combine(_targetSaveDir, targetName + ".key")));
        Assert.Equal(ctloBytes, File.ReadAllBytes(Path.Combine(_targetSaveDir, targetName + ".ctlo")));

        File.WriteAllText(Path.Combine(_targetSaveDir, "Summoner.ma0"), "stale-map");
        File.WriteAllText(Path.Combine(_targetSaveDir, "Summoner.d2rl"), "stale-d2rl");
        File.WriteAllText(Path.Combine(_targetSaveDir, "Summoner.bind"), "stale-bind");

        var refreshedInspection = LadderCharacterMigrationService.Inspect(
            LadderToNormalCopyService.DefaultSavePath,
            null,
            _savedGamesDir);
        var refreshedSummoner = Assert.Single(Assert.Single(refreshedInspection.Sources).Characters, c => c.CharacterName == "Summoner");

        await LadderCharacterMigrationService.MigrateAsync(
            new LadderCharacterMigrationRequest(
                source.FullPath,
                _targetSaveDir,
                _backupRootDir,
                null,
                [
                    new LadderCharacterMigrationSelection(
                        refreshedSummoner.Copies[0].SourcePath,
                        refreshedSummoner.Copies[0].SourceSha256,
                        "Summoner",
                        "Summoner",
                        AllowOverwriteExisting: true,
                        ExpectedExistingTargetSha256: refreshedSummoner.ExistingConflict!.Sha256,
                        PairedBindPath: null,
                        CompanionPaths: [])
                ],
                []));

        Assert.False(File.Exists(Path.Combine(_targetSaveDir, "Summoner.ma0")));
        Assert.False(File.Exists(Path.Combine(_targetSaveDir, "Summoner.d2rl")));
        Assert.False(File.Exists(Path.Combine(_targetSaveDir, "Summoner.bind")));
    }

    [Fact]
    public async Task Safeguards_RejectsTruncatedV105_RemovesOrphanedSidecars_BlocksMultiFileConflictOverwrite_AndHashesOversizedTarget()
    {
        Assert.True(LadderCharacterMigrationService.IsReservedWindowsName("CON"));
        Assert.False(LadderCharacterMigrationService.IsReservedWindowsName("Hero"));

        var ladderDir = Path.Combine(_modsSaveRoot, "ReimaginedThree-Hallowing-33445566");
        Directory.CreateDirectory(ladderDir);

        var truncatedPath = Path.Combine(ladderDir, "ShortSave.d2s");
        WriteTruncatedValidChecksumV105Save(truncatedPath, 500);

        WriteNamedV105Save(Path.Combine(ladderDir, "Orphan.d2s"), "Orphan");
        File.WriteAllText(Path.Combine(_targetSaveDir, "Orphan.map"), "stale-map");
        File.WriteAllBytes(Path.Combine(_targetSaveDir, "Orphan.bind"), BuildValidBindBytes(4158));

        WriteNamedV105Save(Path.Combine(ladderDir, "Hero.d2s"), "Hero");
        WriteNamedV105Save(Path.Combine(_targetSaveDir, "Hero.d2s"), "Alt", ResolveFixturePath("FuckBenFuckingF.d2s"));
        WriteNamedV105Save(Path.Combine(_targetSaveDir, "OtherFile.d2s"), "Hero", ResolveFixturePath("Popsicle.d2s"));

        WriteNamedV105Save(Path.Combine(ladderDir, "BigTarget.d2s"), "BigTarget", ResolveFixturePath("Popsicle.d2s"));
        var oversizedTargetBytes = new byte[1024 * 1024 + 512];
        new Random(42).NextBytes(oversizedTargetBytes);
        File.WriteAllBytes(Path.Combine(_targetSaveDir, "BigTarget.d2s"), oversizedTargetBytes);
        var expectedBigSha = Convert.ToHexStringLower(SHA256.HashData(oversizedTargetBytes));

        var inspection = LadderCharacterMigrationService.Inspect(
            LadderToNormalCopyService.DefaultSavePath,
            null,
            _savedGamesDir);
        var source = Assert.Single(inspection.Sources);

        var shortCandidate = Assert.Single(source.Characters, c => c.CharacterName == "ShortSave");
        Assert.False(shortCandidate.IsMigratable);
        Assert.False(string.IsNullOrWhiteSpace(shortCandidate.BlockedReason));

        var heroCandidate = Assert.Single(source.Characters, c => c.CharacterName == "Hero");
        Assert.NotNull(heroCandidate.ExistingConflict);
        Assert.False(heroCandidate.ExistingConflict!.IsFilenameMatch);

        var bigCandidate = Assert.Single(source.Characters, c => c.CharacterName == "BigTarget");
        Assert.NotNull(bigCandidate.ExistingConflict);
        Assert.Equal(expectedBigSha, bigCandidate.ExistingConflict!.Sha256);

        Assert.Throws<InvalidOperationException>(() => LadderCharacterMigrationService.ValidateRequest(
            new LadderCharacterMigrationRequest(
                source.FullPath,
                _targetSaveDir,
                _backupRootDir,
                null,
                [
                    new LadderCharacterMigrationSelection(
                        bigCandidate.Copies[0].SourcePath,
                        bigCandidate.Copies[0].SourceSha256,
                        "BigTarget",
                        "BigTarget",
                        AllowOverwriteExisting: true,
                        ExpectedExistingTargetSha256: string.Empty,
                        null,
                        [])
                ],
                [])));

        var orphanCandidate = Assert.Single(source.Characters, c => c.CharacterName == "Orphan");
        var orphanCopy = Assert.Single(orphanCandidate.Copies);
        var orphanResult = await LadderCharacterMigrationService.MigrateAsync(
            new LadderCharacterMigrationRequest(
                source.FullPath,
                _targetSaveDir,
                _backupRootDir,
                null,
                [
                    new LadderCharacterMigrationSelection(
                        orphanCopy.SourcePath,
                        orphanCopy.SourceSha256,
                        "Orphan",
                        "Orphan",
                        AllowOverwriteExisting: false,
                        ExpectedExistingTargetSha256: null,
                        null,
                        [])
                ],
                []));

        Assert.Equal(1, orphanResult.MigratedCharacterCount);
        Assert.True(File.Exists(Path.Combine(_targetSaveDir, "Orphan.d2s")));
        Assert.False(File.Exists(Path.Combine(_targetSaveDir, "Orphan.map")));
        Assert.False(File.Exists(Path.Combine(_targetSaveDir, "Orphan.bind")));
    }

    [Fact]
    public async Task Safeguards_AllowsAncestorZipBackup_SkipsNestedZipBackup_IgnoresUnrelatedD2i_AndPrunesChronologically()
    {
        var ladderDir = Path.Combine(_modsSaveRoot, "ReimaginedThree-Hallowing-77889900");
        Directory.CreateDirectory(ladderDir);

        WriteNamedV105Save(Path.Combine(ladderDir, "Runner.d2s"), "Runner");
        WriteNamedV105Save(Path.Combine(_targetSaveDir, "Existing.d2s"), "Existing", ResolveFixturePath("Popsicle.d2s"));
        File.WriteAllBytes(Path.Combine(_targetSaveDir, "ExportedItem.d2i"), [1, 2, 3, 4]);

        var validStashBytes = File.ReadAllBytes(ResolveFixturePath("ModernSharedStashSoftCoreV2.d2i"));
        File.WriteAllBytes(Path.Combine(ladderDir, "ModernSharedStashSoftCoreV2.d2i"), validStashBytes);

        var inspection = LadderCharacterMigrationService.Inspect(
            LadderToNormalCopyService.DefaultSavePath,
            null,
            _savedGamesDir);
        var source = Assert.Single(inspection.Sources);
        var runner = Assert.Single(source.Characters, c => c.CharacterName == "Runner").Copies[0];
        var stashOption = Assert.Single(source.SharedStashes);
        Assert.False(stashOption.ExistsInDestination);
        Assert.False(inspection.TargetHasAnySharedStash);

        // Ancestor LauncherBackupSaveDirectory (_savedGamesDir) is allowed and writes secondary zip.
        var ancestorResult = await LadderCharacterMigrationService.MigrateAsync(
            new LadderCharacterMigrationRequest(
                source.FullPath,
                _targetSaveDir,
                _backupRootDir,
                _savedGamesDir,
                [
                    new LadderCharacterMigrationSelection(
                        runner.SourcePath,
                        runner.SourceSha256,
                        "Runner",
                        "Runner",
                        AllowOverwriteExisting: false,
                        ExpectedExistingTargetSha256: null,
                        null,
                        [])
                ],
                [
                    new LadderSharedStashSelection(
                        stashOption.FileName,
                        stashOption.SourcePath,
                        stashOption.SourceSha256)
                ]));
        Assert.NotNull(ancestorResult.LauncherZipBackupPath);
        Assert.True(File.Exists(ancestorResult.LauncherZipBackupPath!));
        Assert.True(File.Exists(Path.Combine(_targetSaveDir, "ModernSharedStashSoftCoreV2.d2i")));

        // Backup folder equal to or nested inside _targetSaveDir skips secondary zip without failing migration.
        var nestedZipBackup = Path.Combine(_targetSaveDir, "nested-zips");
        var totalRuns = LadderCharacterMigrationService.RetainedMigrationBackupCount + 2;
        var orderedBatches = new List<string>();
        for (var i = 0; i < totalRuns; i++)
        {
            var alias = $"Runner{(char)('A' + i)}";
            var res = await LadderCharacterMigrationService.MigrateAsync(
                new LadderCharacterMigrationRequest(
                    source.FullPath,
                    _targetSaveDir,
                    _backupRootDir,
                    nestedZipBackup,
                    [
                        new LadderCharacterMigrationSelection(
                            runner.SourcePath,
                            runner.SourceSha256,
                            "Runner",
                            alias,
                            AllowOverwriteExisting: false,
                            ExpectedExistingTargetSha256: null,
                            null,
                            [])
                    ],
                    []));
            Assert.Null(res.LauncherZipBackupPath);
            Assert.NotNull(res.BackupDirectory);
            orderedBatches.Add(res.BackupDirectory!);
        }

        var remaining = Directory.EnumerateDirectories(_backupRootDir)
            .Select(Path.GetFileName)
            .OrderByDescending(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(LadderCharacterMigrationService.RetainedMigrationBackupCount, remaining.Length);

        var expectedNewest = orderedBatches
            .Skip(totalRuns - LadderCharacterMigrationService.RetainedMigrationBackupCount)
            .Select(Path.GetFileName)
            .OrderByDescending(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedNewest, remaining);
    }

    private static string ResolveFixturePath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var direct = Path.Combine(dir.FullName, "TestFixtures", "SaveMigration", fileName);
            if (File.Exists(direct))
            {
                return direct;
            }

            var nested = Path.Combine(dir.FullName, "ReimaginedLauncher.Tests", "TestFixtures", "SaveMigration", fileName);
            if (File.Exists(nested))
            {
                return nested;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate SaveMigration fixture '{fileName}'.");
    }

    private static void WriteNamedV105Save(string destinationPath, string characterName, string? templatePath = null)
    {
        var bytes = File.ReadAllBytes(templatePath ?? ResolveFixturePath("SoManyBones.d2s"));
        D2SaveLayoutV104.From(bytes).Name = characterName;
        D2SaveLayoutV104.UpdateChecksum(bytes);
        File.WriteAllBytes(destinationPath, bytes);
    }

    private static void WriteCreationStubV105(string destinationPath)
    {
        var bytes = new byte[403];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0xAA55AA55U);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 105U);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), (uint)bytes.Length);
        File.WriteAllBytes(destinationPath, bytes);
    }

    private static void WriteCorruptChecksumSave(string destinationPath, string characterName = "CorruptHero")
    {
        var bytes = File.ReadAllBytes(ResolveFixturePath("SoManyBones.d2s"));
        D2SaveLayoutV104.From(bytes).Name = characterName;
        bytes[12] ^= 0xFF;
        File.WriteAllBytes(destinationPath, bytes);
    }

    private static void WriteTruncatedValidChecksumV105Save(string destinationPath, int length)
    {
        var bytes = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0xAA55AA55U);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 105U);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), (uint)length);

        uint sum = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            sum = unchecked(((sum << 1) | (sum >> 31)) + bytes[i]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), sum);
        Assert.True(ChecksumCalculator.Verify(bytes));
        File.WriteAllBytes(destinationPath, bytes);
    }

    private static byte[] BuildValidBindBytes(int pairedD2sByteLength)
    {
        const int headerBytes = 0x30;
        const int payloadBytes = 16;
        var bytes = new byte[headerBytes + payloadBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x31534442U);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x06, 2), headerBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x08, 4), 1U);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x0C, 4), payloadBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x18, 8), (ulong)pairedD2sByteLength);
        return bytes;
    }

    private static Dictionary<string, string> SnapshotHashes(string root)
    {
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
