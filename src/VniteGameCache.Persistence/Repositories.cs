using Microsoft.Data.Sqlite;

namespace VniteGameCache.Persistence;

public interface IGameRepository
{
    void Create(GameRecord game);
    GameRecord? GetById(string gameId);
    GameRecord? GetByVniteGameId(string vniteGameId);
    IReadOnlyList<GameRecord> List();
    void Update(GameRecord game);
    void Upsert(GameRecord game);
    void Delete(string gameId);
}

public interface ILaunchCandidateRepository
{
    void ReplaceForGame(string gameId, IEnumerable<GameLaunchCandidateRecord> candidates);
    void Upsert(GameLaunchCandidateRecord candidate);
    IReadOnlyList<GameLaunchCandidateRecord> ListByGame(string gameId);
    GameLaunchCandidateRecord? GetPrimary(string gameId);
    void MarkPrimary(string gameId, string candidateId, bool userConfirmed);
    void SetIgnored(string gameId, string candidateId, bool ignored);
}

public interface IFileEntryRepository
{
    void UpsertMany(IEnumerable<FileEntryRecord> entries);
    IReadOnlyList<FileEntryRecord> ListByGame(string gameId);
    FileEntryRecord? Get(string gameId, string relativePath);
    void DeleteByGame(string gameId);
    void ReplaceForGame(string gameId, IEnumerable<FileEntryRecord> entries);
}

public interface IDirectoryFingerprintRepository
{
    void UpsertMany(IEnumerable<DirectoryFingerprintRecord> records);
    IReadOnlyList<DirectoryFingerprintRecord> ListByGame(string gameId);
}

public interface IMutablePathRepository
{
    void Create(MutablePathRecord record);
    IReadOnlyList<MutablePathRecord> ListByGame(string gameId);
}

public interface ITaskRepository
{
    void Create(TaskRecord record);
    TaskRecord? GetById(string taskId);
    IReadOnlyList<TaskRecord> ListByGame(string gameId);
    IReadOnlyList<TaskRecord> GetPendingOrInterrupted();
    int MarkStaleActiveTasksInterrupted(DateTimeOffset staleBeforeUtc);
    void UpdateState(string taskId, TaskState state, string? errorCode = null, string? errorMessage = null);
    void UpdateProgress(string taskId, long current, long? total);
}

public interface IConflictRepository
{
    void Create(ConflictRecord record);
    IReadOnlyList<ConflictRecord> GetUnresolvedByGame(string gameId);
    void Resolve(string conflictId, string resolutionPayloadJson);
}

public interface ISaveProfileRepository
{
    void Upsert(SaveProfileRecord record);
    SaveProfileRecord? GetById(string saveProfileId);
    IReadOnlyList<SaveProfileRecord> ListByGame(string gameId);
    void UpdateBackupUtc(string saveProfileId, DateTimeOffset backupUtc);
    void UpdateRestoreUtc(string saveProfileId, DateTimeOffset restoreUtc);
}

public interface ISaveBackupRepository
{
    void Create(SaveBackupRecord record);
    SaveBackupRecord? GetById(string backupId);
    IReadOnlyList<SaveBackupRecord> ListByProfile(string saveProfileId);
}

public interface IQuarantineRepository
{
    void Create(QuarantineEntryRecord record);
    QuarantineEntryRecord? GetById(string quarantineId);
    IReadOnlyList<QuarantineEntryRecord> ListByGame(string gameId);
    IReadOnlyList<QuarantineEntryRecord> ListActive();
    void MarkRestored(string quarantineId, DateTimeOffset restoredUtc);
    void MarkDeleted(string quarantineId, DateTimeOffset deletedUtc);
    void Delete(string quarantineId);
}

public sealed class GameRepository(SqliteConnectionFactory factory) : IGameRepository
{
    public void Create(GameRecord game)
    {
        using var connection = factory.OpenConnection();
        ExecuteUpsert(connection, game, insertOnly: true);
    }

    public GameRecord? GetById(string gameId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM games WHERE game_id = $game_id;";
        command.Parameters.AddWithValue("$game_id", gameId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGame(reader) : null;
    }

    public GameRecord? GetByVniteGameId(string vniteGameId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM games WHERE vnite_game_id = $vnite_game_id;";
        command.Parameters.AddWithValue("$vnite_game_id", vniteGameId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGame(reader) : null;
    }

    public IReadOnlyList<GameRecord> List()
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM games ORDER BY display_name;";
        using var reader = command.ExecuteReader();
        var games = new List<GameRecord>();
        while (reader.Read()) games.Add(ReadGame(reader));
        return games;
    }

    public void Update(GameRecord game)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        AddGameParameters(command, game with { UpdatedUtc = DateTimeOffset.UtcNow });
        command.CommandText = GameUpdateSql + " WHERE game_id = $game_id;";
        if (command.ExecuteNonQuery() == 0) throw new PersistenceException("not_found", $"Game not found: {game.GameId}");
    }

    public void Upsert(GameRecord game)
    {
        using var connection = factory.OpenConnection();
        ExecuteUpsert(connection, game, insertOnly: false);
    }

    public void Delete(string gameId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM games WHERE game_id = $game_id;";
        command.Parameters.AddWithValue("$game_id", gameId);
        command.ExecuteNonQuery();
    }

    private static void ExecuteUpsert(SqliteConnection connection, GameRecord game, bool insertOnly)
    {
        using var command = connection.CreateCommand();
        AddGameParameters(command, game);
        command.CommandText = insertOnly
            ? GameInsertSql
            : GameInsertSql + " ON CONFLICT(game_id) DO UPDATE SET " + GameSetSql + ";";
        command.ExecuteNonQuery();
    }

    private const string GameInsertSql = """
        INSERT INTO games(game_id, vnite_game_id, display_name, source_root, cache_root, executable_path, arguments_json,
        working_directory, launch_policy, cache_policy, verification_policy, source_revision, cache_revision,
        last_full_scan_utc, last_incremental_scan_utc, last_played_utc, cache_size_bytes, is_pinned, cache_status,
        contains_possible_save_data, pending_manual_save_backup, last_cache_refresh_utc,
        management_intent, launch_mode_override, executable_relative_path, working_directory_relative_path,
        created_utc, updated_utc)
        VALUES($game_id, $vnite_game_id, $display_name, $source_root, $cache_root, $executable_path, $arguments_json,
        $working_directory, $launch_policy, $cache_policy, $verification_policy, $source_revision, $cache_revision,
        $last_full_scan_utc, $last_incremental_scan_utc, $last_played_utc, $cache_size_bytes, $is_pinned, $cache_status,
        $contains_possible_save_data, $pending_manual_save_backup, $last_cache_refresh_utc,
        $management_intent, $launch_mode_override, $executable_relative_path, $working_directory_relative_path,
        $created_utc, $updated_utc)
        """;

