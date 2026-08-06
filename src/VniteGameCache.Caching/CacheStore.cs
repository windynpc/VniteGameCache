using Microsoft.Data.Sqlite;
using VniteGameCache.Persistence;

namespace VniteGameCache.Caching;

public sealed class CacheStore(SqliteConnectionFactory factory)
{
    public GameRecord? GetGame(string gameId) => new GameRepository(factory).GetById(gameId);
    public IReadOnlyList<FileEntryRecord> GetFiles(string gameId) => new FileEntryRepository(factory).ListByGame(gameId);

    public void CreateTask(TaskRecord task) => new TaskRepository(factory).Create(task);

    public void MarkTask(string taskId, TaskState state, string? code = null, string? message = null) =>
        new TaskRepository(factory).UpdateState(taskId, state, code, message);

    public void UpdateTaskProgress(string taskId, long current, long? total) =>
        new TaskRepository(factory).UpdateProgress(taskId, current, total);

    public void MarkCopying(string taskId)
    {
        using var connection = factory.OpenConnection();
        Execute(connection, """
            UPDATE task_records SET state='Copying', updated_utc=$updated_utc WHERE task_id=$task_id;
            """, ("$task_id", taskId), ("$updated_utc", Utc(DateTimeOffset.UtcNow)));
    }

    public void MarkCacheIncomplete(string gameId)
    {
        using var connection = factory.OpenConnection();
        Execute(connection, """
            UPDATE games SET cache_status='Incomplete', updated_utc=$updated_utc WHERE game_id=$game_id;
            """, ("$game_id", gameId), ("$updated_utc", Utc(DateTimeOffset.UtcNow)));
    }

    public void MarkCacheFailed(string gameId)
    {
        using var connection = factory.OpenConnection();
        Execute(connection, """
            UPDATE games SET cache_status='Failed', updated_utc=$updated_utc WHERE game_id=$game_id;
            """, ("$game_id", gameId), ("$updated_utc", Utc(DateTimeOffset.UtcNow)));
    }

    public void MarkCacheReady(string gameId, long sourceRevision, string taskId, long cacheSizeBytes)
    {
        using var connection = factory.OpenConnection();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            UPDATE games
            SET cache_status='Ready', cache_revision=$cache_revision, cache_size_bytes=$cache_size_bytes,
                last_cache_refresh_utc=$updated_utc, updated_utc=$updated_utc
            WHERE game_id=$game_id;
            """, ("$game_id", gameId), ("$cache_revision", sourceRevision), ("$cache_size_bytes", cacheSizeBytes), ("$updated_utc", Utc(DateTimeOffset.UtcNow)));
        Execute(connection, transaction, """
            UPDATE task_records
            SET state='Completed', completed_utc=$completed_utc, updated_utc=$updated_utc, last_error_code=NULL, last_error_message=NULL
            WHERE task_id=$task_id;
            """, ("$task_id", taskId), ("$completed_utc", Utc(DateTimeOffset.UtcNow)), ("$updated_utc", Utc(DateTimeOffset.UtcNow)));
        transaction.Commit();
    }

    public bool TryRestoreLatestQuarantine(GameRecord game, CopyPlan plan)
    {
        if (!string.Equals(game.CacheRoot, plan.CacheRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Directory.Exists(plan.CacheRoot) && Directory.EnumerateFileSystemEntries(plan.CacheRoot).Any())
        {
            return false;
        }

        var repository = new QuarantineRepository(factory);
        var entry = repository.GetLatestRestorable(game.GameId, plan.CacheRoot);
        if (entry is null || !Directory.Exists(entry.QuarantinePath))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(plan.CacheRoot) ?? ".");
        if (Directory.Exists(plan.CacheRoot))
        {
            Directory.Delete(plan.CacheRoot);
        }

        Directory.Move(entry.QuarantinePath, plan.CacheRoot);
        repository.MarkRestored(entry.QuarantineId, DateTimeOffset.UtcNow);
        return true;
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameters(command, parameters);
        command.ExecuteNonQuery();
    }

    private static void AddParameters(SqliteCommand command, params (string Name, object? Value)[] parameters)
    {
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }
    }

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
}
