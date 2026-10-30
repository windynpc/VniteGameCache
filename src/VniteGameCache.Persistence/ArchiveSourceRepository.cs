using Microsoft.Data.Sqlite;

namespace VniteGameCache.Persistence;

public sealed class ArchiveSourceRepository(SqliteConnectionFactory factory)
{
    public IReadOnlyList<ArchiveSourceRecord> List()
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM archive_sources;";
        using var reader = command.ExecuteReader();
        var records = new List<ArchiveSourceRecord>();
        while (reader.Read()) records.Add(Read(reader));
        return records;
    }

    public ArchiveSourceRecord? GetByGameId(string gameId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM archive_sources WHERE game_id = $game_id;";
        command.Parameters.AddWithValue("$game_id", gameId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public void Upsert(ArchiveSourceRecord source)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO archive_sources(game_id, archive_path, executable_relative_path, expanded_bytes,
                file_count, archive_size_bytes, archive_last_write_utc)
            VALUES($game_id, $archive_path, $executable_relative_path, $expanded_bytes,
                $file_count, $archive_size_bytes, $archive_last_write_utc)
            ON CONFLICT(game_id) DO UPDATE SET
                archive_path=excluded.archive_path,
                executable_relative_path=excluded.executable_relative_path,
                expanded_bytes=excluded.expanded_bytes,
                file_count=excluded.file_count,
                archive_size_bytes=excluded.archive_size_bytes,
                archive_last_write_utc=excluded.archive_last_write_utc;
            """;
        command.Parameters.AddWithValue("$game_id", source.GameId);
        command.Parameters.AddWithValue("$archive_path", source.ArchivePath);
        command.Parameters.AddWithValue("$executable_relative_path", source.ExecutableRelativePath);
        command.Parameters.AddWithValue("$expanded_bytes", source.ExpandedBytes);
        command.Parameters.AddWithValue("$file_count", source.FileCount);
        command.Parameters.AddWithValue("$archive_size_bytes", source.ArchiveSizeBytes);
        command.Parameters.AddWithValue("$archive_last_write_utc", source.ArchiveLastWriteUtc.ToUniversalTime().ToString("O"));
        command.ExecuteNonQuery();
    }

    private static ArchiveSourceRecord Read(SqliteDataReader reader) => new()
    {
        GameId = reader.GetString(reader.GetOrdinal("game_id")),
        ArchivePath = reader.GetString(reader.GetOrdinal("archive_path")),
        ExecutableRelativePath = reader.GetString(reader.GetOrdinal("executable_relative_path")),
        ExpandedBytes = reader.GetInt64(reader.GetOrdinal("expanded_bytes")),
        FileCount = reader.GetInt32(reader.GetOrdinal("file_count")),
        ArchiveSizeBytes = reader.GetInt64(reader.GetOrdinal("archive_size_bytes")),
        ArchiveLastWriteUtc = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("archive_last_write_utc")))
    };
}
