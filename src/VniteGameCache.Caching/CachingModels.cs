using VniteGameCache.Persistence;

namespace VniteGameCache.Caching;

public enum CacheResultStatus
{
    Success,
    CacheAlreadyReady,
    GameNotFound,
    IndexMissing,
    SourceUnavailable,
    InvalidCachePath,
    InsufficientSpace,
    RobocopyFailed,
    VerificationFailed,
    Cancelled,
    UnexpectedError
}

public sealed record CacheRequest(
    Guid RequestId,
    string GameId,
    bool ForceRefresh = false,
    VerificationPolicy VerificationPolicy = VerificationPolicy.Fast,
    bool LaunchAfterCache = false);

public sealed record CopyPlan(
    string GameId,
    string SourceRoot,
    string CacheRoot,
    long SourceRevision,
    long CurrentCacheRevision,
    bool RequiredCopy,
    string Reason,
    int EstimatedFileCount,
    long EstimatedTotalBytes,
    string ExecutableRelativePath,
    VerificationPolicy VerificationPolicy);

public sealed record CacheResult(
    CacheResultStatus Status,
    string GameId,
    bool RobocopyCalled,
    string? ErrorCode,
    string? ErrorMessage,
    CopyPlan? Plan,
    int? RobocopyExitCode,
    string? LaunchExecutablePath,
    string? LaunchWorkingDirectory);

public sealed record CacheProgress(
    string Phase,
    int CopiedFiles,
    int TotalFiles,
    long CopiedBytes,
    long TotalBytes,
    string? CurrentFile,
    TimeSpan Elapsed,
    int? RobocopyExitCode,
    double BytesPerSecond = 0,
    TimeSpan? EstimatedRemaining = null,
    bool IsEstimate = true,
    DateTimeOffset? UpdatedUtc = null);

public sealed record RobocopyProgress(
    long BytesTransferred,
    int FilesObserved,
    string? CurrentFile,
    double BytesPerSecond,
    TimeSpan Elapsed);

public sealed record RobocopyCommand(
    string SourceRoot,
    string CacheRoot,
    IReadOnlyList<string> Arguments,
    string LogPath);

public sealed record RobocopyResult(
    int ExitCode,
    bool Cancelled,
    IReadOnlyList<string> OutputLines)
{
    public bool IsSuccess => !Cancelled && ExitCode is >= 0 and <= 7;
}

public sealed class CacheException(CacheResultStatus status, string code, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public CacheResultStatus Status { get; } = status;
    public string Code { get; } = code;
}
