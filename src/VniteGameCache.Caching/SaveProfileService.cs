using System.IO.Compression;
using VniteGameCache.Contracts;
using VniteGameCache.Persistence;

namespace VniteGameCache.Caching;

public sealed record SaveProfileCreateRequest(
    string GameId,
    string DisplayName,
    SaveProfileSourceType SourceType,
    string PathTemplate,
    string? BackupRoot = null,
    bool IsPrimary = false);

public sealed record SaveProfileValidation(
    string SaveProfileId,
    string ResolvedPath,
    bool Exists,
    bool IsDirectory,
    bool CanRead,
    bool CanWrite,
    long FileCount,
    long TotalBytes,
    DateTimeOffset? LatestModifiedUtc);

public sealed record SaveBackupResult(string BackupId, string BackupPath, long FileCount, long TotalBytes);

public sealed record SaveImportResult(string Status, SaveBackupResult PreImportBackup, long ImportedFileCount);

public sealed record SaveRestoreResult(string Status, SaveBackupResult PreRestoreBackup, string RestoredFrom);

public sealed class SaveProfileService(SqliteConnectionFactory factory)
{
    public SaveProfileRecord AddOrUpdate(SaveProfileCreateRequest request)
    {
        var game = new GameRepository(factory).GetById(request.GameId) ?? throw new CacheException(CacheResultStatus.GameNotFound, "game_not_found", $"Game not found: {request.GameId}");
        var resolved = ResolvePath(game, request.SourceType, request.PathTemplate);
        var backupRoot = request.BackupRoot;
        if (string.IsNullOrWhiteSpace(backupRoot))
        {
            backupRoot = VgcSettingsStore.ResolveDefaultSaveBackupRoot(game.GameId);
        }

        var repository = new SaveProfileRepository(factory);
        var existing = repository.ListByGame(game.GameId)
            .FirstOrDefault(profile =>
                profile.SourceType == request.SourceType &&
                string.Equals(Path.GetFullPath(profile.ResolvedPath), resolved, StringComparison.OrdinalIgnoreCase));
        var profile = new SaveProfileRecord
        {
            SaveProfileId = existing?.SaveProfileId ?? $"save-profile-{Guid.NewGuid():N}",
            GameId = game.GameId,
            DisplayName = request.DisplayName,
            SourceType = request.SourceType,
            PathTemplate = request.PathTemplate,
            ResolvedPath = resolved,
            BackupRoot = backupRoot,
            LastBackupUtc = existing?.LastBackupUtc,
            LastRestoreUtc = existing?.LastRestoreUtc,
            IsPrimary = request.IsPrimary,
            IsEnabled = existing?.IsEnabled ?? true,
            CreatedUtc = existing?.CreatedUtc ?? DateTimeOffset.UtcNow
        };
        repository.Upsert(profile);
        return profile;
    }

    public IReadOnlyList<SaveProfileRecord> List(string gameId) => new SaveProfileRepository(factory).ListByGame(gameId);

    public SaveProfileValidation Validate(string saveProfileId)
    {
        var profile = GetProfile(saveProfileId);
        var exists = Directory.Exists(profile.ResolvedPath) || File.Exists(profile.ResolvedPath);
        var isDirectory = Directory.Exists(profile.ResolvedPath);
        var canRead = false;
        var canWrite = false;
        long fileCount = 0;
        long totalBytes = 0;
        DateTimeOffset? latest = null;

        try
        {
            if (isDirectory)
            {
                foreach (var file in Directory.EnumerateFiles(profile.ResolvedPath, "*", SearchOption.AllDirectories))
                {
                    var info = new FileInfo(file);
                    fileCount++;
                    totalBytes += info.Length;
                    if (latest is null || info.LastWriteTimeUtc > latest.Value.UtcDateTime)
                    {
                        latest = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                    }
                }
            }
            else if (File.Exists(profile.ResolvedPath))
            {
                var info = new FileInfo(profile.ResolvedPath);
                fileCount = 1;
                totalBytes = info.Length;
                latest = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            }

            canRead = exists;
        }
        catch (UnauthorizedAccessException)
        {
            canRead = false;
        }

        try
        {
            var writeTestDirectory = isDirectory ? profile.ResolvedPath : Path.GetDirectoryName(profile.ResolvedPath);
            if (!string.IsNullOrWhiteSpace(writeTestDirectory) && Directory.Exists(writeTestDirectory))
            {
                var testPath = Path.Combine(writeTestDirectory, $".vgc-write-test-{Guid.NewGuid():N}");
                File.WriteAllText(testPath, string.Empty);
                File.Delete(testPath);
                canWrite = true;
            }
        }
        catch
        {
            canWrite = false;
        }

        return new SaveProfileValidation(profile.SaveProfileId, profile.ResolvedPath, exists, isDirectory, canRead, canWrite, fileCount, totalBytes, latest);
    }

