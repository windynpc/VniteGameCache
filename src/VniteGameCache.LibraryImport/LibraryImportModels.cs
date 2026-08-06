using VniteGameCache.Indexing;
using VniteGameCache.Persistence;

namespace VniteGameCache.LibraryImport;

public enum LaunchMode { Direct, TranslationPreferred, MTool, CustomLauncher }
public enum ImportStatus { New, AutoImportable, NeedsConfirmation, Existing, Ignored, Imported, Failed }
public enum MToolCompatibilityStatus { NotConfigured, ExecutableMissing, ExecutableValidated, LaunchProtocolUnknown, LaunchProtocolValidated, LaunchSucceeded, Unsupported }

public sealed record LibraryImportConfig(
    IReadOnlyList<string> LibraryRoots,
    string DefaultCacheRoot,
    LaunchMode DefaultLaunchMode = LaunchMode.Direct,
    string? MToolExecutablePath = null,
    int ScanDepth = 3,
    bool AutoImportEnabled = true,
    bool AutoUpdateExistingEntries = true,
    IReadOnlyList<string>? IgnoredDirectoryPatterns = null);

public sealed record LibraryCandidate(
    string CandidateId,
    string SourceRoot,
    string FolderName,
    IReadOnlyList<ExecutableCandidate> DetectedExecutables,
    ExecutableCandidate? PreferredExecutable,
    double Confidence,
    IReadOnlyList<EngineHint> EngineHints,
    string? ExistingVniteGameId,
    string? ExistingInternalGameId,
    ImportStatus ImportStatus,
    IReadOnlyList<string> Warnings);

public sealed record LibraryDryRunResult(
    IReadOnlyList<LibraryCandidate> Candidates,
    int ToCreate,
    int ToUpdate,
    int NeedsConfirmation,
    int Ignored);

public sealed record LibraryImportResult(
    IReadOnlyList<ImportedGame> ImportedGames,
    IReadOnlyList<LibraryCandidate> SkippedCandidates);

public sealed record ImportedGame(
    string CandidateId,
    string GameId,
    string SourceRoot,
    string CacheRoot,
    string ExecutablePath,
    LaunchMode LaunchMode);

public sealed record MToolValidationResult(
    MToolCompatibilityStatus Status,
    string? ExecutablePath,
    string? InvocationMode,
    int? ExitCode,
    string Message);
