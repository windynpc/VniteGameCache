# Data Model

Phase 4 data model is SQLite schema v1 plus the generated Game Manifest v1.

Phase 5 reuses SQLite schema v1. No database migration is added.

## SQLite

- Library: `Microsoft.Data.Sqlite`
- Schema version: `1`
- Default DB path: `%LOCALAPPDATA%\VniteGameCache\data\vnite-game-cache.db`
- Test/CLI override: `VGC_DB_PATH` or `--db-path`
- PRAGMA: `foreign_keys=ON`, `journal_mode=WAL`, `synchronous=NORMAL`, `busy_timeout=5000`

## Tables

### migration_history

- `schema_version`
- `migration_id`
- `migration_name`
- `applied_utc`
- `checksum`

### games

- `game_id`
- `vnite_game_id`
- `display_name`
- `source_root`
- `cache_root`
- `executable_path`
- `arguments_json`
- `working_directory`
- `launch_policy`
- `cache_policy`
- `verification_policy`
- `source_revision`
- `cache_revision`
- `last_full_scan_utc`
- `last_incremental_scan_utc`
- `last_played_utc`
- `cache_size_bytes`
- `is_pinned`
- `cache_status`
- `created_utc`
- `updated_utc`

Phase 4 updates only `source_revision`, `last_full_scan_utc`, `cache_size_bytes`, and `updated_utc` during successful initial indexing.

Phase 5 updates only:

- `cache_revision`
- `cache_status`
- `updated_utc`

`cache_revision` is set to `source_revision` only after Robocopy success and Fast verification success.

### file_entries

Primary key: `(game_id, relative_path)`

- `game_id`
- `relative_path`
- `size_bytes`
- `modified_utc`
- `file_role`
- `quick_hash`
- `full_hash`
- `source_revision`
- `cache_revision`
- `created_utc`
- `updated_utc`

Rules:

- `relative_path` uses `/`.
- `relative_path` must be game-root-relative.
- Absolute paths, drive-rooted paths, UNC paths, `.`, `..`, and path escapes are rejected before database write.
- Phase 4 does not compute `quick_hash` or `full_hash`.

### directory_fingerprints

Primary key: `(game_id, relative_path)`

- `game_id`
- `relative_path`
- `direct_file_count`
- `direct_directory_count`
- `aggregate_size_bytes`
- `latest_known_modified_utc`
- `source_revision`
- `created_utc`
- `updated_utc`

Rules:

- Root directory fingerprint uses empty `relative_path`.
- `aggregate_size_bytes` is recursive total size of non-excluded indexed files below that directory.
- SMB directory timestamps remain a hint, not a source of truth.

### mutable_paths

Formal user-approved mutable path configuration.

Phase 4 does not write suggested mutable paths into this table. Suggestions are emitted only in Manifest.

### task_records

Phase 4 uses `TaskType = IndexGame`.

Phase 5 uses:

- `TaskType = PrepareCache`
- existing `PrepareLaunch` task remains for Wrapper launch preparation

States:

- success: `Completed`
- cancellation: `Cancelled`
- failure: `Failed`

### conflict_records

Unchanged in Phase 4. Real save conflict detection is not implemented yet.

## Enum Storage

Database enums are stored as stable strings.

Examples:

- `Executable`
- `ImmutableGameData`
- `Save`
- `Config`
- `Completed`
- `Cancelled`
- `Failed`

## Game Manifest v1

Default path:

```text
%LOCALAPPDATA%\VniteGameCache\manifests\<game_id>.json
```

CLI override:

```text
--manifest-root <path>
VGC_MANIFEST_ROOT
```

Contract:

- UTF-8 without BOM
- stable property names using `snake_case`
- stable path sorting
- enum values serialized as strings
- temporary file followed by atomic replace
- failure or cancellation does not overwrite the previous manifest
- no credentials or file contents are written

Top-level fields:

- `schema_version`
- `game_id`
- `source_root`
- `cache_root`
- `generated_at_utc`
- `scan_started_utc`
- `scan_completed_utc`
- `source_revision`
- `cache_revision`
- `status`
- `warning_count`
- `file_count`
- `directory_count`
- `total_size_bytes`
- `executable_candidates`
- `mutable_path_candidates`
- `engine_hints`
- `exclusion_summary`
- `excluded_paths`
- `warnings`
- `files`
- `directory_fingerprints`

Manifest statuses:

- `Complete`
- `CompleteWithWarnings`
- `Failed`
- `Cancelled`

Only `Complete` and `CompleteWithWarnings` are written as successful manifest files in Phase 4.

## Revision Rule

Successful initial indexing:

1. scan source tree outside a database transaction
2. build FileEntry, DirectoryFingerprint, executable candidates, mutable path candidates, and Manifest payload
3. open a short SQLite transaction
4. replace `file_entries` for the game
5. replace `directory_fingerprints` for the game
6. increment `games.source_revision`
7. update `games.last_full_scan_utc`
8. update `task_records`
9. commit
10. write Manifest atomically

Failure or cancellation:

- keeps old `file_entries`
- keeps old `directory_fingerprints`
- keeps old `source_revision`
- keeps old Manifest
- marks task as `Failed` or `Cancelled`

## Phase 5 Copy Plan

Copy Plan is not stored as a separate table in schema v1. It is generated from:

