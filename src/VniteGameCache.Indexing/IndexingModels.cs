using VniteGameCache.Persistence;

namespace VniteGameCache.Indexing;

public enum ManifestStatus { Complete, CompleteWithWarnings, Failed, Cancelled }
public enum IndexingErrorCode { GameNotFound, SourceRootNotFound, SourceRootMismatch, AccessDenied, EnumerationFailed, ReparsePointSkipped, DatabaseWriteFailed, ManifestWriteFailed, Cancelled, UnexpectedError }

public sealed record IndexingOptions(
    string ManifestRoot,
    IReadOnlyList<string>? HardExcludePatterns = null,
    IReadOnlyList<string>? SuggestedExcludePatterns = null,
    IReadOnlySet<string>? IncludeSuggestedExcludePatterns = null,
    bool FollowReparsePoints = false)
{
    public static IndexingOptions Default()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VniteGameCache", "manifests");
        return new IndexingOptions(root);
    }
}

public sealed record IndexingProgress(
    string Phase,
    string? CurrentDirectory,
    long FileCount,
    long DirectoryCount,
    long ProcessedBytes,
    int WarningCount);

public sealed record IndexingResult(
    string GameId,
    ManifestStatus Status,
    long SourceRevision,
    int FileCount,
    int DirectoryCount,
    long TotalSizeBytes,
    string? ManifestPath,
    IReadOnlyList<IndexingWarning> Warnings,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record IndexingWarning(
    IndexingErrorCode Code,
    string? RelativePath,
    string Message);

public sealed record ScannedFile(
    string RelativePath,
    long SizeBytes,
    DateTimeOffset ModifiedUtc,
    FileRole FileRole);

public sealed record ScannedDirectory(
    string RelativePath,
    long DirectFileCount,
    long DirectDirectoryCount,
    long AggregateSizeBytes,
    DateTimeOffset? LatestKnownModifiedUtc);

public sealed record ExecutableCandidate(
    string RelativePath,
    int Score,
    IReadOnlyList<string> Reasons,
    bool IsLauncherCandidate,
    bool IsProbableGameExecutable,
    long FileSize,
    DateTimeOffset ModifiedUtc);

public sealed record MutablePathCandidate(
    string PathTemplate,
    PathKind PathKind,
    SyncMode SuggestedSyncMode,
    double Confidence,
    IReadOnlyList<string> Reasons);

public sealed record ExclusionHit(
    string RelativePath,
    string Pattern,
    string Kind);

public sealed record EngineHint(
    string Engine,
    IReadOnlyList<string> Reasons);

public sealed record ScanResult(
    IReadOnlyList<ScannedFile> Files,
    IReadOnlyList<ScannedDirectory> Directories,
    IReadOnlyList<ExecutableCandidate> ExecutableCandidates,
    IReadOnlyList<MutablePathCandidate> MutablePathCandidates,
    IReadOnlyList<ExclusionHit> Exclusions,
    IReadOnlyList<EngineHint> EngineHints,
    IReadOnlyList<IndexingWarning> Warnings,
    DateTimeOffset ScanStartedUtc,
    DateTimeOffset ScanCompletedUtc)
{
    public ManifestStatus Status => Warnings.Count == 0 ? ManifestStatus.Complete : ManifestStatus.CompleteWithWarnings;
    public long TotalSizeBytes => Files.Where(file => file.FileRole != FileRole.Excluded).Sum(file => file.SizeBytes);
}
