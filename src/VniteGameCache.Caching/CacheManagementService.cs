using VniteGameCache.Contracts;
using VniteGameCache.Persistence;

namespace VniteGameCache.Caching;

public sealed record CacheCleanupOptions(long? MaxTotalCacheBytes, long? MinimumFreeSpaceBytes, bool AutoApply = false);

public sealed record CacheCleanupCandidate(
    string GameId,
    string DisplayName,
    string CacheRoot,
    long CacheSizeBytes,
    DateTimeOffset? LastPlayedUtc,
    string Reason,
    long EstimatedReclaimableBytes = 0,
    bool HasUserDataRisk = false);

public sealed record CacheUsageStats(
    long ActiveCacheBytes,
    long QuarantineBytes,
    long StagingBytes,
    long InProgressBytes)
{
    public long TotalManagedBytes => ActiveCacheBytes + QuarantineBytes + StagingBytes + InProgressBytes;
}

public sealed record CacheCleanupPlan(
    CacheUsageStats Usage,
    long CurrentAvailableFreeSpaceBytes,
    long RequiredByCacheLimitBytes,
    long RequiredByFreeSpaceBytes,
    long RequiredReleaseBytes,
    IReadOnlyList<CacheCleanupCandidate> Candidates);

public sealed record LruCleanupItemResult(
    string GameId,
    string DisplayName,
    string CacheRoot,
    string Status,
    long PermanentlyDeletedBytes,
    long ProtectedUserDataBytes,
    string? Message);

public sealed record LruCleanupResult(
    string Status,
    bool AutoApply,
    CacheUsageStats BeforeUsage,
    CacheUsageStats AfterUsage,
    long FreeSpaceBeforeBytes,
    long FreeSpaceAfterBytes,
    long RequiredReleaseBytes,
    long MovedToQuarantineBytes,
    long ProtectedUserDataBytes,
    long PermanentlyDeletedBytes,
    long ActualFreeSpaceIncreaseBytes,
    long EstimatedReclaimableBytes,
    IReadOnlyList<CacheCleanupCandidate> Candidates,
    IReadOnlyList<LruCleanupItemResult> Deleted,
    IReadOnlyList<LruCleanupItemResult> SkippedOrFailed);

public sealed record QuarantineCleanupItemResult(
    string QuarantineId,
    string GameId,
    string QuarantinePath,
    string Status,
    long PermanentlyDeletedBytes,
    string? Message);

public sealed record QuarantineCleanupResult(
    string Status,
    int RetentionDays,
    int ScannedCount,
    int DeletedCount,
    int FailedCount,
    long PermanentlyDeletedBytes,
    long ActualFreeSpaceIncreaseBytes,
    IReadOnlyList<QuarantineCleanupItemResult> Items);

public sealed record CacheClearRequest(string GameId, bool BackupSaves = false, bool KeepUserData = false, bool Force = false, string Reason = "manual_clear");

public sealed record CacheClearResult(
    string Status,
    string GameId,
    bool Cleared,
    bool BlockedByPossibleSaveData,
    IReadOnlyList<string> DetectedUserDataPaths,
    QuarantineEntryRecord? QuarantineEntry);

public sealed record QuarantineRestoreResult(string Status, string QuarantineId, string RestoredPath);

