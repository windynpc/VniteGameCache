using Microsoft.Data.Sqlite;
using VniteGameCache.Persistence;

namespace VniteGameCache.Persistence.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public void Initialize_creates_schema_pragmas_and_history_idempotently()
    {
        var factory = NewFactory();
        var runner = new MigrationRunner(factory);

        runner.Initialize();
        runner.Initialize();

        var history = runner.GetHistory();
        Assert.Equal(Schema.CurrentVersion, history.Count);
        Assert.Equal(Schema.CurrentVersion, history[^1].SchemaVersion);

        using var connection = factory.OpenConnection();
        Assert.Equal(1L, Scalar<long>(connection, "PRAGMA foreign_keys;"));
        Assert.Equal("wal", Convert.ToString(Scalar<object>(connection, "PRAGMA journal_mode;"))!.ToLowerInvariant());
        Assert.True(Scalar<long>(connection, "PRAGMA busy_timeout;") >= 1000);
        AssertTableExists(connection, "games");
        AssertTableExists(connection, "file_entries");
        AssertTableExists(connection, "save_profiles");
        AssertTableExists(connection, "save_backups");
        AssertTableExists(connection, "quarantine_entries");
        AssertTableExists(connection, "tombstones");
        AssertTableExists(connection, "operation_records");
        AssertTableExists(connection, "launch_sessions");
        AssertTableExists(connection, "migration_history");
    }

    [Fact]
    public void Future_schema_version_is_rejected()
    {
        var factory = NewFactory();
        new MigrationRunner(factory).Initialize();
        using (var connection = factory.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO migration_history(schema_version, migration_id, migration_name, applied_utc)
                VALUES(99, 'future', 'future', '2099-01-01T00:00:00.0000000+00:00');
                """;
            command.ExecuteNonQuery();
        }

        var error = Assert.Throws<MigrationException>(() => new MigrationRunner(factory).Initialize());
        Assert.Equal("future_schema_version", error.Code);
    }

    [Fact]
    public void Game_repository_preserves_paths_and_enforces_vnite_unique()
    {
        var factory = InitializedFactory();
        var games = new GameRepository(factory);
        var game = SampleGame() with
        {
            SourceRoot = @"\\NAS-SERVER\Games\中文 空格\源",
            CacheRoot = @"D:\Cache Root\中文",
            ExecutablePath = @"\\NAS-SERVER\Games\中文 空格\Game.exe",
            WorkingDirectory = @"\\NAS-SERVER\Games\中文 空格",
            VniteGameId = "vnite-1"
        };

        games.Create(game);
        var loaded = games.GetById(game.GameId);

        Assert.NotNull(loaded);
        Assert.Equal(game.SourceRoot, loaded.SourceRoot);
        Assert.Equal(game.ExecutablePath, loaded.ExecutablePath);
        Assert.Equal(game.WorkingDirectory, games.GetByVniteGameId("vnite-1")!.WorkingDirectory);
        Assert.Throws<SqliteException>(() => games.Create(SampleGame("game-2") with { VniteGameId = "vnite-1" }));
    }

    [Fact]
    public void Game_upsert_update_delete_and_list_work()
    {
        var games = new GameRepository(InitializedFactory());
        var game = SampleGame();
        games.Upsert(game);
        games.Upsert(game with { DisplayName = "Updated", CacheStatus = CacheStatus.Ready });
        games.Update(game with { DisplayName = "Updated Again" });

        Assert.Equal("Updated Again", games.GetById(game.GameId)!.DisplayName);
        Assert.Single(games.List());
        games.Delete(game.GameId);
        Assert.Null(games.GetById(game.GameId));
    }

    [Fact]
    public void File_entries_upsert_replace_and_constraints_are_transactional()
    {
        var factory = InitializedFactory();
        var games = new GameRepository(factory);
        var files = new FileEntryRepository(factory);
        games.Create(SampleGame());

        files.UpsertMany([
            SampleFile("game-1", @"data\file 1.txt", 10),
            SampleFile("game-1", @"data\file 2.txt", 20)
        ]);
        files.UpsertMany([SampleFile("game-1", @"data\file 1.txt", 30)]);

        Assert.Equal(2, files.ListByGame("game-1").Count);
        Assert.Equal(30, files.Get("game-1", @"data\file 1.txt")!.SizeBytes);

        Assert.Throws<SqliteException>(() => files.UpsertMany([SampleFile("missing", "x.txt", 1)]));
        Assert.Throws<SqliteException>(() => files.UpsertMany([SampleFile("game-1", @"C:\absolute.txt", 1)]));

        Assert.Throws<PersistenceException>(() => files.ReplaceForGame("game-1", [
            SampleFile("game-1", "ok.txt", 1),
            SampleFile("other", "bad.txt", 1)
        ]));
        Assert.Equal(2, files.ListByGame("game-1").Count);

        files.ReplaceForGame("game-1", [SampleFile("game-1", "only.txt", 1)]);
        Assert.Single(files.ListByGame("game-1"));
    }

    [Fact]
    public void Mutable_paths_directory_fingerprints_tasks_and_conflicts_work()
    {
        var factory = InitializedFactory();
        new GameRepository(factory).Create(SampleGame());
        var directories = new DirectoryFingerprintRepository(factory);
        directories.UpsertMany([new DirectoryFingerprintRecord
        {
            GameId = "game-1",
            RelativePath = "data",
            DirectFileCount = 2,
            DirectDirectoryCount = 1,
            AggregateSizeBytes = 123,
            LatestKnownModifiedUtc = DateTimeOffset.UtcNow,
            SourceRevision = 1
        }]);
        Assert.Single(directories.ListByGame("game-1"));

        var mutable = new MutablePathRepository(factory);
        mutable.Create(new MutablePathRecord
        {
            MutablePathId = "mp-1",
            GameId = "game-1",
            PathTemplate = @"Saves\*.sav",
            PathKind = PathKind.GameRelative,
            SyncMode = SyncMode.Bidirectional,
            ExcludePatternsJson = "[\"*.tmp\"]"
        });
        Assert.Equal("[\"*.tmp\"]", mutable.ListByGame("game-1")[0].ExcludePatternsJson);

        var tasks = new TaskRepository(factory);
        tasks.Create(new TaskRecord { TaskId = "task-1", GameId = "game-1", TaskType = "PrepareLaunch", State = TaskState.Pending });
        tasks.Create(new TaskRecord { TaskId = "global-1", GameId = null, TaskType = "Maintenance", State = TaskState.Interrupted });
        tasks.UpdateProgress("task-1", 1, 2);
        tasks.UpdateState("task-1", TaskState.Completed);
        Assert.Single(tasks.GetPendingOrInterrupted());
        Assert.Equal(TaskState.Completed, tasks.GetById("task-1")!.State);

        var conflicts = new ConflictRepository(factory);
        conflicts.Create(new ConflictRecord
        {
            ConflictId = "conflict-1",
            GameId = "game-1",
            MutablePathId = "mp-1",
            RelativePath = "save1.sav",
            LocalVersionJson = "{\"size\":1}",
            NasVersionJson = "{\"size\":2}"
        });
        Assert.Single(conflicts.GetUnresolvedByGame("game-1"));
        conflicts.Resolve("conflict-1", "{\"choice\":\"local\"}");
        Assert.Empty(conflicts.GetUnresolvedByGame("game-1"));
    }

    [Fact]
    public void Task_repository_marks_stale_checking_copying_and_verifying_as_interrupted()
    {
        var factory = InitializedFactory();
        var tasks = new TaskRepository(factory);
        var oldUtc = DateTimeOffset.UtcNow.AddHours(-8);
        var freshUtc = DateTimeOffset.UtcNow;

        tasks.Create(new TaskRecord { TaskId = "old-copying", TaskType = "Cache", State = TaskState.Copying, UpdatedUtc = oldUtc });
        tasks.Create(new TaskRecord { TaskId = "old-verifying", TaskType = "Cache", State = TaskState.Verifying, UpdatedUtc = oldUtc });
        tasks.Create(new TaskRecord { TaskId = "old-checking", TaskType = "Cache", State = TaskState.Checking, UpdatedUtc = oldUtc });
        tasks.Create(new TaskRecord { TaskId = "fresh-copying", TaskType = "Cache", State = TaskState.Copying, UpdatedUtc = freshUtc });
        tasks.Create(new TaskRecord { TaskId = "old-running", TaskType = "Launch", State = TaskState.Running, UpdatedUtc = oldUtc });

        var recovered = tasks.MarkStaleActiveTasksInterrupted(DateTimeOffset.UtcNow.AddHours(-6));

        Assert.Equal(3, recovered);
        Assert.Equal(TaskState.Interrupted, tasks.GetById("old-copying")!.State);
        Assert.Equal(TaskState.Interrupted, tasks.GetById("old-verifying")!.State);
        Assert.Equal(TaskState.Interrupted, tasks.GetById("old-checking")!.State);
        Assert.Equal("stale_task_recovered", tasks.GetById("old-copying")!.LastErrorCode);
        Assert.Equal(TaskState.Copying, tasks.GetById("fresh-copying")!.State);
        Assert.Equal(TaskState.Running, tasks.GetById("old-running")!.State);
    }

    [Fact]
    public void Phase6_repositories_store_save_profiles_backups_and_quarantine()
    {
        var factory = InitializedFactory();
        new GameRepository(factory).Create(SampleGame() with
        {
            ContainsPossibleSaveData = true,
            PendingManualSaveBackup = true,
            LastCacheRefreshUtc = DateTimeOffset.UtcNow
        });

        var loadedGame = new GameRepository(factory).GetById("game-1")!;
        Assert.True(loadedGame.ContainsPossibleSaveData);
        Assert.True(loadedGame.PendingManualSaveBackup);
        Assert.NotNull(loadedGame.LastCacheRefreshUtc);

        var profiles = new SaveProfileRepository(factory);
        profiles.Upsert(new SaveProfileRecord
        {
            SaveProfileId = "profile-1",
            GameId = "game-1",
            DisplayName = "存档",
            SourceType = SaveProfileSourceType.GameRelative,
            PathTemplate = "save",
            ResolvedPath = @"D:\Cache Root\save",
            BackupRoot = @"D:\Backups"
        });
        profiles.UpdateBackupUtc("profile-1", DateTimeOffset.UtcNow);
        Assert.Single(profiles.ListByGame("game-1"));
        Assert.NotNull(profiles.GetById("profile-1")!.LastBackupUtc);

        var backups = new SaveBackupRepository(factory);
        backups.Create(new SaveBackupRecord
        {
            BackupId = "backup-1",
            SaveProfileId = "profile-1",
            GameId = "game-1",
            BackupPath = @"D:\Backups\save.zip",
            BackupType = SaveBackupType.Manual,
            SourcePath = @"D:\Cache Root\save",
            FileCount = 2,
            TotalBytes = 10
        });
        Assert.Single(backups.ListByProfile("profile-1"));

        var quarantine = new QuarantineRepository(factory);
        quarantine.Create(new QuarantineEntryRecord
        {
            QuarantineId = "quarantine-1",
            GameId = "game-1",
            OriginalCacheRoot = @"D:\Cache Root",
            QuarantinePath = @"D:\Quarantine\game-1",
            Reason = "test",
            SizeBytes = 10,
            FileCount = 2,
            ContainsPossibleSaveData = true
        });
        quarantine.MarkRestored("quarantine-1", DateTimeOffset.UtcNow);
        Assert.NotNull(quarantine.GetById("quarantine-1")!.RestoredUtc);
    }

    [Fact]
    public void Check_constraints_reject_invalid_progress_and_negative_values()
    {
        var factory = InitializedFactory();
        new GameRepository(factory).Create(SampleGame());
        var tasks = new TaskRepository(factory);

        Assert.Throws<SqliteException>(() => tasks.Create(new TaskRecord
        {
            TaskId = "bad-progress",
            GameId = "game-1",
            TaskType = "Bad",
            State = TaskState.Pending,
            ProgressCurrent = 3,
            ProgressTotal = 2
        }));

        Assert.Throws<SqliteException>(() => new DirectoryFingerprintRepository(factory).UpsertMany([new DirectoryFingerprintRecord
        {
            GameId = "game-1",
            RelativePath = "bad",
            DirectFileCount = -1,
            DirectDirectoryCount = 0,
            AggregateSizeBytes = 0
        }]));
    }

    [Fact]
    public void Corrupt_database_is_not_deleted_or_recreated()
    {
        var path = TempDbPath();
        File.WriteAllText(path, "not sqlite");
        var factory = new SqliteConnectionFactory(new DatabaseOptions(path));

        var error = Assert.ThrowsAny<Exception>(() => new MigrationRunner(factory).Initialize());

        Assert.True(error is PersistenceException or MigrationException or SqliteException);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);
    }

    [Fact]
    public void Invalid_database_path_returns_clear_open_error()
    {
        var directoryAsDatabase = Path.Combine(Path.GetTempPath(), "VniteGameCache.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryAsDatabase);
        var factory = new SqliteConnectionFactory(new DatabaseOptions(directoryAsDatabase));

        var error = Assert.ThrowsAny<Exception>(() => new MigrationRunner(factory).Initialize());

        Assert.True(error is PersistenceException or MigrationException or SqliteException);
    }


    [Fact]
    public void Launch_candidate_repository_replaces_and_marks_primary()
    {
        var factory = InitializedFactory();
        new GameRepository(factory).Create(SampleGame());
        var repository = new LaunchCandidateRepository(factory);

        repository.ReplaceForGame("game-1", [
            SampleCandidate("game-1", "Game.exe", LaunchCandidateRole.MainGame, 100, false),
            SampleCandidate("game-1", "Game_CN.exe", LaunchCandidateRole.TranslationGame, 140, true)
        ]);

        var listed = repository.ListByGame("game-1");
        Assert.Equal(2, listed.Count);
        Assert.Equal("Game_CN.exe", repository.GetPrimary("game-1")!.RelativePath);

        repository.MarkPrimary("game-1", listed.First(item => item.RelativePath == "Game.exe").CandidateId, true);
        Assert.Equal("Game.exe", repository.GetPrimary("game-1")!.RelativePath);

        repository.SetIgnored("game-1", listed.First(item => item.RelativePath == "Game_CN.exe").CandidateId, true);
        Assert.True(repository.ListByGame("game-1").First(item => item.RelativePath == "Game_CN.exe").IsIgnored);
    }

    [Fact]
    public void Launch_candidate_scanner_prefers_translation_exe_and_detects_mtool_batch()
    {
        var sourceRoot = TempDirectory();
        WriteTinyExecutable(Path.Combine(sourceRoot, "Game.exe"));
        WriteTinyExecutable(Path.Combine(sourceRoot, "Game_zh-CN.exe"));
        File.WriteAllText(Path.Combine(sourceRoot, "与工具一同启动.bat"), "loaders\\inject.exe Game.exe");
        var game = SampleGame() with { SourceRoot = sourceRoot, ExecutablePath = Path.Combine(sourceRoot, "Game.exe") };

        var candidates = new LaunchCandidateScanner().Scan(game).ToList();

        Assert.Contains(candidates, item => item.RelativePath == "Game_zh-CN.exe" && item.CandidateRole == LaunchCandidateRole.TranslationGame);
        Assert.Contains(candidates, item => item.RelativePath == "与工具一同启动.bat" && item.CandidateRole == LaunchCandidateRole.MToolTemplate);
        Assert.Equal("与工具一同启动.bat", candidates.First(item => item.IsPrimary).RelativePath);
    }

    [Fact]
    public void Launch_candidate_scanner_blocks_helper_tools()
    {
        var sourceRoot = TempDirectory();
        WriteTinyExecutable(Path.Combine(sourceRoot, "uninstall.exe"));
        WriteTinyExecutable(Path.Combine(sourceRoot, "config.exe"));
        var game = SampleGame() with { SourceRoot = sourceRoot };

        var candidates = new LaunchCandidateScanner().Scan(game).ToList();

        Assert.All(candidates, item => Assert.Equal(LaunchCandidateTestStatus.HardBlocked, item.LastTestStatus));
        Assert.Empty(candidates.Where(item => item.IsPrimary));
    }
    private static SqliteConnectionFactory InitializedFactory()
    {
        var factory = NewFactory();
        new MigrationRunner(factory).Initialize();
        return factory;
    }

    private static SqliteConnectionFactory NewFactory() => new(new DatabaseOptions(TempDbPath(), 1000));

    private static string TempDbPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VniteGameCache.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "index.db");
    }



    private static void WriteTinyExecutable(string path)
    {
        var bytes = new byte[2048];
        bytes[0] = 0x4D;
        bytes[1] = 0x5A;
        File.WriteAllBytes(path, bytes);
    }
    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VniteGameCache.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static GameLaunchCandidateRecord SampleCandidate(string gameId, string relativePath, LaunchCandidateRole role, int score, bool isPrimary) => new()
    {
        CandidateId = Guid.NewGuid().ToString("N"),
        GameId = gameId,
        RelativePath = relativePath,
        CandidateType = LaunchCandidateType.Executable,
        CandidateRole = role,
        CandidateSource = LaunchCandidateSource.DiskScan,
        Score = score,
        Confidence = Math.Min(100, score),
        IsPrimary = isPrimary,
        LastTestStatus = LaunchCandidateTestStatus.ReadOnlyValid
    };
    private static GameRecord SampleGame(string gameId = "game-1") => new()
    {
        GameId = gameId,
        DisplayName = $"Game {gameId}",
        SourceRoot = @"\\NAS-SERVER\Games\Source 中文",
        CacheRoot = @"D:\Cache Root",
        ExecutablePath = @"\\NAS-SERVER\Games\Source 中文\Game.exe",
        WorkingDirectory = @"\\NAS-SERVER\Games\Source 中文",
        ArgumentsJson = "[\"--flag\",\"值\"]"
    };

    private static FileEntryRecord SampleFile(string gameId, string relativePath, long size) => new()
    {
        GameId = gameId,
        RelativePath = relativePath,
        SizeBytes = size,
        ModifiedUtc = DateTimeOffset.UtcNow,
        FileRole = FileRole.ImmutableGameData
    };

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private static void AssertTableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", table);
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }
}




