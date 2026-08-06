using Microsoft.Data.Sqlite;

namespace VniteGameCache.Persistence;

public sealed record DatabaseOptions(string DatabasePath, int BusyTimeoutMilliseconds = 5000)
{
    public static DatabaseOptions Default()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VniteGameCache",
            "data");
        return new DatabaseOptions(Path.Combine(root, "vnite-game-cache.db"));
    }
}

public sealed class SqliteConnectionFactory(DatabaseOptions options)
{
    public string DatabasePath => options.DatabasePath;

    public SqliteConnection OpenConnection()
    {
        try
        {
            var directory = Path.GetDirectoryName(options.DatabasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = options.DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            };
            var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            Configure(connection, options.BusyTimeoutMilliseconds);
            return connection;
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 26)
        {
            throw new PersistenceException("database_corrupt", "SQLite database is corrupt. It was not deleted or recreated.", error);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or SqliteException)
        {
            throw new PersistenceException("database_open_failed", $"Failed to open SQLite database: {options.DatabasePath}", error);
        }
    }

    public static void Configure(SqliteConnection connection, int busyTimeoutMilliseconds = 5000)
    {
        ExecuteScalar(connection, "PRAGMA foreign_keys = ON;");
        ExecuteScalar(connection, "PRAGMA journal_mode = WAL;");
        ExecuteScalar(connection, "PRAGMA synchronous = NORMAL;");
        ExecuteScalar(connection, $"PRAGMA busy_timeout = {busyTimeoutMilliseconds};");
    }

    private static object? ExecuteScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}

internal static class DbConvert
{
    public static string Enum<T>(T value) where T : struct, Enum => value.ToString();
    public static T Enum<T>(string value) where T : struct, Enum
    {
        if (!System.Enum.TryParse<T>(value, ignoreCase: false, out var parsed) || !System.Enum.IsDefined(parsed))
        {
            throw new PersistenceException("invalid_enum", $"Invalid {typeof(T).Name}: {value}");
        }
        return parsed;
    }

    public static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    public static string? Utc(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O");
    public static DateTimeOffset Date(string value) => DateTimeOffset.Parse(value).ToUniversalTime();
    public static DateTimeOffset? DateOrNull(string? value) => value is null ? null : DateTimeOffset.Parse(value).ToUniversalTime();
}