    private const string GameSetSql = """
        vnite_game_id=excluded.vnite_game_id, display_name=excluded.display_name, source_root=excluded.source_root,
        cache_root=excluded.cache_root, executable_path=excluded.executable_path, arguments_json=excluded.arguments_json,
        working_directory=excluded.working_directory, launch_policy=excluded.launch_policy, cache_policy=excluded.cache_policy,
        verification_policy=excluded.verification_policy, source_revision=excluded.source_revision, cache_revision=excluded.cache_revision,
        last_full_scan_utc=excluded.last_full_scan_utc, last_incremental_scan_utc=excluded.last_incremental_scan_utc,
        last_played_utc=excluded.last_played_utc, cache_size_bytes=excluded.cache_size_bytes, is_pinned=excluded.is_pinned,
        cache_status=excluded.cache_status, contains_possible_save_data=excluded.contains_possible_save_data,
        pending_manual_save_backup=excluded.pending_manual_save_backup, last_cache_refresh_utc=excluded.last_cache_refresh_utc,
        management_intent=excluded.management_intent, launch_mode_override=excluded.launch_mode_override,
        executable_relative_path=excluded.executable_relative_path,
        working_directory_relative_path=excluded.working_directory_relative_path,
        updated_utc=excluded.updated_utc
        """;

    private const string GameUpdateSql = """
        UPDATE games SET
        vnite_game_id=$vnite_game_id, display_name=$display_name, source_root=$source_root,
        cache_root=$cache_root, executable_path=$executable_path, arguments_json=$arguments_json,
        working_directory=$working_directory, launch_policy=$launch_policy, cache_policy=$cache_policy,
        verification_policy=$verification_policy, source_revision=$source_revision, cache_revision=$cache_revision,
        last_full_scan_utc=$last_full_scan_utc, last_incremental_scan_utc=$last_incremental_scan_utc,
        last_played_utc=$last_played_utc, cache_size_bytes=$cache_size_bytes, is_pinned=$is_pinned,
        cache_status=$cache_status, contains_possible_save_data=$contains_possible_save_data,
        pending_manual_save_backup=$pending_manual_save_backup, last_cache_refresh_utc=$last_cache_refresh_utc,
        management_intent=$management_intent, launch_mode_override=$launch_mode_override,
        executable_relative_path=$executable_relative_path,
        working_directory_relative_path=$working_directory_relative_path,
        updated_utc=$updated_utc
        """;

