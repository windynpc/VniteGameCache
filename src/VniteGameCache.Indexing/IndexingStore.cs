using Microsoft.Data.Sqlite;
using VniteGameCache.Persistence;

namespace VniteGameCache.Indexing;

public sealed class IndexingStore(SqliteConnectionFactory factory)
{
    public GameRecord? GetGame(string gameId) => new GameRepository(factory).GetById(gameId);

    public void CreateTask(TaskRecord task) => new TaskRepository(factory).Create(task);

    public void MarkTaskFailed(string taskId, TaskState state, IndexingErrorCode code, string message)
    {
        new TaskRepository(factory).UpdateState(taskId, state, code.ToString(), message);
    }

    public void CommitFullScan(GameRecord previousGame, ScanResult scan, string taskId, DateTimeOffset committedUtc)
    {
        var nextRevision = previousGame.SourceRevision + 1;
        using var connection = factory.OpenConnection();
        using var transaction = connection.BeginTransaction();

        try
        {
            Execute(connection, transaction, "DELETE FROM file_entries WHERE game_id = $game_id;", ("$game_id", previousGame.GameId));
            Execute(connection, transaction, "DELETE FROM directory_fingerprints WHERE game_id = $game_id;", ("$game_id", previousGame.GameId));

            foreach (var file in scan.Files)
            {
                Execute(connection, transaction, """
                    INSERT INTO file_entries(game_id, relative_path, size_bytes, modified_utc, file_role, quick_hash, full_hash,
                    source_revision, cache_revision, created_utc, updated_utc)
                    VALUES($game_id, $relative_path, $size_bytes, $modified_utc, $file_role, NULL, NULL,
                    $source_revision, $cache_revision, $created_utc, $updated_utc);
                    """,
                    ("$game_id", previousGame.GameId),
                    ("$relative_path", file.RelativePath),
                    ("$size_bytes", file.SizeBytes),
                    ("$modified_utc", Utc(file.ModifiedUtc)),
                    ("$file_role", file.FileRole.ToString()),
                    ("$source_revision", nextRevision),
                    ("$cache_revision", previousGame.CacheRevision),
                    ("$created_utc", Utc(committedUtc)),
                    ("$updated_utc", Utc(committedUtc)));
            }

            foreach (var directory in scan.Directories)
            {
                Execute(connection, transaction, """
                    INSERT INTO directory_fingerprints(game_id, relative_path, direct_file_count, direct_directory_count,
                    aggregate_size_bytes, latest_known_modified_utc, source_revision, created_utc, updated_utc)
                    VALUES($game_id, $relative_path, $direct_file_count, $direct_directory_count, $aggregate_size_bytes,
                    $latest_known_modified_utc, $source_revision, $created_utc, $updated_utc);
                    """,
                    ("$game_id", previousGame.GameId),
                    ("$relative_path", directory.RelativePath),
                    ("$direct_file_count", directory.DirectFileCount),
                    ("$direct_directory_count", directory.DirectDirectoryCount),
                    ("$aggregate_size_bytes", directory.AggregateSizeBytes),
                    ("$latest_known_modified_utc", (object?)Utc(directory.LatestKnownModifiedUtc) ?? DBNull.Value),
                    ("$source_revision", nextRevision),
                    ("$created_utc", Utc(committedUtc)),
                    ("$updated_utc", Utc(committedUtc)));
            }

            Execute(connection, transaction, """
                UPDATE games
                SET source_revision = $source_revision,
                    last_full_scan_utc = $last_full_scan_utc,
                    cache_size_bytes = $cache_size_bytes,
                    updated_utc = $updated_utc
                WHERE game_id = $game_id;
                """,
                ("$game_id", previousGame.GameId),
                ("$source_revision", nextRevision),
                ("$last_full_scan_utc", Utc(committedUtc)),
                ("$cache_size_bytes", scan.TotalSizeBytes),
                ("$updated_utc", Utc(committedUtc)));

            Execute(connection, transaction, """
                UPDATE task_records
                SET state = 'Completed',
                    progress_current = $progress_current,
                    progress_total = $progress_total,
                    completed_utc = $completed_utc,
                    updated_utc = $updated_utc,
                    last_error_code = NULL,
                    last_error_message = NULL
                WHERE task_id = $task_id;
                """,
                ("$task_id", taskId),
                ("$progress_current", scan.Files.Count),
                ("$progress_total", scan.Files.Count),
                ("$completed_utc", Utc(committedUtc)),
                ("$updated_utc", Utc(committedUtc)));

            transaction.Commit();
        }
        catch (Exception error) when (error is SqliteException or PersistenceException)
        {
            throw new IndexingException(IndexingErrorCode.DatabaseWriteFailed, error.Message, error);
        }
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }
        command.ExecuteNonQuery();
    }

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static string? Utc(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O");
}
