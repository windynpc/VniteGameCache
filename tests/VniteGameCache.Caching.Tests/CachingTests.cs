using VniteGameCache.Caching;
using VniteGameCache.Contracts;
using VniteGameCache.Indexing;
using VniteGameCache.Persistence;

namespace VniteGameCache.Caching.Tests;

public sealed class CachingTests
{
    [Fact]
    public async Task First_cache_copy_updates_cache_revision_and_ready_status()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe", "data.bin");
        fixture.CreateGameAndIndex();

        var result = await fixture.Cache.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), "game-1"));

        Assert.Equal(CacheResultStatus.Success, result.Status);
        Assert.True(result.RobocopyCalled);
        Assert.Equal(1, fixture.Runner.CallCount);
        Assert.True(File.Exists(Path.Combine(fixture.CacheRoot, "Game.exe")));
        var game = fixture.Games.GetById("game-1")!;
        Assert.Equal(1, game.CacheRevision);
        Assert.Equal(CacheStatus.Ready, game.CacheStatus);
    }

    [Fact]
    public async Task Ready_matching_revision_skips_robocopy()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe", "data.bin");
        fixture.CreateGameAndIndex();
        await fixture.Cache.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), "game-1"));
        fixture.Runner.Reset();

        var result = await fixture.Cache.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), "game-1"));

        Assert.Equal(CacheResultStatus.CacheAlreadyReady, result.Status);
        Assert.False(result.RobocopyCalled);
        Assert.Equal(0, fixture.Runner.CallCount);
    }

    [Fact]
    public async Task Completed_cache_writes_ready_marker_for_fast_repeat_launch()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe", "data.bin");
        fixture.CreateGameAndIndex();

        await fixture.Cache.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), "game-1"));

        Assert.True(File.Exists(Path.Combine(fixture.CacheRoot, ".vgc-cache-ready.json")));
        fixture.Runner.Reset();
        var result = await fixture.Cache.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), "game-1"));
        Assert.Equal(CacheResultStatus.CacheAlreadyReady, result.Status);
        Assert.Equal(0, fixture.Runner.CallCount);
    }

    [Fact]
    public async Task Cache_copy_persists_streaming_byte_progress_and_snapshot()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe", "data.bin");
        fixture.CreateGameAndIndex();

        var result = await fixture.Cache.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), "game-1"));

        Assert.Equal(CacheResultStatus.Success, result.Status);
        var task = new TaskRepository(fixture.Factory).ListByGame("game-1")
            .Single(record => record.TaskType == "PrepareCache");
        Assert.Equal(TaskState.Completed, task.State);
        Assert.True(task.ProgressTotal > 0);
        Assert.Equal(task.ProgressTotal, task.ProgressCurrent);
        var snapshot = Directory.EnumerateFiles(Path.Combine(fixture.Root, "logs"), "*.progress.json", SearchOption.AllDirectories).Single();
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(snapshot));
        Assert.Equal("Completed", document.RootElement.GetProperty("phase").GetString());
    }

    [Fact]
    public async Task Missing_index_uses_full_robocopy_and_launches_from_cache()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe", "data.bin");
        fixture.CreateGameOnly();

        var result = await fixture.Cache.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), "game-1"));

        Assert.Equal(CacheResultStatus.Success, result.Status);
        Assert.True(result.RobocopyCalled);
        Assert.Equal(1, fixture.Runner.CallCount);
        Assert.Equal(Path.Combine(fixture.CacheRoot, "Game.exe"), result.LaunchExecutablePath);
        Assert.True(File.Exists(Path.Combine(fixture.CacheRoot, "data.bin")));
    }

    [Fact]
    public async Task Fast_verification_ignores_mutable_logs_and_saves()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe", "debug.log", "save/file1.rmmzsave");
        fixture.CreateGameAndIndex();
        await fixture.Cache.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), "game-1"));
        File.WriteAllText(Path.Combine(fixture.CacheRoot, "debug.log"), "changed");
        File.WriteAllText(Path.Combine(fixture.CacheRoot, "save", "file1.rmmzsave"), "changed");
        fixture.Runner.Reset();

        var result = await fixture.Cache.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), "game-1"));

        Assert.Equal(CacheResultStatus.CacheAlreadyReady, result.Status);
        Assert.False(result.RobocopyCalled);
    }

    [Fact]
    public async Task Launch_working_directory_maps_source_subdirectory_to_cache_subdirectory()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("bin/Game.exe", "bin/data.bin");
        fixture.CreateGameAndIndex("bin/Game.exe", "bin");

        var result = await fixture.Cache.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), "game-1"));

        Assert.Equal(CacheResultStatus.Success, result.Status);
        Assert.Equal(Path.Combine(fixture.CacheRoot, "bin"), result.LaunchWorkingDirectory);
    }

    [Fact]
    public async Task Force_refresh_calls_robocopy_even_when_ready()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe");
        fixture.CreateGameAndIndex();
        await fixture.Cache.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), "game-1"));
        fixture.Runner.Reset();

        var result = await fixture.Cache.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), "game-1", ForceRefresh: true));

        Assert.Equal(CacheResultStatus.Success, result.Status);
        Assert.True(result.RobocopyCalled);
        Assert.Equal(1, fixture.Runner.CallCount);
    }

    [Fact]
    public async Task Source_revision_ahead_triggers_incremental_robocopy()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe");
        fixture.CreateGameAndIndex();
        await fixture.Cache.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), "game-1"));
        fixture.CreateSource("patch.dat");
        fixture.IndexAgain();
        fixture.Runner.Reset();

        var result = await fixture.Cache.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), "game-1"));

        Assert.Equal(CacheResultStatus.Success, result.Status);
        Assert.True(result.RobocopyCalled);
        Assert.Equal(1, fixture.Runner.CallCount);
        Assert.Equal(2, fixture.Games.GetById("game-1")!.CacheRevision);
        Assert.True(File.Exists(Path.Combine(fixture.CacheRoot, "patch.dat")));
    }

    [Fact]
    public async Task Robocopy_failure_does_not_advance_cache_revision()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe");
        fixture.CreateGameAndIndex();
        fixture.Runner.NextExitCode = 8;

        var result = await fixture.Cache.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), "game-1"));

        Assert.Equal(CacheResultStatus.RobocopyFailed, result.Status);
        var game = fixture.Games.GetById("game-1")!;
        Assert.Equal(0, game.CacheRevision);
        Assert.Equal(CacheStatus.Failed, game.CacheStatus);
    }

    [Fact]
    public async Task Robocopy_failure_with_complete_cache_is_accepted_after_fast_verification()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe", "d3dcompiler_47.dll");
        fixture.CreateGameAndIndex();
        fixture.Runner.CopyBeforeReturningFailure = true;
        fixture.Runner.NextExitCode = 8;

        var result = await fixture.Cache.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), "game-1"));

        Assert.Equal(CacheResultStatus.Success, result.Status);
        Assert.Equal(8, result.RobocopyExitCode);
        var game = fixture.Games.GetById("game-1")!;
        Assert.Equal(1, game.CacheRevision);
        Assert.Equal(CacheStatus.Ready, game.CacheStatus);
        Assert.True(File.Exists(Path.Combine(fixture.CacheRoot, "d3dcompiler_47.dll")));
    }

    [Fact]
    public async Task Cancelled_copy_marks_incomplete_and_next_run_can_complete()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe", "data.bin");
        fixture.CreateGameAndIndex();
        fixture.Runner.CancelNext = true;

        var cancelled = await fixture.Cache.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), "game-1"));

        Assert.Equal(CacheResultStatus.Cancelled, cancelled.Status);
        Assert.Equal(CacheStatus.Incomplete, fixture.Games.GetById("game-1")!.CacheStatus);
        Assert.Equal(0, fixture.Games.GetById("game-1")!.CacheRevision);

        var completed = await fixture.Cache.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), "game-1"));

        Assert.Equal(CacheResultStatus.Success, completed.Status);
        Assert.Equal(1, fixture.Games.GetById("game-1")!.CacheRevision);
        Assert.True(File.Exists(Path.Combine(fixture.CacheRoot, "data.bin")));
    }

    [Fact]
    public void Path_safety_rejects_overlap_and_dangerous_roots()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe");

        Assert.Throws<CacheException>(() => CachePathSafety.Validate(fixture.SourceRoot, fixture.SourceRoot));
        Assert.Throws<CacheException>(() => CachePathSafety.Validate(fixture.SourceRoot, Path.Combine(fixture.SourceRoot, "cache")));
        Assert.Throws<CacheException>(() => CachePathSafety.Validate(fixture.SourceRoot, Path.GetPathRoot(fixture.SourceRoot)!));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(7, true)]
    [InlineData(8, false)]
    [InlineData(16, false)]
    public void Robocopy_exit_codes_follow_official_success_range(int exitCode, bool success)
    {
        Assert.Equal(success, RobocopyExitCodes.IsSuccess(exitCode));
    }

    [Fact]
    public void Lru_suggestions_skip_pinned_running_and_unbacked_save_risks()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe");
        fixture.CreateGameAndIndex();
        fixture.Games.Upsert(new GameRecord
        {
            GameId = "game-pinned",
            DisplayName = "Pinned",
            SourceRoot = fixture.SourceRoot,
            CacheRoot = Path.Combine(fixture.Root, "pinned-cache"),
            ExecutablePath = Path.Combine(fixture.SourceRoot, "Game.exe"),
            WorkingDirectory = fixture.SourceRoot,
            CacheStatus = CacheStatus.Ready,
            CacheSizeBytes = 100,
            IsPinned = true
        });
        fixture.Games.Upsert(fixture.Games.GetById("game-1")! with
        {
            CacheStatus = CacheStatus.Ready,
            CacheSizeBytes = 200,
            LastPlayedUtc = DateTimeOffset.UtcNow.AddDays(-10)
        });
        Directory.CreateDirectory(fixture.CacheRoot);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "pinned-cache"));

        var candidates = fixture.Management.SuggestCleanup(new CacheCleanupOptions(MaxTotalCacheBytes: 50, MinimumFreeSpaceBytes: null));

        Assert.Single(candidates);
        Assert.Equal("game-1", candidates[0].GameId);
    }

    [Fact]
    public async Task Quick_refresh_preserves_extra_cache_files()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe");
        fixture.CreateGameAndIndex();
        await fixture.Cache.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), "game-1"));
        var extra = Path.Combine(fixture.CacheRoot, "user-extra.txt");
        File.WriteAllText(extra, "keep");

        fixture.CreateSource("patch.dat");
        fixture.IndexAgain();
        var result = await fixture.Management.QuickRefreshAsync("game-1");

        Assert.Equal(CacheResultStatus.Success, result.Status);
        Assert.True(File.Exists(extra));
        Assert.True(File.Exists(Path.Combine(fixture.CacheRoot, "patch.dat")));
    }

    [Fact]
    public void Safe_clear_blocks_possible_save_data_then_quarantines_when_forced()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe");
        fixture.CreateGameAndIndex();
        Directory.CreateDirectory(fixture.CacheRoot);
        Directory.CreateDirectory(Path.Combine(fixture.CacheRoot, "save"));
        File.WriteAllText(Path.Combine(fixture.CacheRoot, "save", "slot1.sav"), "save");

        var blocked = fixture.Management.ClearCache(new CacheClearRequest("game-1"));
        Assert.False(blocked.Cleared);
        Assert.True(blocked.BlockedByPossibleSaveData);

        var cleared = fixture.Management.ClearCache(new CacheClearRequest("game-1", Force: true));
        Assert.True(cleared.Cleared);
        Assert.False(Directory.Exists(fixture.CacheRoot));
        Assert.NotNull(cleared.QuarantineEntry);

        var restored = fixture.Management.RestoreQuarantine(cleared.QuarantineEntry!.QuarantineId);
        Assert.Equal(fixture.CacheRoot, restored.RestoredPath);
        Assert.True(File.Exists(Path.Combine(fixture.CacheRoot, "save", "slot1.sav")));
    }

    [Fact]
    public void Safe_clear_does_not_block_config_directory_unless_enabled()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe");
        fixture.CreateGameAndIndex();
        Directory.CreateDirectory(fixture.CacheRoot);
        Directory.CreateDirectory(Path.Combine(fixture.CacheRoot, "config"));
        File.WriteAllText(Path.Combine(fixture.CacheRoot, "config", "settings.ini"), "config");

        var defaultClear = fixture.Management.ClearCache(new CacheClearRequest("game-1"));
        Assert.True(defaultClear.Cleared);
        Assert.False(defaultClear.BlockedByPossibleSaveData);

        var restored = fixture.Management.RestoreQuarantine(defaultClear.QuarantineEntry!.QuarantineId);
        Assert.Equal(fixture.CacheRoot, restored.RestoredPath);

        var strictManagement = new CacheManagementService(fixture.Factory, fixture.Cache, protectConfigFiles: true);
        var strictClear = strictManagement.ClearCache(new CacheClearRequest("game-1"));

        Assert.False(strictClear.Cleared);
        Assert.True(strictClear.BlockedByPossibleSaveData);
        Assert.Contains(strictClear.DetectedUserDataPaths, item => item.EndsWith("config", StringComparison.OrdinalIgnoreCase));
    }


    [Fact]
    public void Safe_clear_optional_config_protection_detects_mtool_user_folders()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe");
        fixture.CreateGameAndIndex();
        Directory.CreateDirectory(fixture.CacheRoot);
        Directory.CreateDirectory(Path.Combine(fixture.CacheRoot, "MToolScripts"));
        File.WriteAllText(Path.Combine(fixture.CacheRoot, "MToolScripts", "custom.js"), "script");

        var defaultClear = fixture.Management.ClearCache(new CacheClearRequest("game-1"));
        Assert.True(defaultClear.Cleared);
        Assert.False(defaultClear.BlockedByPossibleSaveData);

        var restored = fixture.Management.RestoreQuarantine(defaultClear.QuarantineEntry!.QuarantineId);
        Assert.Equal(fixture.CacheRoot, restored.RestoredPath);

        var strictManagement = new CacheManagementService(fixture.Factory, fixture.Cache, protectConfigFiles: true);
        var strictClear = strictManagement.ClearCache(new CacheClearRequest("game-1"));

        Assert.False(strictClear.Cleared);
        Assert.True(strictClear.BlockedByPossibleSaveData);
        Assert.Contains(strictClear.DetectedUserDataPaths, item => item.EndsWith("MToolScripts", StringComparison.OrdinalIgnoreCase));
    }
    [Fact]
    public void Safe_clear_blocks_when_wrapper_runtime_lock_is_held()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe");
        fixture.CreateGameAndIndex();
        Directory.CreateDirectory(fixture.CacheRoot);
        fixture.Games.Update(fixture.Games.GetById("game-1")! with
        {
            CacheStatus = CacheStatus.Ready,
            CacheSizeBytes = 100
        });
        using var runtimeLock = RuntimeGameLock.TryAcquire("game-1");
        Assert.NotNull(runtimeLock);

        var result = fixture.Management.ClearCache(new CacheClearRequest("game-1", Force: true));

        Assert.False(result.Cleared);
        Assert.Equal("cache_in_use", result.Status);
        Assert.True(Directory.Exists(fixture.CacheRoot));
    }

    [Fact]
    public void Lru_apply_deletes_recreatable_game_body_instead_of_full_quarantine()
    {
        using var fixture = TestFixture.Create(availableFreeSpaceProvider: _ => 10_000);
        fixture.CreateSource("Game.exe", "data.bin");
        fixture.CreateGameAndIndex();
        Directory.CreateDirectory(fixture.CacheRoot);
        File.WriteAllText(Path.Combine(fixture.CacheRoot, "Game.exe"), "exe");
        File.WriteAllText(Path.Combine(fixture.CacheRoot, "data.bin"), "data");
        fixture.Games.Update(fixture.Games.GetById("game-1")! with
        {
            CacheStatus = CacheStatus.Ready,
            CacheSizeBytes = 8
        });

        var result = fixture.Management.ApplyLruCleanup(new CacheCleanupOptions(MaxTotalCacheBytes: 1, MinimumFreeSpaceBytes: 0), maxItems: 1);

        Assert.Contains(result.Status, new[] { "cleaned", "partial" });
        Assert.Single(result.Deleted);
        Assert.True(result.PermanentlyDeletedBytes > 0);
        Assert.Equal(0, result.MovedToQuarantineBytes);
        Assert.False(Directory.Exists(fixture.CacheRoot));
        Assert.Empty(new QuarantineRepository(fixture.Factory).ListByGame("game-1"));
    }

    [Fact]
    public void Lru_apply_skips_ready_cache_without_file_index()
    {
        using var fixture = TestFixture.Create(availableFreeSpaceProvider: _ => 0);
        fixture.CreateSource("Game.exe", "data.bin");
        fixture.CreateGameOnly();
        Directory.CreateDirectory(fixture.CacheRoot);
        File.WriteAllText(Path.Combine(fixture.CacheRoot, "Game.exe"), "exe");
        File.WriteAllText(Path.Combine(fixture.CacheRoot, "data.bin"), "data");
        fixture.Games.Update(fixture.Games.GetById("game-1")! with
        {
            CacheStatus = CacheStatus.Ready,
            CacheRevision = 1,
            SourceRevision = 1,
            CacheSizeBytes = 7
        });

        var result = fixture.Management.ApplyLruCleanup(new CacheCleanupOptions(MaxTotalCacheBytes: 1, MinimumFreeSpaceBytes: 0), maxItems: 1);

        Assert.Empty(result.Candidates);
        Assert.Empty(result.Deleted);
        Assert.True(Directory.Exists(fixture.CacheRoot));
    }

    [Fact]
    public void Lru_apply_uses_minimum_free_space_and_rereads_available_space()
    {
        var reads = new Queue<long>([0, 0, 40, 120, 120]);
        using var fixture = TestFixture.Create(availableFreeSpaceProvider: _ => reads.Count > 0 ? reads.Dequeue() : 120);
        fixture.CreateSource("Game.exe");
        fixture.CreateGameAndIndex();
        Directory.CreateDirectory(fixture.CacheRoot);
        File.WriteAllText(Path.Combine(fixture.CacheRoot, "Game.exe"), "exe");
        fixture.Games.Update(fixture.Games.GetById("game-1")! with
        {
            CacheStatus = CacheStatus.Ready,
            CacheSizeBytes = 3,
            LastPlayedUtc = DateTimeOffset.UtcNow.AddDays(-10)
        });
        var secondCache = Path.Combine(fixture.Root, "second-cache");
        Directory.CreateDirectory(secondCache);
        File.WriteAllText(Path.Combine(secondCache, "Game.exe"), "exe2");
        fixture.Games.Upsert(new GameRecord
        {
            GameId = "game-2",
            DisplayName = "game-2",
            SourceRoot = fixture.SourceRoot,
            CacheRoot = secondCache,
            ExecutablePath = Path.Combine(fixture.SourceRoot, "Game.exe"),
            WorkingDirectory = fixture.SourceRoot,
            CacheStatus = CacheStatus.Ready,
            CacheSizeBytes = 4,
            LastPlayedUtc = DateTimeOffset.UtcNow.AddDays(-9)
        });

        var result = fixture.Management.ApplyLruCleanup(new CacheCleanupOptions(MaxTotalCacheBytes: 0, MinimumFreeSpaceBytes: 100), maxItems: 5);

        Assert.True(result.Deleted.Count >= 1);
        Assert.True(reads.Count < 5);
        Assert.Equal(120, result.FreeSpaceAfterBytes);
    }

    [Fact]
    public void Lru_apply_preserves_user_data_to_small_quarantine()
    {
        using var fixture = TestFixture.Create(availableFreeSpaceProvider: _ => 0);
        fixture.CreateSource("Game.exe");
        fixture.CreateGameAndIndex();
        Directory.CreateDirectory(fixture.CacheRoot);
        File.WriteAllText(Path.Combine(fixture.CacheRoot, "Game.exe"), "exe");
        Directory.CreateDirectory(Path.Combine(fixture.CacheRoot, "save"));
        File.WriteAllText(Path.Combine(fixture.CacheRoot, "save", "slot1.sav"), "save");
        File.WriteAllText(Path.Combine(fixture.CacheRoot, "user-note.txt"), "note");
        fixture.Games.Update(fixture.Games.GetById("game-1")! with
        {
            CacheStatus = CacheStatus.Ready,
            CacheSizeBytes = 12,
            ContainsPossibleSaveData = true
        });

        var result = fixture.Management.ApplyLruCleanup(new CacheCleanupOptions(MaxTotalCacheBytes: 1, MinimumFreeSpaceBytes: 0), maxItems: 1);

        Assert.Single(result.Deleted);
        Assert.True(result.ProtectedUserDataBytes > 0);
        Assert.False(Directory.Exists(fixture.CacheRoot));
        var quarantine = Assert.Single(new QuarantineRepository(fixture.Factory).ListByGame("game-1"));
        Assert.True(File.Exists(Path.Combine(quarantine.QuarantinePath, "save", "slot1.sav")));
        Assert.True(File.Exists(Path.Combine(quarantine.QuarantinePath, "user-note.txt")));
        Assert.False(File.Exists(Path.Combine(quarantine.QuarantinePath, "Game.exe")));
    }

    [Fact]
    public void Lru_apply_does_not_follow_reparse_points_when_detecting_user_data()
    {
        using var fixture = TestFixture.Create(availableFreeSpaceProvider: _ => 0);
        fixture.CreateSource("Game.exe");
        fixture.CreateGameAndIndex();
        Directory.CreateDirectory(fixture.CacheRoot);
        File.WriteAllText(Path.Combine(fixture.CacheRoot, "Game.exe"), "exe");
        var externalRoot = Path.Combine(fixture.Root, "external-user-data");
        Directory.CreateDirectory(externalRoot);
        var externalFile = Path.Combine(externalRoot, "outside.sav");
        File.WriteAllText(externalFile, "outside");
        var linkPath = Path.Combine(fixture.CacheRoot, "linked-save");
        try
        {
            Directory.CreateSymbolicLink(linkPath, externalRoot);
        }
        catch
        {
            return;
        }

        fixture.Games.Update(fixture.Games.GetById("game-1")! with
        {
            CacheStatus = CacheStatus.Ready,
            CacheSizeBytes = 1024
        });

        var result = fixture.Management.ApplyLruCleanup(new CacheCleanupOptions(MaxTotalCacheBytes: 1, MinimumFreeSpaceBytes: 0), maxItems: 1);

        Assert.Single(result.Deleted);
        Assert.Equal(0, result.ProtectedUserDataBytes);
        Assert.Empty(new QuarantineRepository(fixture.Factory).ListByGame("game-1"));
        Assert.True(File.Exists(externalFile));
    }

    [Fact]
    public void Lru_apply_skips_when_source_root_is_unavailable()
    {
        using var fixture = TestFixture.Create(availableFreeSpaceProvider: _ => 0);
        fixture.CreateSource("Game.exe");
        fixture.CreateGameAndIndex();
        Directory.CreateDirectory(fixture.CacheRoot);
        File.WriteAllText(Path.Combine(fixture.CacheRoot, "Game.exe"), "exe");
        Directory.Delete(fixture.SourceRoot, recursive: true);
        fixture.Games.Update(fixture.Games.GetById("game-1")! with
        {
            CacheStatus = CacheStatus.Ready,
            CacheSizeBytes = 3
        });

        var result = fixture.Management.ApplyLruCleanup(new CacheCleanupOptions(MaxTotalCacheBytes: 1, MinimumFreeSpaceBytes: 0), maxItems: 1);

        Assert.Empty(result.Deleted);
        Assert.Empty(result.Candidates);
        Assert.True(Directory.Exists(fixture.CacheRoot));
    }

    [Fact]
    public void Lru_apply_blocks_drive_root_cache_target()
    {
        var deleteCalled = false;
        using var fixture = TestFixture.Create(
            availableFreeSpaceProvider: _ => 0,
            deleteDirectory: _ => deleteCalled = true);
        fixture.CreateSource("Game.exe");
        fixture.CreateGameAndIndex();
        var driveRoot = Path.GetPathRoot(fixture.Root)!;
        fixture.Games.Update(fixture.Games.GetById("game-1")! with
        {
            CacheRoot = driveRoot,
            CacheStatus = CacheStatus.Ready,
            CacheSizeBytes = 1024
        });

        var result = fixture.Management.ApplyLruCleanup(new CacheCleanupOptions(MaxTotalCacheBytes: 1, MinimumFreeSpaceBytes: 0), maxItems: 1);

        Assert.Empty(result.Deleted);
        Assert.Empty(result.Candidates);
        Assert.Empty(result.SkippedOrFailed);
        Assert.False(deleteCalled);
    }

    [Fact]
    public void Expired_quarantine_cleanup_respects_retention_and_retries_failures()
    {
        var failDelete = true;
        using var fixture = TestFixture.Create(
            availableFreeSpaceProvider: _ => 0,
            deleteDirectory: path =>
            {
                if (failDelete)
                {
                    failDelete = false;
                    throw new IOException("locked");
                }

                Directory.Delete(path, recursive: true);
            });
        var quarantineRoot = Path.Combine(fixture.Root, "Quarantine");
        var quarantinePath = Path.Combine(quarantineRoot, "old");
        Directory.CreateDirectory(quarantinePath);
        File.WriteAllText(Path.Combine(quarantinePath, "save.sav"), "save");
        fixture.CreateSource("Game.exe");
        fixture.CreateGameOnly();
        var repository = new QuarantineRepository(fixture.Factory);
        repository.Create(new QuarantineEntryRecord
        {
            QuarantineId = "q-old",
            GameId = "game-1",
            OriginalCacheRoot = fixture.CacheRoot,
            QuarantinePath = quarantinePath,
            Reason = "manual_clear",
            CreatedUtc = DateTimeOffset.UtcNow.AddDays(-31),
            SizeBytes = 4,
            FileCount = 1,
            ContainsPossibleSaveData = true
        });

        var disabled = fixture.Management.CleanupExpiredQuarantine(0);
        Assert.Equal("disabled", disabled.Status);
        Assert.True(Directory.Exists(quarantinePath));

        var failed = fixture.Management.CleanupExpiredQuarantine(30);
        Assert.Equal(1, failed.FailedCount);
        Assert.NotNull(repository.GetById("q-old"));
        Assert.True(Directory.Exists(quarantinePath));

        var cleaned = fixture.Management.CleanupExpiredQuarantine(30);
        Assert.Equal(1, cleaned.DeletedCount);
        Assert.Null(repository.GetById("q-old"));
        Assert.False(Directory.Exists(quarantinePath));
    }

    [Fact]
    public async Task Launch_restores_latest_quarantine_cache_without_robocopy_when_safe()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe", "data.bin");
        fixture.CreateGameAndIndex();
        await fixture.Cache.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), "game-1"));

        var cleared = fixture.Management.ClearCache(new CacheClearRequest("game-1", Force: true));
        Assert.True(cleared.Cleared);
        Assert.NotNull(cleared.QuarantineEntry);
        var quarantinePath = cleared.QuarantineEntry!.QuarantinePath;
        Assert.True(Directory.Exists(quarantinePath));
        fixture.Runner.Reset();

        var result = await fixture.Cache.PrepareCacheAsync(new CacheRequest(Guid.NewGuid(), "game-1"));

        Assert.Equal(CacheResultStatus.Success, result.Status);
        Assert.False(result.RobocopyCalled);
        Assert.Equal(0, fixture.Runner.CallCount);
        Assert.True(File.Exists(Path.Combine(fixture.CacheRoot, "Game.exe")));
        Assert.False(Directory.Exists(quarantinePath));
        var entry = new QuarantineRepository(fixture.Factory).GetById(cleared.QuarantineEntry.QuarantineId)!;
        Assert.NotNull(entry.RestoredUtc);
        var game = fixture.Games.GetById("game-1")!;
        Assert.Equal(CacheStatus.Ready, game.CacheStatus);
        Assert.Equal(1, game.CacheRevision);
    }

    [Fact]
    public void Save_profile_can_validate_backup_export_import_and_restore()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe");
        fixture.CreateGameAndIndex();
        var saveRoot = Path.Combine(fixture.CacheRoot, "save 中文");
        Directory.CreateDirectory(saveRoot);
        File.WriteAllText(Path.Combine(saveRoot, "slot1.sav"), "old");
        var service = new SaveProfileService(fixture.Factory);
        var profile = service.AddOrUpdate(new SaveProfileCreateRequest("game-1", "主存档", SaveProfileSourceType.GameRelative, "save 中文", Path.Combine(fixture.Root, "backups")));

        var validation = service.Validate(profile.SaveProfileId);
        Assert.True(validation.Exists);
        Assert.True(validation.CanRead);
        Assert.Equal(1, validation.FileCount);

        var backup = service.Backup(profile.SaveProfileId);
        Assert.True(File.Exists(backup.BackupPath));

        var exportPath = Path.Combine(fixture.Root, "export.zip");
        service.Export(profile.SaveProfileId, exportPath);
        Assert.True(File.Exists(exportPath));

        var importRoot = Path.Combine(fixture.Root, "import");
        Directory.CreateDirectory(importRoot);
        File.WriteAllText(Path.Combine(importRoot, "slot1.sav"), "new");
        var imported = service.Import(profile.SaveProfileId, importRoot);
        Assert.Equal("imported", imported.Status);
        Assert.Equal("new", File.ReadAllText(Path.Combine(saveRoot, "slot1.sav")));

        var restored = service.Restore(backup.BackupId);
        Assert.Equal("restored", restored.Status);
        Assert.Equal("old", File.ReadAllText(Path.Combine(saveRoot, "slot1.sav")));
    }

    [Fact]
    public void Save_profile_uses_shared_settings_backup_root_when_not_explicit()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe");
        fixture.CreateGameAndIndex();
        var settingsPath = Path.Combine(fixture.Root, "settings.json");
        var backupRoot = Path.Combine(fixture.Root, "configured backups");
        File.WriteAllText(settingsPath, $$"""
        {
          "schema_version": 1,
          "default_save_backup_root": "{{backupRoot.Replace("\\", "\\\\")}}"
        }
        """);
        Environment.SetEnvironmentVariable("VGC_SETTINGS_PATH", settingsPath);
        try
        {
            var service = new SaveProfileService(fixture.Factory);

            var profile = service.AddOrUpdate(new SaveProfileCreateRequest("game-1", "主存档", SaveProfileSourceType.GameRelative, "save"));

            Assert.StartsWith(backupRoot, profile.BackupRoot, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VGC_SETTINGS_PATH", null);
        }
    }

    [Fact]
    public void Save_profile_add_is_idempotent_for_same_resolved_path()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe");
        fixture.CreateGameAndIndex();
        var service = new SaveProfileService(fixture.Factory);

        var first = service.AddOrUpdate(new SaveProfileCreateRequest("game-1", "主存档", SaveProfileSourceType.GameRelative, "save"));
        var second = service.AddOrUpdate(new SaveProfileCreateRequest("game-1", "主存档更新", SaveProfileSourceType.GameRelative, "save"));

        Assert.Equal(first.SaveProfileId, second.SaveProfileId);
        Assert.Single(service.List("game-1"));
        Assert.Equal("主存档更新", service.List("game-1")[0].DisplayName);
    }

    [Fact]
    public void Save_profile_rejects_relative_path_escape()
    {
        using var fixture = TestFixture.Create();
        fixture.CreateSource("Game.exe");
        fixture.CreateGameAndIndex();
        var service = new SaveProfileService(fixture.Factory);

        var error = Assert.Throws<CacheException>(() =>
            service.AddOrUpdate(new SaveProfileCreateRequest("game-1", "逃逸路径", SaveProfileSourceType.GameRelative, @"..\outside")));

        Assert.Equal("save_profile_path_escape", error.Code);
        Assert.Empty(service.List("game-1"));
    }

    [Fact]
    public void Invalid_shared_settings_are_reported_and_block_database_fallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "vgc-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "settings.json");
        File.WriteAllText(settingsPath, "{ invalid json");
        Environment.SetEnvironmentVariable("VGC_SETTINGS_PATH", settingsPath);
        Environment.SetEnvironmentVariable("VGC_DB_PATH", null);
        try
        {
            var health = VgcSettingsStore.LoadWithDiagnostics();
            Assert.Equal(VgcSettingsHealthStatus.InvalidJson, health.Status);
            Assert.Throws<VgcSettingsException>(() => VgcSettingsStore.ResolveDatabasePath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("VGC_SETTINGS_PATH", null);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class FakeRobocopyRunner : IRobocopyRunner
    {
        public int CallCount { get; private set; }
        public int NextExitCode { get; set; }
        public bool CancelNext { get; set; }
        public bool CopyBeforeReturningFailure { get; set; }

        public Task<RobocopyResult> RunAsync(
            RobocopyCommand command,
            CancellationToken cancellationToken,
            Action<RobocopyProgress>? progress = null)
        {
            CallCount++;
            if (CancelNext)
            {
                CancelNext = false;
                Directory.CreateDirectory(command.CacheRoot);
                File.WriteAllText(Path.Combine(command.CacheRoot, "partial.tmp"), "partial");
                return Task.FromResult(new RobocopyResult(16, Cancelled: true, []));
            }

            if (NextExitCode <= 7 || CopyBeforeReturningFailure)
            {
                CopyDirectory(command.SourceRoot, command.CacheRoot);
                var files = Directory.EnumerateFiles(command.SourceRoot, "*", SearchOption.AllDirectories).ToList();
                progress?.Invoke(new RobocopyProgress(
                    files.Sum(file => new FileInfo(file).Length),
                    files.Count,
                    files.LastOrDefault(),
                    files.Sum(file => new FileInfo(file).Length),
                    TimeSpan.FromSeconds(1)));
            }

            var exitCode = NextExitCode;
            NextExitCode = 0;
            CopyBeforeReturningFailure = false;
            return Task.FromResult(new RobocopyResult(exitCode, Cancelled: false, []));
        }

        public void Reset()
        {
            CallCount = 0;
            NextExitCode = 0;
            CancelNext = false;
            CopyBeforeReturningFailure = false;
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
            }
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
        }
    }

    private sealed class TestFixture : IDisposable
    {
        private readonly string root;
        public string Root => root;
        public string SourceRoot { get; }
        public string CacheRoot { get; }
        public GameRepository Games { get; }
        public CacheService Cache { get; }
        public CacheManagementService Management { get; }
        public SqliteConnectionFactory Factory { get; }
        public FakeRobocopyRunner Runner { get; } = new();
        private readonly IndexingService indexing;

        private TestFixture(string root, Func<string, long>? availableFreeSpaceProvider = null, Action<string>? deleteDirectory = null)
        {
            this.root = root;
            SourceRoot = Path.Combine(root, "source 中文 空格");
            CacheRoot = Path.Combine(root, "cache 中文 空格");
            Directory.CreateDirectory(SourceRoot);
            Factory = new SqliteConnectionFactory(new DatabaseOptions(Path.Combine(root, "index.db")));
            new MigrationRunner(Factory).Initialize();
            Games = new GameRepository(Factory);
            indexing = new IndexingService(
                new IndexingStore(Factory),
                new FileSystemScanner(),
                new ManifestBuilder(),
                new IndexingOptions(Path.Combine(root, "manifests")));
            Cache = new CacheService(
                new CacheStore(Factory),
                new CopyPlanner(),
                new RobocopyCommandBuilder(Path.Combine(root, "logs")),
                Runner,
                new CacheVerifier(),
                minimumFreeSpaceBytes: 0);
            Management = new CacheManagementService(Factory, Cache, availableFreeSpaceProvider: availableFreeSpaceProvider, deleteDirectory: deleteDirectory);
        }

        public static TestFixture Create(Func<string, long>? availableFreeSpaceProvider = null, Action<string>? deleteDirectory = null) =>
            new(Path.Combine(Path.GetTempPath(), "vgc-cache-tests", Guid.NewGuid().ToString("N")), availableFreeSpaceProvider, deleteDirectory);

        public void CreateSource(params string[] files)
        {
            foreach (var file in files)
            {
                var path = Path.Combine(SourceRoot, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file);
            }
        }

        public void CreateGameAndIndex(string executableRelativePath = "Game.exe", string? workingRelativePath = null)
        {
            CreateGameOnly(executableRelativePath, workingRelativePath);
            var result = indexing.IndexGame("game-1");
            Assert.True(result.Status is ManifestStatus.Complete or ManifestStatus.CompleteWithWarnings);
        }

        public void CreateGameOnly(string executableRelativePath = "Game.exe", string? workingRelativePath = null)
        {
            Games.Create(new GameRecord
            {
                GameId = "game-1",
                DisplayName = "game-1",
                SourceRoot = SourceRoot,
                CacheRoot = CacheRoot,
                ExecutablePath = Path.Combine(SourceRoot, executableRelativePath),
                WorkingDirectory = workingRelativePath is null ? SourceRoot : Path.Combine(SourceRoot, workingRelativePath)
            });
        }

        public void IndexAgain()
        {
            var result = indexing.IndexGame("game-1");
            Assert.True(result.Status is ManifestStatus.Complete or ManifestStatus.CompleteWithWarnings);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }
}