    private static void AddGameParameters(SqliteCommand command, GameRecord game)
    {
        command.Parameters.AddWithValue("$game_id", game.GameId);
        command.Parameters.AddWithValue("$vnite_game_id", (object?)game.VniteGameId ?? DBNull.Value);
        command.Parameters.AddWithValue("$display_name", game.DisplayName);
        command.Parameters.AddWithValue("$source_root", game.SourceRoot);
        command.Parameters.AddWithValue("$cache_root", game.CacheRoot);
        command.Parameters.AddWithValue("$executable_path", game.ExecutablePath);
        command.Parameters.AddWithValue("$arguments_json", game.ArgumentsJson);
        command.Parameters.AddWithValue("$working_directory", game.WorkingDirectory);
        command.Parameters.AddWithValue("$launch_policy", DbConvert.Enum(game.LaunchPolicy));
        command.Parameters.AddWithValue("$cache_policy", DbConvert.Enum(game.CachePolicy));
        command.Parameters.AddWithValue("$verification_policy", DbConvert.Enum(game.VerificationPolicy));
        command.Parameters.AddWithValue("$source_revision", game.SourceRevision);
        command.Parameters.AddWithValue("$cache_revision", game.CacheRevision);
        command.Parameters.AddWithValue("$last_full_scan_utc", (object?)DbConvert.Utc(game.LastFullScanUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_incremental_scan_utc", (object?)DbConvert.Utc(game.LastIncrementalScanUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_played_utc", (object?)DbConvert.Utc(game.LastPlayedUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$cache_size_bytes", game.CacheSizeBytes);
        command.Parameters.AddWithValue("$is_pinned", game.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$cache_status", DbConvert.Enum(game.CacheStatus));
        command.Parameters.AddWithValue("$contains_possible_save_data", game.ContainsPossibleSaveData ? 1 : 0);
        command.Parameters.AddWithValue("$pending_manual_save_backup", game.PendingManualSaveBackup ? 1 : 0);
        command.Parameters.AddWithValue("$last_cache_refresh_utc", (object?)DbConvert.Utc(game.LastCacheRefreshUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$management_intent", DbConvert.Enum(game.ManagementIntent));
        command.Parameters.AddWithValue("$launch_mode_override", DbConvert.Enum(game.LaunchModeOverride));
        command.Parameters.AddWithValue("$executable_relative_path", (object?)game.ExecutableRelativePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$working_directory_relative_path", (object?)game.WorkingDirectoryRelativePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$created_utc", DbConvert.Utc(game.CreatedUtc));
        command.Parameters.AddWithValue("$updated_utc", DbConvert.Utc(game.UpdatedUtc));
    }

    internal static GameRecord ReadGame(SqliteDataReader reader) => new()
    {
        GameId = reader.String("game_id"),
        VniteGameId = reader.NullableString("vnite_game_id"),
        DisplayName = reader.String("display_name"),
        SourceRoot = reader.String("source_root"),
        CacheRoot = reader.String("cache_root"),
        ExecutablePath = reader.String("executable_path"),
        ArgumentsJson = reader.String("arguments_json"),
        WorkingDirectory = reader.String("working_directory"),
        LaunchPolicy = DbConvert.Enum<LaunchPolicy>(reader.String("launch_policy")),
        CachePolicy = DbConvert.Enum<CachePolicy>(reader.String("cache_policy")),
        VerificationPolicy = DbConvert.Enum<VerificationPolicy>(reader.String("verification_policy")),
        SourceRevision = reader.Int64("source_revision"),
        CacheRevision = reader.Int64("cache_revision"),
        LastFullScanUtc = DbConvert.DateOrNull(reader.NullableString("last_full_scan_utc")),
        LastIncrementalScanUtc = DbConvert.DateOrNull(reader.NullableString("last_incremental_scan_utc")),
        LastPlayedUtc = DbConvert.DateOrNull(reader.NullableString("last_played_utc")),
        CacheSizeBytes = reader.Int64("cache_size_bytes"),
        IsPinned = reader.Int64("is_pinned") == 1,
        ContainsPossibleSaveData = reader.Int64("contains_possible_save_data") == 1,
        PendingManualSaveBackup = reader.Int64("pending_manual_save_backup") == 1,
        LastCacheRefreshUtc = DbConvert.DateOrNull(reader.NullableString("last_cache_refresh_utc")),
        ManagementIntent = DbConvert.Enum<ManagementIntent>(reader.String("management_intent")),
        LaunchModeOverride = DbConvert.Enum<GameLaunchModeOverride>(reader.String("launch_mode_override")),
        ExecutableRelativePath = reader.NullableString("executable_relative_path"),
        WorkingDirectoryRelativePath = reader.NullableString("working_directory_relative_path"),
        CacheStatus = DbConvert.Enum<CacheStatus>(reader.String("cache_status")),
        CreatedUtc = DbConvert.Date(reader.String("created_utc")),
        UpdatedUtc = DbConvert.Date(reader.String("updated_utc"))
    };
}

public sealed class LaunchCandidateRepository(SqliteConnectionFactory factory) : ILaunchCandidateRepository
{
    public void ReplaceForGame(string gameId, IEnumerable<GameLaunchCandidateRecord> candidates)
    {
        using var connection = factory.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM game_launch_candidates WHERE game_id=$game_id;";
            delete.Parameters.AddWithValue("$game_id", gameId);
            delete.ExecuteNonQuery();
        }

        foreach (var candidate in candidates)
        {
            UpsertOne(connection, transaction, candidate with { GameId = gameId });
        }

        transaction.Commit();
    }

    public void Upsert(GameLaunchCandidateRecord candidate)
    {
        using var connection = factory.OpenConnection();
        using var transaction = connection.BeginTransaction();
        UpsertOne(connection, transaction, candidate);
        transaction.Commit();
    }

    public IReadOnlyList<GameLaunchCandidateRecord> ListByGame(string gameId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM game_launch_candidates WHERE game_id=$game_id ORDER BY is_primary DESC, is_ignored ASC, score DESC, confidence DESC, relative_path;";
        command.Parameters.AddWithValue("$game_id", gameId);
        using var reader = command.ExecuteReader();
        var records = new List<GameLaunchCandidateRecord>();
        while (reader.Read()) records.Add(Read(reader));
        return records;
    }

    public GameLaunchCandidateRecord? GetPrimary(string gameId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM game_launch_candidates WHERE game_id=$game_id AND is_primary=1 LIMIT 1;";
        command.Parameters.AddWithValue("$game_id", gameId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public void MarkPrimary(string gameId, string candidateId, bool userConfirmed)
    {
        using var connection = factory.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "UPDATE game_launch_candidates SET is_primary=0, updated_utc=$updated_utc WHERE game_id=$game_id;";
            clear.Parameters.AddWithValue("$game_id", gameId);
            clear.Parameters.AddWithValue("$updated_utc", DbConvert.Utc(DateTimeOffset.UtcNow));
            clear.ExecuteNonQuery();
        }

        using (var mark = connection.CreateCommand())
        {
            mark.Transaction = transaction;
            mark.CommandText = "UPDATE game_launch_candidates SET is_primary=1, user_confirmed=$user_confirmed, updated_utc=$updated_utc WHERE game_id=$game_id AND candidate_id=$candidate_id;";
            mark.Parameters.AddWithValue("$game_id", gameId);
            mark.Parameters.AddWithValue("$candidate_id", candidateId);
            mark.Parameters.AddWithValue("$user_confirmed", userConfirmed ? 1 : 0);
            mark.Parameters.AddWithValue("$updated_utc", DbConvert.Utc(DateTimeOffset.UtcNow));
            if (mark.ExecuteNonQuery() == 0) throw new PersistenceException("candidate_not_found", $"Launch candidate not found: {candidateId}");
        }

        transaction.Commit();
    }

    public void SetIgnored(string gameId, string candidateId, bool ignored)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE game_launch_candidates SET is_ignored=$is_ignored, is_primary=CASE WHEN $is_ignored=1 THEN 0 ELSE is_primary END, updated_utc=$updated_utc WHERE game_id=$game_id AND candidate_id=$candidate_id;";
        command.Parameters.AddWithValue("$game_id", gameId);
        command.Parameters.AddWithValue("$candidate_id", candidateId);
        command.Parameters.AddWithValue("$is_ignored", ignored ? 1 : 0);
        command.Parameters.AddWithValue("$updated_utc", DbConvert.Utc(DateTimeOffset.UtcNow));
        if (command.ExecuteNonQuery() == 0) throw new PersistenceException("candidate_not_found", $"Launch candidate not found: {candidateId}");
    }

    private static void UpsertOne(SqliteConnection connection, SqliteTransaction transaction, GameLaunchCandidateRecord candidate)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO game_launch_candidates(candidate_id, game_id, relative_path, candidate_type, candidate_role,
            candidate_source, score, confidence, is_primary, fallback_priority, is_ignored, user_confirmed,
            file_size, last_write_utc, metadata_fingerprint, last_test_status, last_test_utc, last_launch_session_id,
            metadata_json, reasons_json, exclusion_reasons_json, created_utc, updated_utc)
            VALUES($candidate_id, $game_id, $relative_path, $candidate_type, $candidate_role,
            $candidate_source, $score, $confidence, $is_primary, $fallback_priority, $is_ignored, $user_confirmed,
            $file_size, $last_write_utc, $metadata_fingerprint, $last_test_status, $last_test_utc, $last_launch_session_id,
            $metadata_json, $reasons_json, $exclusion_reasons_json, $created_utc, $updated_utc)
            ON CONFLICT(game_id, relative_path, candidate_type) DO UPDATE SET
            candidate_role=excluded.candidate_role, candidate_source=excluded.candidate_source,
            score=excluded.score, confidence=excluded.confidence, is_primary=excluded.is_primary,
            fallback_priority=excluded.fallback_priority, is_ignored=excluded.is_ignored,
            user_confirmed=excluded.user_confirmed, file_size=excluded.file_size,
            last_write_utc=excluded.last_write_utc, metadata_fingerprint=excluded.metadata_fingerprint,
            last_test_status=excluded.last_test_status, last_test_utc=excluded.last_test_utc,
            last_launch_session_id=excluded.last_launch_session_id, metadata_json=excluded.metadata_json,
            reasons_json=excluded.reasons_json, exclusion_reasons_json=excluded.exclusion_reasons_json,
            updated_utc=excluded.updated_utc;
            """;
        Add(command, candidate);
        command.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand command, GameLaunchCandidateRecord candidate)
    {
        command.Parameters.AddWithValue("$candidate_id", candidate.CandidateId);
        command.Parameters.AddWithValue("$game_id", candidate.GameId);
        command.Parameters.AddWithValue("$relative_path", candidate.RelativePath);
        command.Parameters.AddWithValue("$candidate_type", DbConvert.Enum(candidate.CandidateType));
        command.Parameters.AddWithValue("$candidate_role", DbConvert.Enum(candidate.CandidateRole));
        command.Parameters.AddWithValue("$candidate_source", DbConvert.Enum(candidate.CandidateSource));
        command.Parameters.AddWithValue("$score", candidate.Score);
        command.Parameters.AddWithValue("$confidence", candidate.Confidence);
        command.Parameters.AddWithValue("$is_primary", candidate.IsPrimary ? 1 : 0);
        command.Parameters.AddWithValue("$fallback_priority", (object?)candidate.FallbackPriority ?? DBNull.Value);
        command.Parameters.AddWithValue("$is_ignored", candidate.IsIgnored ? 1 : 0);
        command.Parameters.AddWithValue("$user_confirmed", candidate.UserConfirmed ? 1 : 0);
        command.Parameters.AddWithValue("$file_size", candidate.FileSize);
        command.Parameters.AddWithValue("$last_write_utc", (object?)DbConvert.Utc(candidate.LastWriteUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$metadata_fingerprint", candidate.MetadataFingerprint);
        command.Parameters.AddWithValue("$last_test_status", DbConvert.Enum(candidate.LastTestStatus));
        command.Parameters.AddWithValue("$last_test_utc", (object?)DbConvert.Utc(candidate.LastTestUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_launch_session_id", (object?)candidate.LastLaunchSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$metadata_json", candidate.MetadataJson);
        command.Parameters.AddWithValue("$reasons_json", candidate.ReasonsJson);
        command.Parameters.AddWithValue("$exclusion_reasons_json", candidate.ExclusionReasonsJson);
        command.Parameters.AddWithValue("$created_utc", DbConvert.Utc(candidate.CreatedUtc));
        command.Parameters.AddWithValue("$updated_utc", DbConvert.Utc(candidate.UpdatedUtc));
    }

    private static GameLaunchCandidateRecord Read(SqliteDataReader reader) => new()
    {
        CandidateId = reader.String("candidate_id"),
        GameId = reader.String("game_id"),
        RelativePath = reader.String("relative_path"),
        CandidateType = DbConvert.Enum<LaunchCandidateType>(reader.String("candidate_type")),
        CandidateRole = DbConvert.Enum<LaunchCandidateRole>(reader.String("candidate_role")),
        CandidateSource = DbConvert.Enum<LaunchCandidateSource>(reader.String("candidate_source")),
        Score = (int)reader.Int64("score"),
        Confidence = (int)reader.Int64("confidence"),
        IsPrimary = reader.Int64("is_primary") == 1,
        FallbackPriority = reader.NullableInt64("fallback_priority") is { } priority ? (int)priority : null,
        IsIgnored = reader.Int64("is_ignored") == 1,
        UserConfirmed = reader.Int64("user_confirmed") == 1,
        FileSize = reader.Int64("file_size"),
        LastWriteUtc = DbConvert.DateOrNull(reader.NullableString("last_write_utc")),
        MetadataFingerprint = reader.String("metadata_fingerprint"),
        LastTestStatus = DbConvert.Enum<LaunchCandidateTestStatus>(reader.String("last_test_status")),
        LastTestUtc = DbConvert.DateOrNull(reader.NullableString("last_test_utc")),
        LastLaunchSessionId = reader.NullableString("last_launch_session_id"),
        MetadataJson = reader.String("metadata_json"),
        ReasonsJson = reader.String("reasons_json"),
        ExclusionReasonsJson = reader.String("exclusion_reasons_json"),
        CreatedUtc = DbConvert.Date(reader.String("created_utc")),
        UpdatedUtc = DbConvert.Date(reader.String("updated_utc"))
    };
}

public sealed class FileEntryRepository(SqliteConnectionFactory factory) : IFileEntryRepository
{
    public void UpsertMany(IEnumerable<FileEntryRecord> entries)
    {
        using var connection = factory.OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var entry in entries)
        {
            UpsertOne(connection, transaction, entry);
        }
        transaction.Commit();
    }

    public IReadOnlyList<FileEntryRecord> ListByGame(string gameId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM file_entries WHERE game_id = $game_id ORDER BY relative_path;";
        command.Parameters.AddWithValue("$game_id", gameId);
        using var reader = command.ExecuteReader();
        var records = new List<FileEntryRecord>();
        while (reader.Read()) records.Add(ReadFileEntry(reader));
        return records;
    }

    public FileEntryRecord? Get(string gameId, string relativePath)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM file_entries WHERE game_id = $game_id AND relative_path = $relative_path;";
        command.Parameters.AddWithValue("$game_id", gameId);
        command.Parameters.AddWithValue("$relative_path", relativePath);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadFileEntry(reader) : null;
    }

    public void DeleteByGame(string gameId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM file_entries WHERE game_id = $game_id;";
        command.Parameters.AddWithValue("$game_id", gameId);
        command.ExecuteNonQuery();
    }

    public void ReplaceForGame(string gameId, IEnumerable<FileEntryRecord> entries)
    {
        using var connection = factory.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM file_entries WHERE game_id = $game_id;";
            delete.Parameters.AddWithValue("$game_id", gameId);
            delete.ExecuteNonQuery();
        }
        foreach (var entry in entries)
        {
            if (entry.GameId != gameId) throw new PersistenceException("invalid_game_id", "Replacement entries must match the target game_id.");
            UpsertOne(connection, transaction, entry);
        }
        transaction.Commit();
    }

    private static void UpsertOne(SqliteConnection connection, SqliteTransaction transaction, FileEntryRecord entry)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO file_entries(game_id, relative_path, size_bytes, modified_utc, file_role, quick_hash, full_hash,
            source_revision, cache_revision, created_utc, updated_utc)
            VALUES($game_id, $relative_path, $size_bytes, $modified_utc, $file_role, $quick_hash, $full_hash,
            $source_revision, $cache_revision, $created_utc, $updated_utc)
            ON CONFLICT(game_id, relative_path) DO UPDATE SET
            size_bytes=excluded.size_bytes, modified_utc=excluded.modified_utc, file_role=excluded.file_role,
            quick_hash=excluded.quick_hash, full_hash=excluded.full_hash, source_revision=excluded.source_revision,
            cache_revision=excluded.cache_revision, updated_utc=excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$game_id", entry.GameId);
        command.Parameters.AddWithValue("$relative_path", entry.RelativePath);
        command.Parameters.AddWithValue("$size_bytes", entry.SizeBytes);
        command.Parameters.AddWithValue("$modified_utc", DbConvert.Utc(entry.ModifiedUtc));
        command.Parameters.AddWithValue("$file_role", DbConvert.Enum(entry.FileRole));
        command.Parameters.AddWithValue("$quick_hash", (object?)entry.QuickHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$full_hash", (object?)entry.FullHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$source_revision", entry.SourceRevision);
        command.Parameters.AddWithValue("$cache_revision", entry.CacheRevision);
        command.Parameters.AddWithValue("$created_utc", DbConvert.Utc(entry.CreatedUtc));
        command.Parameters.AddWithValue("$updated_utc", DbConvert.Utc(entry.UpdatedUtc));
        command.ExecuteNonQuery();
    }

    private static FileEntryRecord ReadFileEntry(SqliteDataReader reader) => new()
    {
        GameId = reader.String("game_id"),
        RelativePath = reader.String("relative_path"),
        SizeBytes = reader.Int64("size_bytes"),
        ModifiedUtc = DbConvert.Date(reader.String("modified_utc")),
        FileRole = DbConvert.Enum<FileRole>(reader.String("file_role")),
        QuickHash = reader.NullableString("quick_hash"),
        FullHash = reader.NullableString("full_hash"),
        SourceRevision = reader.Int64("source_revision"),
        CacheRevision = reader.Int64("cache_revision"),
        CreatedUtc = DbConvert.Date(reader.String("created_utc")),
        UpdatedUtc = DbConvert.Date(reader.String("updated_utc"))
    };
}

public sealed class DirectoryFingerprintRepository(SqliteConnectionFactory factory) : IDirectoryFingerprintRepository
{
    public void UpsertMany(IEnumerable<DirectoryFingerprintRecord> records)
    {
        using var connection = factory.OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var record in records)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO directory_fingerprints(game_id, relative_path, direct_file_count, direct_directory_count,
                aggregate_size_bytes, latest_known_modified_utc, source_revision, created_utc, updated_utc)
                VALUES($game_id, $relative_path, $direct_file_count, $direct_directory_count, $aggregate_size_bytes,
                $latest_known_modified_utc, $source_revision, $created_utc, $updated_utc)
                ON CONFLICT(game_id, relative_path) DO UPDATE SET
                direct_file_count=excluded.direct_file_count, direct_directory_count=excluded.direct_directory_count,
                aggregate_size_bytes=excluded.aggregate_size_bytes, latest_known_modified_utc=excluded.latest_known_modified_utc,
                source_revision=excluded.source_revision, updated_utc=excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$game_id", record.GameId);
            command.Parameters.AddWithValue("$relative_path", record.RelativePath);
            command.Parameters.AddWithValue("$direct_file_count", record.DirectFileCount);
            command.Parameters.AddWithValue("$direct_directory_count", record.DirectDirectoryCount);
            command.Parameters.AddWithValue("$aggregate_size_bytes", record.AggregateSizeBytes);
            command.Parameters.AddWithValue("$latest_known_modified_utc", (object?)DbConvert.Utc(record.LatestKnownModifiedUtc) ?? DBNull.Value);
            command.Parameters.AddWithValue("$source_revision", record.SourceRevision);
            command.Parameters.AddWithValue("$created_utc", DbConvert.Utc(record.CreatedUtc));
            command.Parameters.AddWithValue("$updated_utc", DbConvert.Utc(record.UpdatedUtc));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<DirectoryFingerprintRecord> ListByGame(string gameId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM directory_fingerprints WHERE game_id = $game_id ORDER BY relative_path;";
        command.Parameters.AddWithValue("$game_id", gameId);
        using var reader = command.ExecuteReader();
        var records = new List<DirectoryFingerprintRecord>();
        while (reader.Read())
        {
            records.Add(new DirectoryFingerprintRecord
            {
                GameId = reader.String("game_id"),
                RelativePath = reader.String("relative_path"),
                DirectFileCount = reader.Int64("direct_file_count"),
                DirectDirectoryCount = reader.Int64("direct_directory_count"),
                AggregateSizeBytes = reader.Int64("aggregate_size_bytes"),
                LatestKnownModifiedUtc = DbConvert.DateOrNull(reader.NullableString("latest_known_modified_utc")),
                SourceRevision = reader.Int64("source_revision"),
                CreatedUtc = DbConvert.Date(reader.String("created_utc")),
                UpdatedUtc = DbConvert.Date(reader.String("updated_utc"))
            });
        }
        return records;
    }
}

public sealed class MutablePathRepository(SqliteConnectionFactory factory) : IMutablePathRepository
{
    public void Create(MutablePathRecord record)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mutable_paths(mutable_path_id, game_id, path_template, path_kind, sync_mode, snapshot_enabled,
            conflict_policy, exclude_patterns_json, created_utc, updated_utc)
            VALUES($mutable_path_id, $game_id, $path_template, $path_kind, $sync_mode, $snapshot_enabled,
            $conflict_policy, $exclude_patterns_json, $created_utc, $updated_utc);
            """;
        command.Parameters.AddWithValue("$mutable_path_id", record.MutablePathId);
        command.Parameters.AddWithValue("$game_id", record.GameId);
        command.Parameters.AddWithValue("$path_template", record.PathTemplate);
        command.Parameters.AddWithValue("$path_kind", DbConvert.Enum(record.PathKind));
        command.Parameters.AddWithValue("$sync_mode", DbConvert.Enum(record.SyncMode));
        command.Parameters.AddWithValue("$snapshot_enabled", record.SnapshotEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$conflict_policy", DbConvert.Enum(record.ConflictPolicy));
        command.Parameters.AddWithValue("$exclude_patterns_json", record.ExcludePatternsJson);
        command.Parameters.AddWithValue("$created_utc", DbConvert.Utc(record.CreatedUtc));
        command.Parameters.AddWithValue("$updated_utc", DbConvert.Utc(record.UpdatedUtc));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<MutablePathRecord> ListByGame(string gameId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM mutable_paths WHERE game_id = $game_id ORDER BY path_template;";
        command.Parameters.AddWithValue("$game_id", gameId);
        using var reader = command.ExecuteReader();
        var records = new List<MutablePathRecord>();
        while (reader.Read())
        {
            records.Add(new MutablePathRecord
            {
                MutablePathId = reader.String("mutable_path_id"),
                GameId = reader.String("game_id"),
                PathTemplate = reader.String("path_template"),
                PathKind = DbConvert.Enum<PathKind>(reader.String("path_kind")),
                SyncMode = DbConvert.Enum<SyncMode>(reader.String("sync_mode")),
                SnapshotEnabled = reader.Int64("snapshot_enabled") == 1,
                ConflictPolicy = DbConvert.Enum<ConflictPolicy>(reader.String("conflict_policy")),
                ExcludePatternsJson = reader.String("exclude_patterns_json"),
                CreatedUtc = DbConvert.Date(reader.String("created_utc")),
                UpdatedUtc = DbConvert.Date(reader.String("updated_utc"))
            });
        }
        return records;
    }
}

public sealed class TaskRepository(SqliteConnectionFactory factory) : ITaskRepository
{
    public void Create(TaskRecord record)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO task_records(task_id, game_id, task_type, state, created_utc, updated_utc, started_utc, completed_utc,
            progress_current, progress_total, retry_count, last_error_code, last_error_message, recovery_payload_json)
            VALUES($task_id, $game_id, $task_type, $state, $created_utc, $updated_utc, $started_utc, $completed_utc,
            $progress_current, $progress_total, $retry_count, $last_error_code, $last_error_message, $recovery_payload_json);
            """;
        AddTaskParameters(command, record);
        command.ExecuteNonQuery();
    }

    public TaskRecord? GetById(string taskId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM task_records WHERE task_id = $task_id;";
        command.Parameters.AddWithValue("$task_id", taskId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTask(reader) : null;
    }

    public IReadOnlyList<TaskRecord> ListByGame(string gameId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM task_records WHERE game_id = $game_id ORDER BY created_utc;";
        command.Parameters.AddWithValue("$game_id", gameId);
        using var reader = command.ExecuteReader();
        var records = new List<TaskRecord>();
        while (reader.Read()) records.Add(ReadTask(reader));
        return records;
    }

    public IReadOnlyList<TaskRecord> GetPendingOrInterrupted()
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM task_records WHERE state IN ('Pending','Interrupted') ORDER BY updated_utc;";
        using var reader = command.ExecuteReader();
        var records = new List<TaskRecord>();
        while (reader.Read()) records.Add(ReadTask(reader));
        return records;
    }

    public int MarkStaleActiveTasksInterrupted(DateTimeOffset staleBeforeUtc)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE task_records
            SET state='Interrupted',
                updated_utc=$updated_utc,
                last_error_code='stale_task_recovered',
                last_error_message='Active cache task was stale after Agent restart.'
            WHERE state IN ('Checking','Copying','Verifying') AND updated_utc < $stale_before_utc;
            """;
        command.Parameters.AddWithValue("$updated_utc", DbConvert.Utc(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$stale_before_utc", DbConvert.Utc(staleBeforeUtc));
        return command.ExecuteNonQuery();
    }

    public void UpdateState(string taskId, TaskState state, string? errorCode = null, string? errorMessage = null)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE task_records SET state=$state, updated_utc=$updated_utc,
            started_utc=COALESCE(started_utc, CASE WHEN $state IN ('Checking','Launching','Running') THEN $updated_utc ELSE started_utc END),
            completed_utc=CASE WHEN $state IN ('Completed','Failed','Cancelled','Conflict') THEN $updated_utc ELSE completed_utc END,
            last_error_code=$last_error_code, last_error_message=$last_error_message
            WHERE task_id=$task_id;
            """;
        command.Parameters.AddWithValue("$task_id", taskId);
        command.Parameters.AddWithValue("$state", DbConvert.Enum(state));
        command.Parameters.AddWithValue("$updated_utc", DbConvert.Utc(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$last_error_code", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_error_message", (object?)errorMessage ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void UpdateProgress(string taskId, long current, long? total)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE task_records SET progress_current=$current, progress_total=$total, updated_utc=$updated_utc WHERE task_id=$task_id;";
        command.Parameters.AddWithValue("$task_id", taskId);
        command.Parameters.AddWithValue("$current", current);
        command.Parameters.AddWithValue("$total", (object?)total ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated_utc", DbConvert.Utc(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    internal static void AddTaskParameters(SqliteCommand command, TaskRecord record)
    {
        command.Parameters.AddWithValue("$task_id", record.TaskId);
        command.Parameters.AddWithValue("$game_id", (object?)record.GameId ?? DBNull.Value);
        command.Parameters.AddWithValue("$task_type", record.TaskType);
        command.Parameters.AddWithValue("$state", DbConvert.Enum(record.State));
        command.Parameters.AddWithValue("$created_utc", DbConvert.Utc(record.CreatedUtc));
        command.Parameters.AddWithValue("$updated_utc", DbConvert.Utc(record.UpdatedUtc));
        command.Parameters.AddWithValue("$started_utc", (object?)DbConvert.Utc(record.StartedUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$completed_utc", (object?)DbConvert.Utc(record.CompletedUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$progress_current", record.ProgressCurrent);
        command.Parameters.AddWithValue("$progress_total", (object?)record.ProgressTotal ?? DBNull.Value);
        command.Parameters.AddWithValue("$retry_count", record.RetryCount);
        command.Parameters.AddWithValue("$last_error_code", (object?)record.LastErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_error_message", (object?)record.LastErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$recovery_payload_json", (object?)record.RecoveryPayloadJson ?? DBNull.Value);
    }

    internal static TaskRecord ReadTask(SqliteDataReader reader) => new()
    {
        TaskId = reader.String("task_id"),
        GameId = reader.NullableString("game_id"),
        TaskType = reader.String("task_type"),
        State = DbConvert.Enum<TaskState>(reader.String("state")),
        CreatedUtc = DbConvert.Date(reader.String("created_utc")),
        UpdatedUtc = DbConvert.Date(reader.String("updated_utc")),
        StartedUtc = DbConvert.DateOrNull(reader.NullableString("started_utc")),
        CompletedUtc = DbConvert.DateOrNull(reader.NullableString("completed_utc")),
        ProgressCurrent = reader.Int64("progress_current"),
        ProgressTotal = reader.NullableInt64("progress_total"),
        RetryCount = (int)reader.Int64("retry_count"),
        LastErrorCode = reader.NullableString("last_error_code"),
        LastErrorMessage = reader.NullableString("last_error_message"),
        RecoveryPayloadJson = reader.NullableString("recovery_payload_json")
    };
}

public sealed class ConflictRepository(SqliteConnectionFactory factory) : IConflictRepository
{
    public void Create(ConflictRecord record)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conflict_records(conflict_id, game_id, mutable_path_id, relative_path, local_version_json,
            nas_version_json, detected_utc, resolution_state, resolved_utc, resolution_payload_json)
            VALUES($conflict_id, $game_id, $mutable_path_id, $relative_path, $local_version_json,
            $nas_version_json, $detected_utc, $resolution_state, $resolved_utc, $resolution_payload_json);
            """;
        command.Parameters.AddWithValue("$conflict_id", record.ConflictId);
        command.Parameters.AddWithValue("$game_id", record.GameId);
        command.Parameters.AddWithValue("$mutable_path_id", record.MutablePathId);
        command.Parameters.AddWithValue("$relative_path", record.RelativePath);
        command.Parameters.AddWithValue("$local_version_json", record.LocalVersionJson);
        command.Parameters.AddWithValue("$nas_version_json", record.NasVersionJson);
        command.Parameters.AddWithValue("$detected_utc", DbConvert.Utc(record.DetectedUtc));
        command.Parameters.AddWithValue("$resolution_state", DbConvert.Enum(record.ResolutionState));
        command.Parameters.AddWithValue("$resolved_utc", (object?)DbConvert.Utc(record.ResolvedUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$resolution_payload_json", (object?)record.ResolutionPayloadJson ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ConflictRecord> GetUnresolvedByGame(string gameId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM conflict_records WHERE game_id=$game_id AND resolution_state='Unresolved' ORDER BY detected_utc;";
        command.Parameters.AddWithValue("$game_id", gameId);
        using var reader = command.ExecuteReader();
        var records = new List<ConflictRecord>();
        while (reader.Read()) records.Add(ReadConflict(reader));
        return records;
    }

    public void Resolve(string conflictId, string resolutionPayloadJson)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conflict_records SET resolution_state='Resolved', resolved_utc=$resolved_utc, resolution_payload_json=$payload WHERE conflict_id=$conflict_id;";
        command.Parameters.AddWithValue("$conflict_id", conflictId);
        command.Parameters.AddWithValue("$resolved_utc", DbConvert.Utc(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$payload", resolutionPayloadJson);
        command.ExecuteNonQuery();
    }

    private static ConflictRecord ReadConflict(SqliteDataReader reader) => new()
    {
        ConflictId = reader.String("conflict_id"),
        GameId = reader.String("game_id"),
        MutablePathId = reader.String("mutable_path_id"),
        RelativePath = reader.String("relative_path"),
        LocalVersionJson = reader.String("local_version_json"),
        NasVersionJson = reader.String("nas_version_json"),
        DetectedUtc = DbConvert.Date(reader.String("detected_utc")),
        ResolutionState = DbConvert.Enum<ResolutionState>(reader.String("resolution_state")),
        ResolvedUtc = DbConvert.DateOrNull(reader.NullableString("resolved_utc")),
        ResolutionPayloadJson = reader.NullableString("resolution_payload_json")
    };
}

public sealed class SaveProfileRepository(SqliteConnectionFactory factory) : ISaveProfileRepository
{
    public void Upsert(SaveProfileRecord record)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO save_profiles(save_profile_id, game_id, display_name, source_type, path_template, resolved_path,
            include_patterns_json, exclude_patterns_json, backup_root, last_backup_utc, last_restore_utc, is_primary,
            is_enabled, created_utc, updated_utc)
            VALUES($save_profile_id, $game_id, $display_name, $source_type, $path_template, $resolved_path,
            $include_patterns_json, $exclude_patterns_json, $backup_root, $last_backup_utc, $last_restore_utc,
            $is_primary, $is_enabled, $created_utc, $updated_utc)
            ON CONFLICT(save_profile_id) DO UPDATE SET
            display_name=excluded.display_name, source_type=excluded.source_type, path_template=excluded.path_template,
            resolved_path=excluded.resolved_path, include_patterns_json=excluded.include_patterns_json,
            exclude_patterns_json=excluded.exclude_patterns_json, backup_root=excluded.backup_root,
            last_backup_utc=excluded.last_backup_utc, last_restore_utc=excluded.last_restore_utc,
            is_primary=excluded.is_primary, is_enabled=excluded.is_enabled, updated_utc=excluded.updated_utc;
            """;
        Add(command, record);
        command.ExecuteNonQuery();
    }

    public SaveProfileRecord? GetById(string saveProfileId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM save_profiles WHERE save_profile_id=$save_profile_id;";
        command.Parameters.AddWithValue("$save_profile_id", saveProfileId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<SaveProfileRecord> ListByGame(string gameId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM save_profiles WHERE game_id=$game_id ORDER BY is_primary DESC, display_name;";
        command.Parameters.AddWithValue("$game_id", gameId);
        using var reader = command.ExecuteReader();
        var records = new List<SaveProfileRecord>();
        while (reader.Read()) records.Add(Read(reader));
        return records;
    }

    public void UpdateBackupUtc(string saveProfileId, DateTimeOffset backupUtc) => UpdateUtc(saveProfileId, "last_backup_utc", backupUtc);
    public void UpdateRestoreUtc(string saveProfileId, DateTimeOffset restoreUtc) => UpdateUtc(saveProfileId, "last_restore_utc", restoreUtc);

    private void UpdateUtc(string saveProfileId, string column, DateTimeOffset value)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE save_profiles SET {column}=$value, updated_utc=$updated_utc WHERE save_profile_id=$save_profile_id;";
        command.Parameters.AddWithValue("$save_profile_id", saveProfileId);
        command.Parameters.AddWithValue("$value", DbConvert.Utc(value));
        command.Parameters.AddWithValue("$updated_utc", DbConvert.Utc(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand command, SaveProfileRecord record)
    {
        command.Parameters.AddWithValue("$save_profile_id", record.SaveProfileId);
        command.Parameters.AddWithValue("$game_id", record.GameId);
        command.Parameters.AddWithValue("$display_name", record.DisplayName);
        command.Parameters.AddWithValue("$source_type", DbConvert.Enum(record.SourceType));
        command.Parameters.AddWithValue("$path_template", record.PathTemplate);
        command.Parameters.AddWithValue("$resolved_path", record.ResolvedPath);
        command.Parameters.AddWithValue("$include_patterns_json", record.IncludePatternsJson);
        command.Parameters.AddWithValue("$exclude_patterns_json", record.ExcludePatternsJson);
        command.Parameters.AddWithValue("$backup_root", record.BackupRoot);
        command.Parameters.AddWithValue("$last_backup_utc", (object?)DbConvert.Utc(record.LastBackupUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_restore_utc", (object?)DbConvert.Utc(record.LastRestoreUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$is_primary", record.IsPrimary ? 1 : 0);
        command.Parameters.AddWithValue("$is_enabled", record.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$created_utc", DbConvert.Utc(record.CreatedUtc));
        command.Parameters.AddWithValue("$updated_utc", DbConvert.Utc(record.UpdatedUtc));
    }

    private static SaveProfileRecord Read(SqliteDataReader reader) => new()
    {
        SaveProfileId = reader.String("save_profile_id"),
        GameId = reader.String("game_id"),
        DisplayName = reader.String("display_name"),
        SourceType = DbConvert.Enum<SaveProfileSourceType>(reader.String("source_type")),
        PathTemplate = reader.String("path_template"),
        ResolvedPath = reader.String("resolved_path"),
        IncludePatternsJson = reader.String("include_patterns_json"),
        ExcludePatternsJson = reader.String("exclude_patterns_json"),
        BackupRoot = reader.String("backup_root"),
        LastBackupUtc = DbConvert.DateOrNull(reader.NullableString("last_backup_utc")),
        LastRestoreUtc = DbConvert.DateOrNull(reader.NullableString("last_restore_utc")),
        IsPrimary = reader.Int64("is_primary") == 1,
        IsEnabled = reader.Int64("is_enabled") == 1,
        CreatedUtc = DbConvert.Date(reader.String("created_utc")),
        UpdatedUtc = DbConvert.Date(reader.String("updated_utc"))
    };
}

public sealed class SaveBackupRepository(SqliteConnectionFactory factory) : ISaveBackupRepository
{
    public void Create(SaveBackupRecord record)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO save_backups(backup_id, save_profile_id, game_id, backup_path, backup_type, created_utc,
            source_path, file_count, total_bytes, note, is_pinned)
            VALUES($backup_id, $save_profile_id, $game_id, $backup_path, $backup_type, $created_utc,
            $source_path, $file_count, $total_bytes, $note, $is_pinned);
            """;
        command.Parameters.AddWithValue("$backup_id", record.BackupId);
        command.Parameters.AddWithValue("$save_profile_id", record.SaveProfileId);
        command.Parameters.AddWithValue("$game_id", record.GameId);
        command.Parameters.AddWithValue("$backup_path", record.BackupPath);
        command.Parameters.AddWithValue("$backup_type", DbConvert.Enum(record.BackupType));
        command.Parameters.AddWithValue("$created_utc", DbConvert.Utc(record.CreatedUtc));
        command.Parameters.AddWithValue("$source_path", record.SourcePath);
        command.Parameters.AddWithValue("$file_count", record.FileCount);
        command.Parameters.AddWithValue("$total_bytes", record.TotalBytes);
        command.Parameters.AddWithValue("$note", (object?)record.Note ?? DBNull.Value);
        command.Parameters.AddWithValue("$is_pinned", record.IsPinned ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public SaveBackupRecord? GetById(string backupId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM save_backups WHERE backup_id=$backup_id;";
        command.Parameters.AddWithValue("$backup_id", backupId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<SaveBackupRecord> ListByProfile(string saveProfileId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM save_backups WHERE save_profile_id=$save_profile_id ORDER BY created_utc DESC;";
        command.Parameters.AddWithValue("$save_profile_id", saveProfileId);
        using var reader = command.ExecuteReader();
        var records = new List<SaveBackupRecord>();
        while (reader.Read()) records.Add(Read(reader));
        return records;
    }

    private static SaveBackupRecord Read(SqliteDataReader reader) => new()
    {
        BackupId = reader.String("backup_id"),
        SaveProfileId = reader.String("save_profile_id"),
        GameId = reader.String("game_id"),
        BackupPath = reader.String("backup_path"),
        BackupType = DbConvert.Enum<SaveBackupType>(reader.String("backup_type")),
        CreatedUtc = DbConvert.Date(reader.String("created_utc")),
        SourcePath = reader.String("source_path"),
        FileCount = reader.Int64("file_count"),
        TotalBytes = reader.Int64("total_bytes"),
        Note = reader.NullableString("note"),
        IsPinned = reader.Int64("is_pinned") == 1
    };
}

public sealed class QuarantineRepository(SqliteConnectionFactory factory) : IQuarantineRepository
{
    public void Create(QuarantineEntryRecord record)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO quarantine_entries(quarantine_id, game_id, original_cache_root, quarantine_path, reason,
            created_utc, restored_utc, deleted_utc, size_bytes, file_count, contains_possible_save_data)
            VALUES($quarantine_id, $game_id, $original_cache_root, $quarantine_path, $reason,
            $created_utc, $restored_utc, $deleted_utc, $size_bytes, $file_count, $contains_possible_save_data);
            """;
        command.Parameters.AddWithValue("$quarantine_id", record.QuarantineId);
        command.Parameters.AddWithValue("$game_id", record.GameId);
        command.Parameters.AddWithValue("$original_cache_root", record.OriginalCacheRoot);
        command.Parameters.AddWithValue("$quarantine_path", record.QuarantinePath);
        command.Parameters.AddWithValue("$reason", record.Reason);
        command.Parameters.AddWithValue("$created_utc", DbConvert.Utc(record.CreatedUtc));
        command.Parameters.AddWithValue("$restored_utc", (object?)DbConvert.Utc(record.RestoredUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$deleted_utc", (object?)DbConvert.Utc(record.DeletedUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$size_bytes", record.SizeBytes);
        command.Parameters.AddWithValue("$file_count", record.FileCount);
        command.Parameters.AddWithValue("$contains_possible_save_data", record.ContainsPossibleSaveData ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public QuarantineEntryRecord? GetById(string quarantineId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM quarantine_entries WHERE quarantine_id=$quarantine_id;";
        command.Parameters.AddWithValue("$quarantine_id", quarantineId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<QuarantineEntryRecord> ListByGame(string gameId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM quarantine_entries WHERE game_id=$game_id ORDER BY created_utc DESC;";
        command.Parameters.AddWithValue("$game_id", gameId);
        using var reader = command.ExecuteReader();
        var records = new List<QuarantineEntryRecord>();
        while (reader.Read()) records.Add(Read(reader));
        return records;
    }

    public IReadOnlyList<QuarantineEntryRecord> ListActive()
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM quarantine_entries
            WHERE restored_utc IS NULL
              AND deleted_utc IS NULL
            ORDER BY created_utc DESC;
            """;
        using var reader = command.ExecuteReader();
        var records = new List<QuarantineEntryRecord>();
        while (reader.Read()) records.Add(Read(reader));
        return records;
    }

    public QuarantineEntryRecord? GetLatestRestorable(string gameId, string originalCacheRoot)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM quarantine_entries
            WHERE game_id=$game_id
              AND original_cache_root=$original_cache_root
              AND restored_utc IS NULL
              AND deleted_utc IS NULL
            ORDER BY created_utc DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$game_id", gameId);
        command.Parameters.AddWithValue("$original_cache_root", originalCacheRoot);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public void MarkRestored(string quarantineId, DateTimeOffset restoredUtc) => Mark(quarantineId, "restored_utc", restoredUtc);
    public void MarkDeleted(string quarantineId, DateTimeOffset deletedUtc) => Mark(quarantineId, "deleted_utc", deletedUtc);

    public void Delete(string quarantineId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM quarantine_entries WHERE quarantine_id=$quarantine_id;";
        command.Parameters.AddWithValue("$quarantine_id", quarantineId);
        command.ExecuteNonQuery();
    }

    private void Mark(string quarantineId, string column, DateTimeOffset value)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE quarantine_entries SET {column}=$value WHERE quarantine_id=$quarantine_id;";
        command.Parameters.AddWithValue("$quarantine_id", quarantineId);
        command.Parameters.AddWithValue("$value", DbConvert.Utc(value));
        command.ExecuteNonQuery();
    }

    private static QuarantineEntryRecord Read(SqliteDataReader reader) => new()
    {
        QuarantineId = reader.String("quarantine_id"),
        GameId = reader.String("game_id"),
        OriginalCacheRoot = reader.String("original_cache_root"),
        QuarantinePath = reader.String("quarantine_path"),
        Reason = reader.String("reason"),
        CreatedUtc = DbConvert.Date(reader.String("created_utc")),
        RestoredUtc = DbConvert.DateOrNull(reader.NullableString("restored_utc")),
        DeletedUtc = DbConvert.DateOrNull(reader.NullableString("deleted_utc")),
        SizeBytes = reader.Int64("size_bytes"),
        FileCount = reader.Int64("file_count"),
        ContainsPossibleSaveData = reader.Int64("contains_possible_save_data") == 1
    };
}

internal static class ReaderExtensions
{
    public static string String(this SqliteDataReader reader, string name) => reader.GetString(reader.GetOrdinal(name));
    public static string? NullableString(this SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
    public static long Int64(this SqliteDataReader reader, string name) => reader.GetInt64(reader.GetOrdinal(name));
    public static long? NullableInt64(this SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }
}