public sealed class CacheManagementService(
    SqliteConnectionFactory factory,
    CacheService cacheService,
    bool protectConfigFiles = false,
    Func<string, long>? availableFreeSpaceProvider = null,
    Action<string>? deleteDirectory = null)
{
    private readonly Func<string, long> availableFreeSpaceProvider = availableFreeSpaceProvider ?? ReadAvailableFreeSpace;
    private readonly Action<string> deleteDirectory = deleteDirectory ?? (path => Directory.Delete(path, recursive: true));

    private static readonly string[] SaveDataPathHints =
    [
        "save",
        "savedata",
        "saves",
        "UserData",
        "www/save"
    ];

    private static readonly string[] ConfigDataPathHints =
    [
        "config",
        "screenshots",
        "screenshot",
        "mods",
        "mod",
        "MTool",
        "MToolScripts",
        "MToolData",
        "MCenter",
        "scripts"
    ];

    public CacheCleanupPlan PlanCleanup(CacheCleanupOptions options)
    {
        var usage = CalculateUsageStats();
        var cacheRoot = FindPrimaryCacheRoot() ?? AppContext.BaseDirectory;
        var availableFreeSpace = availableFreeSpaceProvider(cacheRoot);
        var capacityLimit = options.MaxTotalCacheBytes is null ? 0 : Math.Max(0, options.MaxTotalCacheBytes.Value);
        var minimumFreeSpace = options.MinimumFreeSpaceBytes is null ? 0 : Math.Max(0, options.MinimumFreeSpaceBytes.Value);
        var requiredByCacheLimit = capacityLimit == 0 ? 0 : Math.Max(0, usage.TotalManagedBytes - capacityLimit);
        var requiredByFreeSpace = minimumFreeSpace == 0 ? 0 : Math.Max(0, minimumFreeSpace - availableFreeSpace);
        var requiredRelease = Math.Max(requiredByCacheLimit, requiredByFreeSpace);
        var candidates = BuildCleanupCandidates(requiredRelease);
        return new CacheCleanupPlan(usage, availableFreeSpace, requiredByCacheLimit, requiredByFreeSpace, requiredRelease, candidates);
    }

    public IReadOnlyList<CacheCleanupCandidate> SuggestCleanup(CacheCleanupOptions options) => PlanCleanup(options).Candidates;

    public LruCleanupResult ApplyLruCleanup(CacheCleanupOptions options, int maxItems = int.MaxValue)
    {
        var beforePlan = PlanCleanup(options);
        var freeBefore = beforePlan.CurrentAvailableFreeSpaceBytes;
        if (beforePlan.RequiredReleaseBytes <= 0)
        {
            return new LruCleanupResult(
                "not_needed",
                true,
                beforePlan.Usage,
                beforePlan.Usage,
                freeBefore,
                freeBefore,
                0,
                0,
                0,
                0,
                0,
                beforePlan.Candidates.Sum(candidate => Math.Max(0, candidate.EstimatedReclaimableBytes)),
                beforePlan.Candidates,
                [],
                []);
        }

        var deleted = new List<LruCleanupItemResult>();
        var skipped = new List<LruCleanupItemResult>();
        var movedToQuarantineBytes = 0L;
        var protectedUserDataBytes = 0L;
        var permanentlyDeletedBytes = 0L;
        var gameRepository = new GameRepository(factory);
        var quarantineRepository = new QuarantineRepository(factory);
        var appliedCount = 0;

        foreach (var candidate in beforePlan.Candidates)
        {
            if (appliedCount >= Math.Max(1, maxItems))
            {
                break;
            }

            if (CleanupTargetReached(options))
            {
                break;
            }

            var game = gameRepository.GetById(candidate.GameId);
            if (game is null)
            {
                skipped.Add(candidate.ToItem("game_not_found", 0, 0, null));
                continue;
            }

            var validation = ValidateLruCandidate(game, gameRepository);
            if (validation is not null)
            {
                skipped.Add(candidate.ToItem(validation, 0, 0, null));
                continue;
            }

            try
            {
                var cacheStats = DirectoryStats(game.CacheRoot);
                var protectedPaths = DetectProtectedUserData(game);
                var protectedRoot = protectedPaths.Count == 0
                    ? null
                    : Path.Combine(Path.GetDirectoryName(game.CacheRoot) ?? game.CacheRoot, "Quarantine", $"{game.GameId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-userdata");
                var protectedStats = protectedRoot is null
                    ? (FileCount: 0L, TotalBytes: 0L)
                    : CopyProtectedUserData(game.CacheRoot, protectedRoot, protectedPaths);

                EnsureSafeManagedDirectoryTarget(game.CacheRoot, "cache_root");
                deleteDirectory(game.CacheRoot);
                if (protectedRoot is not null)
                {
                    quarantineRepository.Create(new QuarantineEntryRecord
                    {
                        QuarantineId = $"quarantine-{Guid.NewGuid():N}",
                        GameId = game.GameId,
                        OriginalCacheRoot = game.CacheRoot,
                        QuarantinePath = protectedRoot,
                        Reason = "auto_lru_user_data",
                        SizeBytes = protectedStats.TotalBytes,
                        FileCount = protectedStats.FileCount,
                        ContainsPossibleSaveData = true
                    });
                }

                gameRepository.Update(game with
                {
                    CacheStatus = CacheStatus.NotCached,
                    CacheRevision = 0,
                    CacheSizeBytes = 0,
                    ContainsPossibleSaveData = protectedStats.TotalBytes > 0,
                    PendingManualSaveBackup = protectedStats.TotalBytes > 0,
                    UpdatedUtc = DateTimeOffset.UtcNow
                });

                var deletedBytes = Math.Max(0, cacheStats.TotalBytes - protectedStats.TotalBytes);
                permanentlyDeletedBytes += deletedBytes;
                movedToQuarantineBytes += protectedStats.TotalBytes;
                protectedUserDataBytes += protectedStats.TotalBytes;
                deleted.Add(candidate.ToItem("deleted_game_body", deletedBytes, protectedStats.TotalBytes, null));
                appliedCount++;
                _ = availableFreeSpaceProvider(game.CacheRoot);
            }
            catch (Exception ex)
            {
                skipped.Add(candidate.ToItem("delete_failed", 0, 0, ex.Message));
                break;
            }
        }

        var afterUsage = CalculateUsageStats();
        var freeAfter = availableFreeSpaceProvider(FindPrimaryCacheRoot() ?? AppContext.BaseDirectory);
        var status = deleted.Count > 0
            ? CleanupTargetReached(options) ? "cleaned" : "partial"
            : skipped.Count > 0 ? "blocked" : "not_needed";
        return new LruCleanupResult(
            status,
            true,
            beforePlan.Usage,
            afterUsage,
            freeBefore,
            freeAfter,
            beforePlan.RequiredReleaseBytes,
            movedToQuarantineBytes,
            protectedUserDataBytes,
            permanentlyDeletedBytes,
            Math.Max(0, freeAfter - freeBefore),
            beforePlan.Candidates.Sum(candidate => Math.Max(0, candidate.EstimatedReclaimableBytes)),
            beforePlan.Candidates,
            deleted,
            skipped);
    }

    public QuarantineCleanupResult CleanupExpiredQuarantine(int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return new QuarantineCleanupResult("disabled", retentionDays, 0, 0, 0, 0, 0, []);
        }

        var repository = new QuarantineRepository(factory);
        var expiredBefore = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        var entries = repository.ListActive()
            .Where(entry => entry.CreatedUtc <= expiredBefore)
            .ToList();
        var freeBefore = availableFreeSpaceProvider(FindPrimaryCacheRoot() ?? AppContext.BaseDirectory);
        var items = new List<QuarantineCleanupItemResult>();
        var deletedBytes = 0L;

        foreach (var entry in entries)
        {
            if (HasActiveTask(entry.GameId))
            {
                items.Add(new QuarantineCleanupItemResult(entry.QuarantineId, entry.GameId, entry.QuarantinePath, "active_task", 0, null));
                continue;
            }

            try
            {
                var stats = DirectoryStats(entry.QuarantinePath);
                if (Directory.Exists(entry.QuarantinePath))
                {
                    EnsureSafeManagedDirectoryTarget(entry.QuarantinePath, "quarantine_path");
                    deleteDirectory(entry.QuarantinePath);
                }
                repository.Delete(entry.QuarantineId);
                deletedBytes += stats.TotalBytes;
                items.Add(new QuarantineCleanupItemResult(entry.QuarantineId, entry.GameId, entry.QuarantinePath, "deleted", stats.TotalBytes, null));
            }
            catch (Exception ex)
            {
                items.Add(new QuarantineCleanupItemResult(entry.QuarantineId, entry.GameId, entry.QuarantinePath, "delete_failed_retry", 0, ex.Message));
            }
        }

        var freeAfter = availableFreeSpaceProvider(FindPrimaryCacheRoot() ?? AppContext.BaseDirectory);
        var failedCount = items.Count(item => item.Status != "deleted");
        return new QuarantineCleanupResult(
            failedCount == 0 ? "cleaned" : "partial",
            retentionDays,
            entries.Count,
            items.Count(item => item.Status == "deleted"),
            failedCount,
            deletedBytes,
            Math.Max(0, freeAfter - freeBefore),
            items);
    }

    public Task<CacheResult> QuickRefreshAsync(string gameId, CancellationToken cancellationToken = default) =>
        cacheService.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), gameId, ForceRefresh: true), cancellationToken);

    public async Task<CacheClearResult> CleanRebuildAsync(string gameId, CancellationToken cancellationToken = default)
    {
        var clear = ClearCache(new CacheClearRequest(gameId, Force: false, Reason: "clean_rebuild"));
        if (!clear.Cleared)
        {
            return clear;
        }

        var refreshed = await QuickRefreshAsync(gameId, cancellationToken);
        return refreshed.Status is CacheResultStatus.Success or CacheResultStatus.CacheAlreadyReady
            ? clear
            : clear with { Status = $"rebuild_failed:{refreshed.ErrorCode ?? refreshed.Status.ToString()}" };
    }

    public CacheClearResult ClearCache(CacheClearRequest request)
    {
        var game = new GameRepository(factory).GetById(request.GameId);
        if (game is null)
        {
            return new CacheClearResult("game_not_found", request.GameId, false, false, [], null);
        }

        if (game.CacheStatus is CacheStatus.Copying or CacheStatus.Checking or CacheStatus.Verifying ||
            IsGameActuallyRunning(game, new GameRepository(factory)))
        {
            return new CacheClearResult("cache_in_use", game.GameId, false, false, [], null);
        }

        var detected = DetectUserData(game.CacheRoot, protectConfigFiles);
        var hasPossibleSaveData = game.ContainsPossibleSaveData || game.PendingManualSaveBackup || detected.Count > 0 ||
                                  new SaveProfileRepository(factory).ListByGame(game.GameId).Any(profile => profile.IsEnabled);
        if (hasPossibleSaveData && !request.BackupSaves && !request.KeepUserData && !request.Force)
        {
            return new CacheClearResult("blocked_possible_save_data", game.GameId, false, true, detected, null);
        }

        if (!Directory.Exists(game.CacheRoot))
        {
            return new CacheClearResult("cache_missing", game.GameId, false, hasPossibleSaveData, detected, null);
        }

        EnsureSafeManagedDirectoryTarget(game.CacheRoot, "cache_root");
        var stats = DirectoryStats(game.CacheRoot);
        var quarantineRoot = Path.Combine(Path.GetDirectoryName(game.CacheRoot) ?? game.CacheRoot, "Quarantine");
        Directory.CreateDirectory(quarantineRoot);
        var quarantinePath = Path.Combine(quarantineRoot, $"{game.GameId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
        Directory.Move(game.CacheRoot, quarantinePath);

        var entry = new QuarantineEntryRecord
        {
            QuarantineId = $"quarantine-{Guid.NewGuid():N}",
            GameId = game.GameId,
            OriginalCacheRoot = game.CacheRoot,
            QuarantinePath = quarantinePath,
            Reason = request.Reason,
            SizeBytes = stats.TotalBytes,
            FileCount = stats.FileCount,
            ContainsPossibleSaveData = hasPossibleSaveData
        };
        new QuarantineRepository(factory).Create(entry);
        new GameRepository(factory).Update(game with
        {
            CacheStatus = CacheStatus.NotCached,
            CacheRevision = 0,
            CacheSizeBytes = 0,
            ContainsPossibleSaveData = hasPossibleSaveData,
            UpdatedUtc = DateTimeOffset.UtcNow
        });
        return new CacheClearResult("cleared_to_quarantine", game.GameId, true, hasPossibleSaveData, detected, entry);
    }

    public QuarantineRestoreResult RestoreQuarantine(string quarantineId)
    {
        var repository = new QuarantineRepository(factory);
        var entry = repository.GetById(quarantineId) ?? throw new CacheException(CacheResultStatus.InvalidCachePath, "quarantine_not_found", $"Quarantine entry not found: {quarantineId}");
        if (!Directory.Exists(entry.QuarantinePath))
        {
            throw new CacheException(CacheResultStatus.InvalidCachePath, "quarantine_path_missing", $"Quarantine path missing: {entry.QuarantinePath}");
        }

        if (Directory.Exists(entry.OriginalCacheRoot) && Directory.EnumerateFileSystemEntries(entry.OriginalCacheRoot).Any())
        {
            throw new CacheException(CacheResultStatus.InvalidCachePath, "cache_target_not_empty", $"Cache target is not empty: {entry.OriginalCacheRoot}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(entry.OriginalCacheRoot) ?? ".");
        if (Directory.Exists(entry.OriginalCacheRoot))
        {
            Directory.Delete(entry.OriginalCacheRoot);
        }

        Directory.Move(entry.QuarantinePath, entry.OriginalCacheRoot);
        repository.MarkRestored(entry.QuarantineId, DateTimeOffset.UtcNow);
        var gameRepository = new GameRepository(factory);
        var game = gameRepository.GetById(entry.GameId);
        if (game is not null)
        {
            gameRepository.Update(game with
            {
                CacheStatus = CacheStatus.Incomplete,
                CacheSizeBytes = entry.SizeBytes,
                ContainsPossibleSaveData = entry.ContainsPossibleSaveData,
                UpdatedUtc = DateTimeOffset.UtcNow
            });
        }

        return new QuarantineRestoreResult("restored", entry.QuarantineId, entry.OriginalCacheRoot);
    }

    private IReadOnlyList<CacheCleanupCandidate> BuildCleanupCandidates(long requiredReleaseBytes)
    {
        var repository = new GameRepository(factory);
        return repository.List()
            .Where(game => game.CacheStatus == CacheStatus.Ready)
            .Where(game => !game.IsPinned)
            .Where(game => !HasActiveTask(game.GameId))
            .Where(game => !IsGameActuallyRunning(game, repository))
            .Where(game => Directory.Exists(game.CacheRoot))
            .Where(game => IsSafeManagedDirectoryTarget(game.CacheRoot))
            .Where(game => Directory.Exists(game.SourceRoot))
            .Where(game => new FileEntryRepository(factory).ListByGame(game.GameId).Count > 0)
            .Select(game =>
            {
                var hasRisk = DetectProtectedUserData(game).Count > 0;
                var reason = requiredReleaseBytes > 0 ? "cleanup_required" : "manual_cleanup_review";
                return new CacheCleanupCandidate(
                    game.GameId,
                    game.DisplayName,
                    game.CacheRoot,
                    game.CacheSizeBytes,
                    game.LastPlayedUtc,
                    reason,
                    Math.Max(0, DirectoryStats(game.CacheRoot).TotalBytes),
                    hasRisk);
            })
            .OrderBy(candidate => candidate.LastPlayedUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(candidate => candidate.CacheSizeBytes)
            .ThenBy(candidate => candidate.HasUserDataRisk)
            .ToList();
    }

    private string? ValidateLruCandidate(GameRecord game, GameRepository repository)
    {
        if (game.CacheStatus is CacheStatus.Copying or CacheStatus.Checking or CacheStatus.Verifying or CacheStatus.Running) return "cache_in_use";
        if (game.IsPinned) return "pinned";
        if (!Directory.Exists(game.SourceRoot)) return "source_unavailable";
        if (!Directory.Exists(game.CacheRoot)) return "cache_missing";
        if (HasActiveTask(game.GameId)) return "active_task";
        if (new FileEntryRepository(factory).ListByGame(game.GameId).Count == 0) return "missing_file_index";
        return IsGameActuallyRunning(game, repository) ? "cache_in_use" : null;
    }

    private bool CleanupTargetReached(CacheCleanupOptions options)
    {
        var plan = PlanCleanup(options);
        return plan.RequiredReleaseBytes <= 0;
    }

    private CacheUsageStats CalculateUsageStats()
    {
        var activeCacheBytes = 0L;
        var inProgressBytes = 0L;
        var stagingBytes = 0L;
        var seenStagingRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in new GameRepository(factory).List())
        {
            if (Directory.Exists(game.CacheRoot) && IsSafeManagedDirectoryTarget(game.CacheRoot))
            {
                var size = DirectoryStats(game.CacheRoot).TotalBytes;
                if (game.CacheStatus is CacheStatus.Copying or CacheStatus.Verifying or CacheStatus.Checking)
                {
                    inProgressBytes += size;
                }
                else
                {
                    activeCacheBytes += size;
                }
            }

            var parent = Path.GetDirectoryName(game.CacheRoot);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                seenStagingRoots.Add(Path.Combine(parent, "Staging"));
                seenStagingRoots.Add(Path.Combine(parent, ".staging"));
            }
        }

        foreach (var stagingRoot in seenStagingRoots.Where(Directory.Exists))
        {
            stagingBytes += DirectoryStats(stagingRoot).TotalBytes;
        }

        var quarantineBytes = new QuarantineRepository(factory).ListActive()
            .Where(entry => Directory.Exists(entry.QuarantinePath))
            .Where(entry => IsSafeManagedDirectoryTarget(entry.QuarantinePath))
            .Sum(entry => DirectoryStats(entry.QuarantinePath).TotalBytes);
        return new CacheUsageStats(activeCacheBytes, quarantineBytes, stagingBytes, inProgressBytes);
    }

    private IReadOnlyList<string> DetectProtectedUserData(GameRecord game)
    {
        var paths = new List<string>();
        paths.AddRange(DetectUserData(game.CacheRoot, protectConfigFiles));
        foreach (var profile in new SaveProfileRepository(factory).ListByGame(game.GameId).Where(profile => profile.IsEnabled))
        {
            if ((Directory.Exists(profile.ResolvedPath) || File.Exists(profile.ResolvedPath)) &&
                IsSubPathOf(profile.ResolvedPath, game.CacheRoot))
            {
                paths.Add(profile.ResolvedPath);
            }
        }

        paths.AddRange(DetectManifestExternalPaths(game));
        return CollapseNestedPaths(paths);
    }

    private IReadOnlyList<string> DetectManifestExternalPaths(GameRecord game)
    {
        if (!Directory.Exists(game.CacheRoot)) return [];
        var manifestPaths = new HashSet<string>(
            new FileEntryRepository(factory).ListByGame(game.GameId)
                .Select(entry => NormalizeRelativePath(entry.RelativePath)),
            StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var file in EnumerateFilesWithoutReparsePoints(game.CacheRoot))
        {
            var relative = NormalizeRelativePath(Path.GetRelativePath(game.CacheRoot, file));
            if (!manifestPaths.Contains(relative))
            {
                result.Add(file);
            }
        }

        return result;
    }

    private static (long FileCount, long TotalBytes) CopyProtectedUserData(string cacheRoot, string protectedRoot, IReadOnlyList<string> protectedPaths)
    {
        Directory.CreateDirectory(protectedRoot);
        foreach (var sourcePath in protectedPaths)
        {
            var relativePath = Path.GetRelativePath(cacheRoot, sourcePath);
            if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
            {
                throw new CacheException(CacheResultStatus.InvalidCachePath, "protected_path_outside_cache", $"Protected path is outside cache root: {sourcePath}");
            }

            var destinationPath = Path.Combine(protectedRoot, relativePath);
            if (Directory.Exists(sourcePath))
            {
                if (IsReparsePoint(sourcePath))
                {
                    throw new CacheException(CacheResultStatus.InvalidCachePath, "protected_path_reparse_point", $"Protected path is a reparse point: {sourcePath}");
                }
                CopyDirectory(sourcePath, destinationPath);
            }
            else if (File.Exists(sourcePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }
        }

        return DirectoryStats(protectedRoot);
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var directory in EnumerateDirectoriesWithoutReparsePoints(sourceRoot))
        {
            Directory.CreateDirectory(Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, directory)));
        }

        foreach (var file in EnumerateFilesWithoutReparsePoints(sourceRoot))
        {
            var destination = Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static IReadOnlyList<string> DetectUserData(string cacheRoot, bool includeConfigData)
    {
        if (!Directory.Exists(cacheRoot)) return [];
        var detected = new List<string>();
        foreach (var hint in SaveDataPathHints.Concat(includeConfigData ? ConfigDataPathHints : []))
        {
            var path = Path.Combine(cacheRoot, hint.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(path) || File.Exists(path))
            {
                detected.Add(path);
            }
        }

        return detected;
    }

    private static IReadOnlyList<string> CollapseNestedPaths(IEnumerable<string> paths)
    {
        var ordered = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length)
            .ToList();
        var result = new List<string>();
        foreach (var path in ordered)
        {
            if (!result.Any(parent => IsSubPathOf(path, parent) || path.Equals(parent, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(path);
            }
        }

        return result;
    }

    private static bool IsSubPathOf(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private bool HasActiveTask(string gameId) =>
        new TaskRepository(factory).ListByGame(gameId)
            .Any(task => task.State is TaskState.Checking or TaskState.Copying or TaskState.Verifying or TaskState.Cleaning or TaskState.Running);

    private static bool IsGameActuallyRunning(GameRecord game, GameRepository repository)
    {
        if (RuntimeGameLock.IsHeld(game.GameId))
        {
            return true;
        }

        if (game.CacheStatus != CacheStatus.Running)
        {
            return false;
        }

        var repairedStatus = Directory.Exists(game.CacheRoot) ? CacheStatus.Ready : CacheStatus.NotCached;
        repository.Update(game with
        {
            CacheStatus = repairedStatus,
            UpdatedUtc = DateTimeOffset.UtcNow
        });
        return false;
    }

    private string? FindPrimaryCacheRoot() =>
        new GameRepository(factory).List()
            .Select(game => Directory.Exists(game.CacheRoot) ? game.CacheRoot : Path.GetDirectoryName(game.CacheRoot))
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

    private static long ReadAvailableFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrWhiteSpace(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return 0;
        }
    }

    private static (long FileCount, long TotalBytes) DirectoryStats(string root)
    {
        long fileCount = 0;
        long totalBytes = 0;
        if (!Directory.Exists(root)) return (0, 0);
        foreach (var file in EnumerateFilesWithoutReparsePoints(root))
        {
            var info = new FileInfo(file);
            fileCount++;
            totalBytes += info.Length;
        }

        return (fileCount, totalBytes);
    }

    private static IEnumerable<string> EnumerateFilesWithoutReparsePoints(string root)
    {
        if (!Directory.Exists(root) || IsReparsePoint(root)) yield break;
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.Count > 0)
        {
            var current = directories.Pop();
            string[] childDirectories;
            string[] files;
            try
            {
                childDirectories = Directory.EnumerateDirectories(current).ToArray();
                files = Directory.EnumerateFiles(current).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!IsReparsePoint(file))
                {
                    yield return file;
                }
            }

            foreach (var directory in childDirectories)
            {
                if (!IsReparsePoint(directory))
                {
                    directories.Push(directory);
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesWithoutReparsePoints(string root)
    {
        if (!Directory.Exists(root) || IsReparsePoint(root)) yield break;
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.Count > 0)
        {
            string[] childDirectories;
            try
            {
                childDirectories = Directory.EnumerateDirectories(directories.Pop()).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var directory in childDirectories)
            {
                if (IsReparsePoint(directory)) continue;
                yield return directory;
                directories.Push(directory);
            }
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static void EnsureSafeManagedDirectoryTarget(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new CacheException(CacheResultStatus.InvalidCachePath, $"{label}_empty", $"{label} is empty.");
        }

        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(root) || fullPath.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new CacheException(CacheResultStatus.InvalidCachePath, $"{label}_is_drive_root", $"{label} must not be a drive or share root: {path}");
        }

        if (IsReparsePoint(fullPath))
        {
            throw new CacheException(CacheResultStatus.InvalidCachePath, $"{label}_reparse_point", $"{label} must not be a reparse point: {path}");
        }
    }

    private static bool IsSafeManagedDirectoryTarget(string path)
    {
        try
        {
            EnsureSafeManagedDirectoryTarget(path, "path");
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal static class CacheCleanupCandidateExtensions
{
    public static LruCleanupItemResult ToItem(this CacheCleanupCandidate candidate, string status, long permanentlyDeletedBytes, long protectedUserDataBytes, string? message) =>
        new(candidate.GameId, candidate.DisplayName, candidate.CacheRoot, status, permanentlyDeletedBytes, protectedUserDataBytes, message);
}


