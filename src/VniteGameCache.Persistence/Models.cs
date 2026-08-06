namespace VniteGameCache.Persistence;

public static class Schema
{
    public const int CurrentVersion = 4;
}

public enum LaunchPolicy { FastLaunch, StrictLaunch, OfflineLaunch }
public enum CachePolicy { AlwaysCached, CacheBeforeLaunch, RunDirectlyFromNas, SaveSyncOnly, NeverCache }
public enum VerificationPolicy { Fast, Standard, Full }
public enum CacheStatus { NotCached, Checking, Copying, Verifying, Ready, Incomplete, Stale, Running, PendingSaveSync, Conflict, Offline, Failed }
public enum TaskState { Pending, Checking, Copying, Verifying, Ready, Launching, Running, SyncingSaves, WaitingForNAS, Cleaning, Completed, Interrupted, Conflict, Failed, Cancelled }
public enum FileRole { Executable, ImmutableGameData, MutableGameData, Save, Config, Screenshot, Mod, GeneratedCache, Temporary, Log, Excluded }
public enum SyncMode { NasToLocal, LocalToNas, Bidirectional, NewerWins, Manual }
public enum PathKind { GameRelative, Absolute, AppData, LocalAppData, Documents, SavedGames }
public enum ConflictPolicy { Manual, KeepLocal, KeepNas, NewerWins }
public enum ResolutionState { Unresolved, Resolved, Ignored }
public enum SaveProfileSourceType { GameRelative, AppData, LocalAppData, Documents, SavedGames, Absolute }
public enum SaveBackupType { Manual, BeforeImport, BeforeRestore, BeforeClear }
public enum ManagementIntent { Managed, Suspended, Unmanaged, Ignored }
public enum GameLaunchModeOverride { Inherit, Direct, TranslationPreferred, MTool, CustomScript }
public enum OperationStatus { Planned, Running, Completed, Failed, RolledBack, Interrupted }
public enum LaunchCandidateType { Executable, Batch, CommandScript, Shortcut, Unknown }
public enum LaunchCandidateRole { MainGame, TranslationGame, Launcher, MToolTemplate, MToolTarget, LocaleToolScript, CustomLaunchScript, ConfigurationTool, Updater, Uninstaller, CrashReporter, Helper, Runtime, EngineComponent, Unknown }
public enum LaunchCandidateSource { DiskScan, ExistingGameRecord, ManualSelection, VniteOriginalRoute, MToolAdapter, LaunchHistory, ImportedShortcut }
public enum LaunchCandidateTestStatus { Untested, ReadOnlyValid, ReadOnlyInvalid, TestSucceeded, TestFailed, NeedsRevalidation, HardBlocked }