    public SaveBackupResult Backup(string saveProfileId, SaveBackupType backupType = SaveBackupType.Manual, string? note = null)
    {
        var profile = GetProfile(saveProfileId);
        Directory.CreateDirectory(profile.BackupRoot);
        var backupId = $"save-backup-{Guid.NewGuid():N}";
        var path = Path.Combine(profile.BackupRoot, $"{SafeName(profile.DisplayName)}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_{backupId}.zip");
        var stats = CreateZipFromPath(profile.ResolvedPath, path);
        var backup = new SaveBackupRecord
        {
            BackupId = backupId,
            SaveProfileId = profile.SaveProfileId,
            GameId = profile.GameId,
            BackupPath = path,
            BackupType = backupType,
            SourcePath = profile.ResolvedPath,
            FileCount = stats.FileCount,
            TotalBytes = stats.TotalBytes,
            Note = note
        };
        new SaveBackupRepository(factory).Create(backup);
        new SaveProfileRepository(factory).UpdateBackupUtc(profile.SaveProfileId, DateTimeOffset.UtcNow);
        return new SaveBackupResult(backup.BackupId, backup.BackupPath, backup.FileCount, backup.TotalBytes);
    }

    public SaveBackupResult Export(string saveProfileId, string outputZipPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputZipPath) ?? ".");
        var profile = GetProfile(saveProfileId);
        var stats = CreateZipFromPath(profile.ResolvedPath, outputZipPath);
        return new SaveBackupResult("export", outputZipPath, stats.FileCount, stats.TotalBytes);
    }

    public SaveImportResult Import(string saveProfileId, string inputPath)
    {
        var profile = GetProfile(saveProfileId);
        var backup = Backup(saveProfileId, SaveBackupType.BeforeImport, "Automatic backup before manual import.");
        var imported = ImportInto(profile.ResolvedPath, inputPath);
        return new SaveImportResult("imported", backup, imported);
    }

    public SaveRestoreResult Restore(string backupId)
    {
        var backupRepository = new SaveBackupRepository(factory);
        var backup = backupRepository.GetById(backupId) ?? throw new CacheException(CacheResultStatus.InvalidCachePath, "backup_not_found", $"Backup not found: {backupId}");
        var profile = GetProfile(backup.SaveProfileId);
        var preRestore = Backup(profile.SaveProfileId, SaveBackupType.BeforeRestore, $"Automatic backup before restoring {backupId}.");
        ImportInto(profile.ResolvedPath, backup.BackupPath);
        new SaveProfileRepository(factory).UpdateRestoreUtc(profile.SaveProfileId, DateTimeOffset.UtcNow);
        return new SaveRestoreResult("restored", preRestore, backup.BackupPath);
    }

    private SaveProfileRecord GetProfile(string saveProfileId) =>
        new SaveProfileRepository(factory).GetById(saveProfileId) ?? throw new CacheException(CacheResultStatus.InvalidCachePath, "save_profile_not_found", $"Save Profile not found: {saveProfileId}");

    private static string ResolvePath(GameRecord game, SaveProfileSourceType sourceType, string template)
    {
        var root = sourceType switch
        {
            SaveProfileSourceType.GameRelative => game.CacheRoot,
            SaveProfileSourceType.AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            SaveProfileSourceType.LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SaveProfileSourceType.Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            SaveProfileSourceType.SavedGames => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"),
            SaveProfileSourceType.Absolute => string.Empty,
            _ => string.Empty
        };

        var resolved = sourceType == SaveProfileSourceType.Absolute ? Path.GetFullPath(template) : Path.GetFullPath(Path.Combine(root, template));
        if (sourceType != SaveProfileSourceType.Absolute && !IsSubPathOrSame(resolved, root))
        {
            throw new CacheException(CacheResultStatus.InvalidCachePath, "save_profile_path_escape", "Save Profile path_template escapes its configured root.");
        }

        return resolved;
    }

    private static bool IsSubPathOrSame(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static (long FileCount, long TotalBytes) CreateZipFromPath(string sourcePath, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        long fileCount = 0;
        long totalBytes = 0;
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        if (Directory.Exists(sourcePath))
        {
            foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourcePath, file);
                archive.CreateEntryFromFile(file, relative, CompressionLevel.Fastest);
                var info = new FileInfo(file);
                fileCount++;
                totalBytes += info.Length;
            }
        }
        else if (File.Exists(sourcePath))
        {
            archive.CreateEntryFromFile(sourcePath, Path.GetFileName(sourcePath), CompressionLevel.Fastest);
            var info = new FileInfo(sourcePath);
            fileCount = 1;
            totalBytes = info.Length;
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath) ?? ".");
        }

        return (fileCount, totalBytes);
    }

    private static long ImportInto(string targetPath, string inputPath)
    {
        Directory.CreateDirectory(targetPath);
        if (File.Exists(inputPath) && Path.GetExtension(inputPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var count = 0L;
            using var archive = ZipFile.OpenRead(inputPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                var destination = Path.GetFullPath(Path.Combine(targetPath, entry.FullName));
                var root = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CacheException(CacheResultStatus.InvalidCachePath, "zip_path_escape", $"ZIP entry escapes target path: {entry.FullName}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? targetPath);
                entry.ExtractToFile(destination, overwrite: true);
                count++;
            }

            return count;
        }

        if (Directory.Exists(inputPath))
        {
            var count = 0L;
            foreach (var file in Directory.EnumerateFiles(inputPath, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(inputPath, file);
                var destination = Path.Combine(targetPath, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? targetPath);
                File.Copy(file, destination, overwrite: true);
                count++;
            }

            return count;
        }

        throw new CacheException(CacheResultStatus.InvalidCachePath, "import_path_missing", $"Import path missing: {inputPath}");
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars).Trim();
    }
}