- `games`
- `file_entries`
- current cache root file existence
- request flags

Fields:

- `game_id`
- `source_root`
- `cache_root`
- `source_revision`
- `current_cache_revision`
- `required_copy`
- `reason`
- `estimated_file_count`
- `estimated_total_bytes`
- `executable_relative_path`
- `verification_policy`

## Phase 5 Cache State Rules

Skip Robocopy:

```text
cache_status == Ready
AND source_revision == cache_revision
AND cache executable exists
AND force_refresh == false
```

Successful copy:

- `cache_status = Ready`
- `cache_revision = source_revision`

Cancelled copy:

- `cache_status = Incomplete`
- `cache_revision` unchanged

Failed copy or failed verification:

- `cache_status = Failed`
- `cache_revision` unchanged

## PrepareLaunchResponse Extension

Phase 5 extends `PrepareLaunchResponse` with optional fields:

- `launch_executable_path`
- `launch_working_directory`

Compatibility:

- Missing fields mean Wrapper uses request paths.
- Present fields mean Wrapper validates and launches cache paths.

## Shared Settings Snapshot

Non-SQLite configuration shared across the plugin, Agent, Wrapper and Control Panel is stored in:

```text
%LOCALAPPDATA%\VniteGameCache\config\settings.json
```

Schema version: `1`.

Fields:

- `database_path`
- `default_cache_root`
- `default_save_backup_root`
- `minimum_free_space_bytes`
- `cache_max_total_bytes`
- `quarantine_retention_days`
- `log_root`
- `commands_root`
- `updated_utc`

Migration rule:

- Existing `PluginDB` values remain the source for the Vnite UI.
- The plugin writes the snapshot on activation and before invoking Agent.
- Agent, Wrapper and Control Panel read the snapshot and fall back to environment variables or legacy defaults when the file is missing.

## Runtime State Command

Agent CLI:

```text
VniteGameCache.Agent runtime-state --game-id <id> --state Running|Ready [--process-id <pid>] [--db-path <path>]
```

No schema migration is required. The command updates existing `games.cache_status`, `games.last_played_utc` and `games.updated_utc`.

## Phase 6 Schema v2

Schema current version is `2`.

### games additions

- `contains_possible_save_data INTEGER NOT NULL DEFAULT 0`
- `pending_manual_save_backup INTEGER NOT NULL DEFAULT 0`
- `last_cache_refresh_utc TEXT`

Phase 5 fields used by LRU:

- `last_played_utc`
- `cache_size_bytes`
- `is_pinned`
- `cache_status`

### save_profiles

Manual user-approved save path configuration.

Fields:

- `save_profile_id`
- `game_id`
- `display_name`
- `source_type`
- `path_template`
- `resolved_path`
- `include_patterns_json`
- `exclude_patterns_json`
- `backup_root`
- `last_backup_utc`
- `last_restore_utc`
- `is_primary`
- `is_enabled`
- `created_utc`
- `updated_utc`

Allowed `source_type` values:

- `GameRelative`
- `AppData`
- `LocalAppData`
- `Documents`
- `SavedGames`
- `Absolute`

### save_backups

ZIP backup registry for manual backup, pre-import backup, pre-restore backup, and pre-clear backup.

### quarantine_entries

Registry for cache directories or protected user-data fragments moved out of active cache.

- Manual safe clear stores the full cache directory for recovery.
- Automatic LRU stores only detected user data, then permanently deletes re-creatable game body files.
- `quarantine_retention_days = 0` means no automatic permanent deletion.
- Expired entries with no active task may be permanently deleted and removed from the table.

### cache cleanup candidates

No table is created. Candidates are dynamic results based on current `games` rows and live cache root state.

Cleanup result projections include:

- `MovedToQuarantineBytes`
- `ProtectedUserDataBytes`
- `PermanentlyDeletedBytes`
- `ActualFreeSpaceIncreaseBytes`
- `EstimatedReclaimableBytes`

## Phase 8.0 Schema v4

Schema current version is `4`.

### game_launch_candidates

Per-game launch candidate registry for the Executable Manager.

Fields:

- `candidate_id`
- `game_id`
- `relative_path`
- `candidate_type`
- `candidate_role`
- `candidate_source`
- `score`
- `confidence`
- `is_primary`
- `fallback_priority`
- `is_ignored`
- `user_confirmed`
- `file_size`
- `last_write_utc`
- `metadata_fingerprint`
- `last_test_status`
- `last_test_utc`
- `last_launch_session_id`
- `metadata_json`
- `reasons_json`
- `exclusion_reasons_json`
- `created_utc`
- `updated_utc`

Candidate identity is stable by `game_id + normalized relative_path + candidate_type`. The table has one primary candidate per game and unique fallback priorities per game.

Allowed `candidate_type` values:

- `Executable`
- `Batch`
- `CommandScript`
- `Shortcut`
- `Unknown`

Core `candidate_role` values:

- `MainGame`
- `TranslationGame`
- `Launcher`
- `MToolTemplate`
- `MToolTarget`
- `LocaleToolScript`
- `CustomLaunchScript`
- `ConfigurationTool`
- `Updater`
- `Uninstaller`
- `CrashReporter`
- `Helper`
- `Runtime`
- `EngineComponent`
- `Unknown`

The effective launch profile remains in `games`; this table does not replace Wrapper resolution.