public sealed record GameLaunchCandidateRecord
{
    public required string CandidateId { get; init; }
    public required string GameId { get; init; }
    public required string RelativePath { get; init; }
    public LaunchCandidateType CandidateType { get; init; } = LaunchCandidateType.Unknown;
    public LaunchCandidateRole CandidateRole { get; init; } = LaunchCandidateRole.Unknown;
    public LaunchCandidateSource CandidateSource { get; init; } = LaunchCandidateSource.DiskScan;
    public int Score { get; init; }
    public int Confidence { get; init; }
    public bool IsPrimary { get; init; }
    public int? FallbackPriority { get; init; }
    public bool IsIgnored { get; init; }
    public bool UserConfirmed { get; init; }
    public long FileSize { get; init; }
    public DateTimeOffset? LastWriteUtc { get; init; }
    public string MetadataFingerprint { get; init; } = "";
    public LaunchCandidateTestStatus LastTestStatus { get; init; } = LaunchCandidateTestStatus.Untested;
    public DateTimeOffset? LastTestUtc { get; init; }
    public string? LastLaunchSessionId { get; init; }
    public string MetadataJson { get; init; } = "{}";
    public string ReasonsJson { get; init; } = "[]";
    public string ExclusionReasonsJson { get; init; } = "[]";
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record GameRecord
{
    public required string GameId { get; init; }
    public string? VniteGameId { get; init; }
    public required string DisplayName { get; init; }
    public required string SourceRoot { get; init; }
    public required string CacheRoot { get; init; }
    public required string ExecutablePath { get; init; }
    public string ArgumentsJson { get; init; } = "[]";
    public required string WorkingDirectory { get; init; }
    public LaunchPolicy LaunchPolicy { get; init; } = LaunchPolicy.FastLaunch;
    public CachePolicy CachePolicy { get; init; } = CachePolicy.CacheBeforeLaunch;
    public VerificationPolicy VerificationPolicy { get; init; } = VerificationPolicy.Fast;
    public long SourceRevision { get; init; }
    public long CacheRevision { get; init; }
    public DateTimeOffset? LastFullScanUtc { get; init; }
    public DateTimeOffset? LastIncrementalScanUtc { get; init; }
    public DateTimeOffset? LastPlayedUtc { get; init; }
    public long CacheSizeBytes { get; init; }
    public bool IsPinned { get; init; }
    public bool ContainsPossibleSaveData { get; init; }
    public bool PendingManualSaveBackup { get; init; }
    public DateTimeOffset? LastCacheRefreshUtc { get; init; }
    public ManagementIntent ManagementIntent { get; init; } = ManagementIntent.Managed;
    public GameLaunchModeOverride LaunchModeOverride { get; init; } = GameLaunchModeOverride.Inherit;
    public string? ExecutableRelativePath { get; init; }
    public string? WorkingDirectoryRelativePath { get; init; }
    public CacheStatus CacheStatus { get; init; } = CacheStatus.NotCached;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record TombstoneRecord
{
    public required string TombstoneId { get; init; }
    public string? GameId { get; init; }
    public string? VniteGameId { get; init; }
    public required string SourceIdentity { get; init; }
    public required string Reason { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record OperationRecord
{
    public required string OperationId { get; init; }
    public required string OperationType { get; init; }
    public string? GameId { get; init; }
    public OperationStatus Status { get; init; } = OperationStatus.Planned;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public required string RequestedChangesJson { get; init; }
    public string StepsJson { get; init; } = "[]";
    public string ChangedItemsJson { get; init; } = "[]";
    public string WarningsJson { get; init; } = "[]";
    public string? Error { get; init; }
    public string RollbackStatus { get; init; } = "";
}

public sealed record LaunchSessionRecord
{
    public required string LaunchSessionId { get; init; }
    public required string TraceId { get; init; }
    public required string GameId { get; init; }
    public string? VniteGameId { get; init; }
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string EffectiveLaunchMode { get; init; } = "";
    public string ShortcutPath { get; init; } = "";
    public string WrapperPath { get; init; } = "";
    public string AgentResultJson { get; init; } = "{}";
    public string SourceRoot { get; init; } = "";
    public string CacheRoot { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public int? ProcessId { get; init; }
    public string ObservedProcessPath { get; init; } = "";
    public int? ExitCode { get; init; }
    public string FailureStage { get; init; } = "";
    public string FailureMessage { get; init; } = "";
    public DateTimeOffset? CompletedUtc { get; init; }
}

public sealed record SaveProfileRecord
{
    public required string SaveProfileId { get; init; }
    public required string GameId { get; init; }
    public required string DisplayName { get; init; }
    public SaveProfileSourceType SourceType { get; init; } = SaveProfileSourceType.GameRelative;
    public required string PathTemplate { get; init; }
    public required string ResolvedPath { get; init; }
    public string IncludePatternsJson { get; init; } = "[\"**/*\"]";
    public string ExcludePatternsJson { get; init; } = "[]";
    public required string BackupRoot { get; init; }
    public DateTimeOffset? LastBackupUtc { get; init; }
    public DateTimeOffset? LastRestoreUtc { get; init; }
    public bool IsPrimary { get; init; }
    public bool IsEnabled { get; init; } = true;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record SaveBackupRecord
{
    public required string BackupId { get; init; }
    public required string SaveProfileId { get; init; }
    public required string GameId { get; init; }
    public required string BackupPath { get; init; }
    public SaveBackupType BackupType { get; init; } = SaveBackupType.Manual;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string SourcePath { get; init; }
    public long FileCount { get; init; }
    public long TotalBytes { get; init; }
    public string? Note { get; init; }
    public bool IsPinned { get; init; }
}

public sealed record QuarantineEntryRecord
{
    public required string QuarantineId { get; init; }
    public required string GameId { get; init; }
    public required string OriginalCacheRoot { get; init; }
    public required string QuarantinePath { get; init; }
    public required string Reason { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RestoredUtc { get; init; }
    public DateTimeOffset? DeletedUtc { get; init; }
    public long SizeBytes { get; init; }
    public long FileCount { get; init; }
    public bool ContainsPossibleSaveData { get; init; }
}

public sealed record FileEntryRecord
{
    public required string GameId { get; init; }
    public required string RelativePath { get; init; }
    public long SizeBytes { get; init; }
    public required DateTimeOffset ModifiedUtc { get; init; }
    public FileRole FileRole { get; init; } = FileRole.ImmutableGameData;
    public string? QuickHash { get; init; }
    public string? FullHash { get; init; }
    public long SourceRevision { get; init; }
    public long CacheRevision { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record DirectoryFingerprintRecord
{
    public required string GameId { get; init; }
    public required string RelativePath { get; init; }
    public long DirectFileCount { get; init; }
    public long DirectDirectoryCount { get; init; }
    public long AggregateSizeBytes { get; init; }
    public DateTimeOffset? LatestKnownModifiedUtc { get; init; }
    public long SourceRevision { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record MutablePathRecord
{
    public required string MutablePathId { get; init; }
    public required string GameId { get; init; }
    public required string PathTemplate { get; init; }
    public PathKind PathKind { get; init; } = PathKind.GameRelative;
    public SyncMode SyncMode { get; init; } = SyncMode.Bidirectional;
    public bool SnapshotEnabled { get; init; } = true;
    public ConflictPolicy ConflictPolicy { get; init; } = ConflictPolicy.Manual;
    public string ExcludePatternsJson { get; init; } = "[]";
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record TaskRecord
{
    public required string TaskId { get; init; }
    public string? GameId { get; init; }
    public required string TaskType { get; init; }
    public TaskState State { get; init; } = TaskState.Pending;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public long ProgressCurrent { get; init; }
    public long? ProgressTotal { get; init; }
    public int RetryCount { get; init; }
    public string? LastErrorCode { get; init; }
    public string? LastErrorMessage { get; init; }
    public string? RecoveryPayloadJson { get; init; }
}

public sealed record ConflictRecord
{
    public required string ConflictId { get; init; }
    public required string GameId { get; init; }
    public required string MutablePathId { get; init; }
    public required string RelativePath { get; init; }
    public required string LocalVersionJson { get; init; }
    public required string NasVersionJson { get; init; }
    public DateTimeOffset DetectedUtc { get; init; } = DateTimeOffset.UtcNow;
    public ResolutionState ResolutionState { get; init; } = ResolutionState.Unresolved;
    public DateTimeOffset? ResolvedUtc { get; init; }
    public string? ResolutionPayloadJson { get; init; }
}

public sealed record MigrationHistoryRecord(int SchemaVersion, string MigrationId, string MigrationName, DateTimeOffset AppliedUtc, string? Checksum);
