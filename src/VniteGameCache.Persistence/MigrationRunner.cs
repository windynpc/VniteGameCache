using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace VniteGameCache.Persistence;

public interface IMigrationRunner
{
    void Initialize();
    IReadOnlyList<MigrationHistoryRecord> GetHistory();
}

public sealed class MigrationRunner(SqliteConnectionFactory factory) : IMigrationRunner
{
    private static readonly Migration[] Migrations =
    [
        new(1, "001_initial_schema", "Initial SQLite persistence schema", InitialSchemaSql),
        new(2, "002_phase6_productization", "Phase 6 cache management and manual save profiles", Phase6ProductizationSql),
        new(3, "003_recovery_reconciliation", "Recovery reconciliation schema", RecoveryReconciliationSql),
        new(4, "004_executable_manager", "Executable manager launch candidates", ExecutableManagerSql),
        new(5, "005_archive_sources", "Archive-backed game source metadata", ArchiveSourcesSql)
    ];

    public void Initialize()
    {
        using var connection = factory.OpenConnection();
        try
        {
            EnsureHistoryTable(connection);
            var applied = GetAppliedVersions(connection);
            var maxApplied = applied.Count == 0 ? 0 : applied.Max();
            if (maxApplied > Schema.CurrentVersion)
            {
                throw new MigrationException("future_schema_version", $"Database schema version {maxApplied} is newer than supported version {Schema.CurrentVersion}.");
            }

            foreach (var migration in Migrations.OrderBy(item => item.Version))
            {
                if (applied.Contains(migration.Version))
                {
                    continue;
                }

                using var transaction = connection.BeginTransaction();
                try
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = migration.Sql;
                    command.ExecuteNonQuery();

                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO migration_history(schema_version, migration_id, migration_name, applied_utc, checksum)
                        VALUES ($schema_version, $migration_id, $migration_name, $applied_utc, $checksum);
                        """;
                    insert.Parameters.AddWithValue("$schema_version", migration.Version);
                    insert.Parameters.AddWithValue("$migration_id", migration.Id);
                    insert.Parameters.AddWithValue("$migration_name", migration.Name);
                    insert.Parameters.AddWithValue("$applied_utc", DbConvert.Utc(DateTimeOffset.UtcNow));
                    insert.Parameters.AddWithValue("$checksum", Sha256(migration.Sql));
                    insert.ExecuteNonQuery();
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }
        catch (MigrationException)
        {
            throw;
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 26)
        {
            throw new MigrationException("database_corrupt", "SQLite database is corrupt. It was not deleted or recreated.", error);
        }
        catch (SqliteException error) when (error.SqliteErrorCode is 8 or 10)
        {
            throw new MigrationException("database_not_writable", "SQLite database is not writable. Migration stopped.", error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        {
            throw new MigrationException("migration_failed", "Migration failed. Existing database was preserved.", error);
        }
    }

    public IReadOnlyList<MigrationHistoryRecord> GetHistory()
    {
        using var connection = factory.OpenConnection();
        EnsureHistoryTable(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT schema_version, migration_id, migration_name, applied_utc, checksum FROM migration_history ORDER BY schema_version;";
        using var reader = command.ExecuteReader();
        var records = new List<MigrationHistoryRecord>();
        while (reader.Read())
        {
            records.Add(new MigrationHistoryRecord(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                DbConvert.Date(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return records;
    }

    private static void EnsureHistoryTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS migration_history (
              schema_version INTEGER PRIMARY KEY,
              migration_id TEXT NOT NULL UNIQUE,
              migration_name TEXT NOT NULL,
              applied_utc TEXT NOT NULL,
              checksum TEXT
            );
            """;
        command.ExecuteNonQuery();
    }

    private static HashSet<int> GetAppliedVersions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT schema_version FROM migration_history;";
        using var reader = command.ExecuteReader();
        var versions = new HashSet<int>();
        while (reader.Read())
        {
            versions.Add(reader.GetInt32(0));
        }
        return versions;
    }

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed record Migration(int Version, string Id, string Name, string Sql);

    private const string InitialSchemaSql = """
        CREATE TABLE games (
          game_id TEXT PRIMARY KEY,
          vnite_game_id TEXT UNIQUE,
          display_name TEXT NOT NULL,
          source_root TEXT NOT NULL,
          cache_root TEXT NOT NULL,
          executable_path TEXT NOT NULL,
          arguments_json TEXT NOT NULL DEFAULT '[]',
          working_directory TEXT NOT NULL,
          launch_policy TEXT NOT NULL CHECK (launch_policy IN ('FastLaunch','StrictLaunch','OfflineLaunch')),
          cache_policy TEXT NOT NULL CHECK (cache_policy IN ('AlwaysCached','CacheBeforeLaunch','RunDirectlyFromNas','SaveSyncOnly','NeverCache')),
          verification_policy TEXT NOT NULL CHECK (verification_policy IN ('Fast','Standard','Full')),
          source_revision INTEGER NOT NULL DEFAULT 0 CHECK (source_revision >= 0),
          cache_revision INTEGER NOT NULL DEFAULT 0 CHECK (cache_revision >= 0),
          last_full_scan_utc TEXT,
          last_incremental_scan_utc TEXT,
          last_played_utc TEXT,
          cache_size_bytes INTEGER NOT NULL DEFAULT 0 CHECK (cache_size_bytes >= 0),
          is_pinned INTEGER NOT NULL DEFAULT 0 CHECK (is_pinned IN (0,1)),
          cache_status TEXT NOT NULL CHECK (cache_status IN ('NotCached','Checking','Copying','Verifying','Ready','Incomplete','Stale','Running','PendingSaveSync','Conflict','Offline','Failed')),
          created_utc TEXT NOT NULL,
          updated_utc TEXT NOT NULL
        );

        CREATE TABLE file_entries (
          game_id TEXT NOT NULL,
          relative_path TEXT NOT NULL CHECK (
            relative_path <> '' AND
            substr(relative_path, 1, 1) <> '/' AND
            substr(relative_path, 1, 1) <> '\' AND
            relative_path NOT GLOB '[A-Za-z]:*'
          ),
          size_bytes INTEGER NOT NULL CHECK (size_bytes >= 0),
          modified_utc TEXT NOT NULL,
          file_role TEXT NOT NULL CHECK (file_role IN ('Executable','ImmutableGameData','MutableGameData','Save','Config','Screenshot','Mod','GeneratedCache','Temporary','Log','Excluded')),
          quick_hash TEXT,
          full_hash TEXT,
          source_revision INTEGER NOT NULL DEFAULT 0 CHECK (source_revision >= 0),
          cache_revision INTEGER NOT NULL DEFAULT 0 CHECK (cache_revision >= 0),
          created_utc TEXT NOT NULL,
          updated_utc TEXT NOT NULL,
          PRIMARY KEY (game_id, relative_path),
          FOREIGN KEY (game_id) REFERENCES games(game_id) ON DELETE CASCADE
        );

        CREATE TABLE directory_fingerprints (
          game_id TEXT NOT NULL,
          relative_path TEXT NOT NULL,
          direct_file_count INTEGER NOT NULL CHECK (direct_file_count >= 0),
          direct_directory_count INTEGER NOT NULL CHECK (direct_directory_count >= 0),
          aggregate_size_bytes INTEGER NOT NULL CHECK (aggregate_size_bytes >= 0),
          latest_known_modified_utc TEXT,
          source_revision INTEGER NOT NULL DEFAULT 0 CHECK (source_revision >= 0),
          created_utc TEXT NOT NULL,
          updated_utc TEXT NOT NULL,
          PRIMARY KEY (game_id, relative_path),
          FOREIGN KEY (game_id) REFERENCES games(game_id) ON DELETE CASCADE
        );

        CREATE TABLE mutable_paths (
          mutable_path_id TEXT PRIMARY KEY,
          game_id TEXT NOT NULL,
          path_template TEXT NOT NULL,
          path_kind TEXT NOT NULL CHECK (path_kind IN ('GameRelative','Absolute','AppData','LocalAppData','Documents','SavedGames')),
          sync_mode TEXT NOT NULL CHECK (sync_mode IN ('NasToLocal','LocalToNas','Bidirectional','NewerWins','Manual')),
          snapshot_enabled INTEGER NOT NULL CHECK (snapshot_enabled IN (0,1)),
          conflict_policy TEXT NOT NULL CHECK (conflict_policy IN ('Manual','KeepLocal','KeepNas','NewerWins')),
          exclude_patterns_json TEXT NOT NULL DEFAULT '[]',
          created_utc TEXT NOT NULL,
          updated_utc TEXT NOT NULL,
          FOREIGN KEY (game_id) REFERENCES games(game_id) ON DELETE CASCADE
        );

        CREATE TABLE task_records (
          task_id TEXT PRIMARY KEY,
          game_id TEXT,
          task_type TEXT NOT NULL,
          state TEXT NOT NULL CHECK (state IN ('Pending','Checking','Copying','Verifying','Ready','Launching','Running','SyncingSaves','WaitingForNAS','Cleaning','Completed','Interrupted','Conflict','Failed','Cancelled')),
          created_utc TEXT NOT NULL,
          updated_utc TEXT NOT NULL,
          started_utc TEXT,
          completed_utc TEXT,
          progress_current INTEGER NOT NULL DEFAULT 0 CHECK (progress_current >= 0),
          progress_total INTEGER CHECK (progress_total IS NULL OR progress_total >= 0),
          retry_count INTEGER NOT NULL DEFAULT 0 CHECK (retry_count >= 0),
          last_error_code TEXT,
          last_error_message TEXT,
          recovery_payload_json TEXT,
          CHECK (progress_total IS NULL OR progress_current <= progress_total),
          FOREIGN KEY (game_id) REFERENCES games(game_id) ON DELETE SET NULL
        );

        CREATE TABLE conflict_records (
          conflict_id TEXT PRIMARY KEY,
          game_id TEXT NOT NULL,
          mutable_path_id TEXT NOT NULL,
          relative_path TEXT NOT NULL,
          local_version_json TEXT NOT NULL,
          nas_version_json TEXT NOT NULL,
          detected_utc TEXT NOT NULL,
          resolution_state TEXT NOT NULL CHECK (resolution_state IN ('Unresolved','Resolved','Ignored')),
          resolved_utc TEXT,
          resolution_payload_json TEXT,
          FOREIGN KEY (game_id) REFERENCES games(game_id) ON DELETE CASCADE,
          FOREIGN KEY (mutable_path_id) REFERENCES mutable_paths(mutable_path_id) ON DELETE CASCADE
        );

        CREATE INDEX idx_file_entries_game ON file_entries(game_id);
        CREATE INDEX idx_tasks_state ON task_records(state, updated_utc);
        CREATE INDEX idx_tasks_game ON task_records(game_id);
        CREATE INDEX idx_conflicts_game_state ON conflict_records(game_id, resolution_state);
        """;

    private const string Phase6ProductizationSql = """
        ALTER TABLE games ADD COLUMN contains_possible_save_data INTEGER NOT NULL DEFAULT 0 CHECK (contains_possible_save_data IN (0,1));
        ALTER TABLE games ADD COLUMN pending_manual_save_backup INTEGER NOT NULL DEFAULT 0 CHECK (pending_manual_save_backup IN (0,1));
        ALTER TABLE games ADD COLUMN last_cache_refresh_utc TEXT;

        CREATE TABLE save_profiles (
          save_profile_id TEXT PRIMARY KEY,
          game_id TEXT NOT NULL,
          display_name TEXT NOT NULL,
          source_type TEXT NOT NULL CHECK (source_type IN ('GameRelative','AppData','LocalAppData','Documents','SavedGames','Absolute')),
          path_template TEXT NOT NULL,
          resolved_path TEXT NOT NULL,
          include_patterns_json TEXT NOT NULL DEFAULT '["**/*"]',
          exclude_patterns_json TEXT NOT NULL DEFAULT '[]',
          backup_root TEXT NOT NULL,
          last_backup_utc TEXT,
          last_restore_utc TEXT,
          is_primary INTEGER NOT NULL DEFAULT 0 CHECK (is_primary IN (0,1)),
          is_enabled INTEGER NOT NULL DEFAULT 1 CHECK (is_enabled IN (0,1)),
          created_utc TEXT NOT NULL,
          updated_utc TEXT NOT NULL,
          FOREIGN KEY (game_id) REFERENCES games(game_id) ON DELETE CASCADE
        );

        CREATE TABLE save_backups (
          backup_id TEXT PRIMARY KEY,
          save_profile_id TEXT NOT NULL,
          game_id TEXT NOT NULL,
          backup_path TEXT NOT NULL,
          backup_type TEXT NOT NULL CHECK (backup_type IN ('Manual','BeforeImport','BeforeRestore','BeforeClear')),
          created_utc TEXT NOT NULL,
          source_path TEXT NOT NULL,
          file_count INTEGER NOT NULL DEFAULT 0 CHECK (file_count >= 0),
          total_bytes INTEGER NOT NULL DEFAULT 0 CHECK (total_bytes >= 0),
          note TEXT,
          is_pinned INTEGER NOT NULL DEFAULT 0 CHECK (is_pinned IN (0,1)),
          FOREIGN KEY (save_profile_id) REFERENCES save_profiles(save_profile_id) ON DELETE CASCADE,
          FOREIGN KEY (game_id) REFERENCES games(game_id) ON DELETE CASCADE
        );

        CREATE TABLE quarantine_entries (
          quarantine_id TEXT PRIMARY KEY,
          game_id TEXT NOT NULL,
          original_cache_root TEXT NOT NULL,
          quarantine_path TEXT NOT NULL,
          reason TEXT NOT NULL,
          created_utc TEXT NOT NULL,
          restored_utc TEXT,
          deleted_utc TEXT,
          size_bytes INTEGER NOT NULL DEFAULT 0 CHECK (size_bytes >= 0),
          file_count INTEGER NOT NULL DEFAULT 0 CHECK (file_count >= 0),
          contains_possible_save_data INTEGER NOT NULL DEFAULT 0 CHECK (contains_possible_save_data IN (0,1)),
          FOREIGN KEY (game_id) REFERENCES games(game_id) ON DELETE CASCADE
        );

        CREATE INDEX idx_save_profiles_game ON save_profiles(game_id, is_enabled);
        CREATE INDEX idx_save_backups_profile ON save_backups(save_profile_id, created_utc);
        CREATE INDEX idx_quarantine_game ON quarantine_entries(game_id, created_utc);
        """;

    private const string RecoveryReconciliationSql = """
        ALTER TABLE games ADD COLUMN management_intent TEXT NOT NULL DEFAULT 'Managed' CHECK (management_intent IN ('Managed','Suspended','Unmanaged','Ignored'));
        ALTER TABLE games ADD COLUMN launch_mode_override TEXT NOT NULL DEFAULT 'Inherit' CHECK (launch_mode_override IN ('Inherit','Direct','TranslationPreferred','MTool','CustomScript'));
        ALTER TABLE games ADD COLUMN executable_relative_path TEXT;
        ALTER TABLE games ADD COLUMN working_directory_relative_path TEXT;

        CREATE TABLE tombstones (
          tombstone_id TEXT PRIMARY KEY,
          game_id TEXT,
          vnite_game_id TEXT,
          source_identity TEXT NOT NULL,
          reason TEXT NOT NULL,
          created_utc TEXT NOT NULL
        );

        CREATE TABLE operation_records (
          operation_id TEXT PRIMARY KEY,
          operation_type TEXT NOT NULL,
          game_id TEXT,
          status TEXT NOT NULL CHECK (status IN ('Planned','Running','Completed','Failed','RolledBack','Interrupted')),
          created_utc TEXT NOT NULL,
          updated_utc TEXT NOT NULL,
          started_utc TEXT,
          completed_utc TEXT,
          requested_changes_json TEXT NOT NULL,
          steps_json TEXT NOT NULL DEFAULT '[]',
          changed_items_json TEXT NOT NULL DEFAULT '[]',
          warnings_json TEXT NOT NULL DEFAULT '[]',
          error TEXT,
          rollback_status TEXT NOT NULL DEFAULT '',
          FOREIGN KEY (game_id) REFERENCES games(game_id) ON DELETE SET NULL
        );

        CREATE TABLE launch_sessions (
          launch_session_id TEXT PRIMARY KEY,
          trace_id TEXT NOT NULL,
          game_id TEXT NOT NULL,
          vnite_game_id TEXT,
          started_utc TEXT NOT NULL,
          effective_launch_mode TEXT NOT NULL DEFAULT '',
          shortcut_path TEXT NOT NULL DEFAULT '',
          wrapper_path TEXT NOT NULL DEFAULT '',
          agent_result_json TEXT NOT NULL DEFAULT '{}',
          source_root TEXT NOT NULL DEFAULT '',
          cache_root TEXT NOT NULL DEFAULT '',
          executable_path TEXT NOT NULL DEFAULT '',
          working_directory TEXT NOT NULL DEFAULT '',
          process_id INTEGER,
          observed_process_path TEXT NOT NULL DEFAULT '',
          exit_code INTEGER,
          failure_stage TEXT NOT NULL DEFAULT '',
          failure_message TEXT NOT NULL DEFAULT '',
          completed_utc TEXT,
          FOREIGN KEY (game_id) REFERENCES games(game_id) ON DELETE CASCADE
        );

        CREATE INDEX idx_games_management_intent ON games(management_intent);
        CREATE INDEX idx_tombstones_vnite ON tombstones(vnite_game_id);
        CREATE INDEX idx_tombstones_source ON tombstones(source_identity);
        CREATE INDEX idx_operations_status ON operation_records(status, updated_utc);
        CREATE INDEX idx_operations_game ON operation_records(game_id, created_utc);
        CREATE INDEX idx_launch_sessions_game ON launch_sessions(game_id, started_utc);
        """;

    private const string ExecutableManagerSql = """
        CREATE TABLE game_launch_candidates (
          candidate_id TEXT PRIMARY KEY,
          game_id TEXT NOT NULL,
          relative_path TEXT NOT NULL CHECK (
            relative_path <> '' AND
            substr(relative_path, 1, 1) <> '/' AND
            substr(relative_path, 1, 1) <> '\' AND
            relative_path NOT GLOB '[A-Za-z]:*'
          ),
          candidate_type TEXT NOT NULL CHECK (candidate_type IN ('Executable','Batch','CommandScript','Shortcut','Unknown')),
          candidate_role TEXT NOT NULL CHECK (candidate_role IN ('MainGame','TranslationGame','Launcher','MToolTemplate','MToolTarget','LocaleToolScript','CustomLaunchScript','ConfigurationTool','Updater','Uninstaller','CrashReporter','Helper','Runtime','EngineComponent','Unknown')),
          candidate_source TEXT NOT NULL CHECK (candidate_source IN ('DiskScan','ExistingGameRecord','ManualSelection','VniteOriginalRoute','MToolAdapter','LaunchHistory','ImportedShortcut')),
          score INTEGER NOT NULL DEFAULT 0,
          confidence INTEGER NOT NULL DEFAULT 0,
          is_primary INTEGER NOT NULL DEFAULT 0 CHECK (is_primary IN (0,1)),
          fallback_priority INTEGER,
          is_ignored INTEGER NOT NULL DEFAULT 0 CHECK (is_ignored IN (0,1)),
          user_confirmed INTEGER NOT NULL DEFAULT 0 CHECK (user_confirmed IN (0,1)),
          file_size INTEGER NOT NULL DEFAULT 0 CHECK (file_size >= 0),
          last_write_utc TEXT,
          metadata_fingerprint TEXT NOT NULL DEFAULT '',
          last_test_status TEXT NOT NULL DEFAULT 'Untested' CHECK (last_test_status IN ('Untested','ReadOnlyValid','ReadOnlyInvalid','TestSucceeded','TestFailed','NeedsRevalidation','HardBlocked')),
          last_test_utc TEXT,
          last_launch_session_id TEXT,
          metadata_json TEXT NOT NULL DEFAULT '{}',
          reasons_json TEXT NOT NULL DEFAULT '[]',
          exclusion_reasons_json TEXT NOT NULL DEFAULT '[]',
          created_utc TEXT NOT NULL,
          updated_utc TEXT NOT NULL,
          UNIQUE(game_id, relative_path, candidate_type),
          FOREIGN KEY (game_id) REFERENCES games(game_id) ON DELETE CASCADE,
          FOREIGN KEY (last_launch_session_id) REFERENCES launch_sessions(launch_session_id) ON DELETE SET NULL
        );

        CREATE UNIQUE INDEX idx_launch_candidates_primary ON game_launch_candidates(game_id) WHERE is_primary = 1;
        CREATE UNIQUE INDEX idx_launch_candidates_fallback_priority ON game_launch_candidates(game_id, fallback_priority) WHERE fallback_priority IS NOT NULL;
        CREATE INDEX idx_launch_candidates_game_score ON game_launch_candidates(game_id, is_ignored, score DESC, confidence DESC);
        """;

    private const string ArchiveSourcesSql = """
        CREATE TABLE archive_sources (
          game_id TEXT PRIMARY KEY,
          archive_path TEXT NOT NULL,
          executable_relative_path TEXT NOT NULL,
          expanded_bytes INTEGER NOT NULL CHECK(expanded_bytes >= 0),
          file_count INTEGER NOT NULL CHECK(file_count > 0),
          archive_size_bytes INTEGER NOT NULL CHECK(archive_size_bytes >= 0),
          archive_last_write_utc TEXT NOT NULL,
          FOREIGN KEY (game_id) REFERENCES games(game_id) ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX idx_archive_sources_path ON archive_sources(archive_path);
        """;
}
