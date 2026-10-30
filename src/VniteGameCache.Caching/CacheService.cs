using VniteGameCache.Persistence;
using System.Text.Json;

namespace VniteGameCache.Caching;

public sealed class CacheService(
    CacheStore store,
    CopyPlanner planner,
    RobocopyCommandBuilder commandBuilder,
    IRobocopyRunner robocopyRunner,
    CacheVerifier verifier,
    long minimumFreeSpaceBytes)
{
    public async Task<CacheResult> PrepareCacheAsync(CacheRequest request, CancellationToken cancellationToken = default)
    {
        var taskId = $"prepare-cache-{request.RequestId:N}";
        GameRecord? game = null;
        CopyPlan? plan = null;

        try
        {
            game = store.GetGame(request.GameId);
            if (game is null)
            {
                return Failure(CacheResultStatus.GameNotFound, request.GameId, "game_not_found", $"Game not found: {request.GameId}", null, false, null);
            }

            var files = store.GetFiles(game.GameId);
            store.CreateTask(new TaskRecord
            {
                TaskId = taskId,
                GameId = game.GameId,
                TaskType = "PrepareCache",
                State = TaskState.Checking,
                StartedUtc = DateTimeOffset.UtcNow,
                RecoveryPayloadJson = $"{{\"game_id\":\"{EscapeJson(game.GameId)}\",\"force_refresh\":{request.ForceRefresh.ToString().ToLowerInvariant()}}}"
            });

            plan = planner.Build(game, files, request.ForceRefresh, request.VerificationPolicy);
            var launchExecutable = Path.Combine(plan.CacheRoot, plan.ExecutableRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var launchWorkingDirectory = MapWorkingDirectory(game.SourceRoot, game.CacheRoot, game.WorkingDirectory);

            if (!plan.RequiredCopy)
            {
                verifier.VerifyFast(plan, files);
                store.MarkTask(taskId, TaskState.Completed);
                return new CacheResult(CacheResultStatus.CacheAlreadyReady, game.GameId, false, null, null, plan, null, launchExecutable, launchWorkingDirectory);
            }

            var restoredFromQuarantine = store.TryRestoreLatestQuarantine(game, plan);
            if (restoredFromQuarantine)
            {
                store.MarkTask(taskId, TaskState.Verifying);
                try
                {
                    verifier.VerifyFast(plan, files);
                    verifier.WriteReadyMarker(plan);
                    store.MarkCacheReady(game.GameId, game.SourceRevision, taskId, DirectorySize(plan.CacheRoot));
                    return new CacheResult(CacheResultStatus.Success, game.GameId, false, null, null, plan, null, launchExecutable, launchWorkingDirectory);
                }
                catch (CacheException error) when (error.Status == CacheResultStatus.VerificationFailed)
                {
                    store.MarkCacheIncomplete(game.GameId);
                }
            }

            CachePathSafety.EnsureSpace(plan.CacheRoot, plan.EstimatedTotalBytes, minimumFreeSpaceBytes);
            Directory.CreateDirectory(plan.CacheRoot);

            cancellationToken.ThrowIfCancellationRequested();
            store.MarkCopying(taskId);
            var command = commandBuilder.Build(plan);
            var existingProgress = CalculateExistingProgress(plan, files);
            store.UpdateTaskProgress(taskId, existingProgress.Bytes, plan.EstimatedTotalBytes);
            var latestProgress = new CacheProgress(
                "Copying",
                existingProgress.Files,
                plan.EstimatedFileCount,
                existingProgress.Bytes,
                plan.EstimatedTotalBytes,
                null,
                TimeSpan.Zero,
                null,
                UpdatedUtc: DateTimeOffset.UtcNow);
            WriteProgressSnapshot(command, latestProgress);
            var robocopy = await robocopyRunner.RunAsync(command, cancellationToken, progress =>
            {
                var copiedBytes = Math.Min(plan.EstimatedTotalBytes, existingProgress.Bytes + Math.Max(0, progress.BytesTransferred));
                var copiedFiles = Math.Min(plan.EstimatedFileCount, existingProgress.Files + Math.Max(0, progress.FilesObserved));
                TimeSpan? remaining = progress.BytesPerSecond > 0 && plan.EstimatedTotalBytes > copiedBytes
                    ? TimeSpan.FromSeconds((plan.EstimatedTotalBytes - copiedBytes) / progress.BytesPerSecond)
                    : null;
                store.UpdateTaskProgress(taskId, copiedBytes, plan.EstimatedTotalBytes);
                latestProgress = new CacheProgress(
                    "Copying",
                    copiedFiles,
                    plan.EstimatedFileCount,
                    copiedBytes,
                    plan.EstimatedTotalBytes,
                    progress.CurrentFile,
                    progress.Elapsed,
                    null,
                    progress.BytesPerSecond,
                    remaining,
                    IsEstimate: true,
                    UpdatedUtc: DateTimeOffset.UtcNow);
                WriteProgressSnapshot(command, latestProgress);
            });
            if (robocopy.Cancelled)
            {
                WriteProgressSnapshot(command, latestProgress with { Phase = "Cancelled", RobocopyExitCode = robocopy.ExitCode, UpdatedUtc = DateTimeOffset.UtcNow });
                store.MarkCacheIncomplete(game.GameId);
                store.MarkTask(taskId, TaskState.Cancelled, "cancelled", "Cache copy was cancelled.");
                return Failure(CacheResultStatus.Cancelled, game.GameId, "cancelled", "Cache copy was cancelled.", plan, true, robocopy.ExitCode);
            }

            if (!robocopy.IsSuccess)
            {
                try
                {
                    store.MarkTask(taskId, TaskState.Verifying);
                    verifier.VerifyFast(plan, files);
                    verifier.WriteReadyMarker(plan);
                    store.MarkCacheReady(game.GameId, game.SourceRevision, taskId, DirectorySize(plan.CacheRoot));
                    WriteProgressSnapshot(command, latestProgress with
                    {
                        Phase = "Completed",
                        CopiedFiles = plan.EstimatedFileCount,
                        CopiedBytes = plan.EstimatedTotalBytes,
                        RobocopyExitCode = robocopy.ExitCode,
                        IsEstimate = false,
                        UpdatedUtc = DateTimeOffset.UtcNow
                    });
                    return new CacheResult(CacheResultStatus.Success, game.GameId, true, null, null, plan, robocopy.ExitCode, launchExecutable, launchWorkingDirectory);
                }
                catch (CacheException)
                {
                    // Keep the original robocopy failure when the cache is not actually launch-ready.
                }

                var failureMessage = $"Robocopy failed with exit code {robocopy.ExitCode}. Log: {command.LogPath}";
                if (!string.IsNullOrWhiteSpace(latestProgress.CurrentFile))
                {
                    failureMessage += $" Last observed file: {latestProgress.CurrentFile}";
                }

                store.MarkCacheFailed(game.GameId);
                store.MarkTask(taskId, TaskState.Failed, "robocopy_failed", failureMessage);
                WriteProgressSnapshot(command, latestProgress with { Phase = "Failed", RobocopyExitCode = robocopy.ExitCode, UpdatedUtc = DateTimeOffset.UtcNow });
                return Failure(CacheResultStatus.RobocopyFailed, game.GameId, "robocopy_failed", failureMessage, plan, true, robocopy.ExitCode);
            }

            store.MarkTask(taskId, TaskState.Verifying);
            WriteProgressSnapshot(command, new CacheProgress(
                "Verifying",
                plan.EstimatedFileCount,
                plan.EstimatedFileCount,
                plan.EstimatedTotalBytes,
                plan.EstimatedTotalBytes,
                null,
                TimeSpan.Zero,
                robocopy.ExitCode,
                UpdatedUtc: DateTimeOffset.UtcNow));
            verifier.VerifyFast(plan, files);
            verifier.WriteReadyMarker(plan);
            store.MarkCacheReady(game.GameId, game.SourceRevision, taskId, DirectorySize(plan.CacheRoot));
            WriteProgressSnapshot(command, new CacheProgress(
                "Completed",
                plan.EstimatedFileCount,
                plan.EstimatedFileCount,
                plan.EstimatedTotalBytes,
                plan.EstimatedTotalBytes,
                null,
                TimeSpan.Zero,
                robocopy.ExitCode,
                IsEstimate: false,
                UpdatedUtc: DateTimeOffset.UtcNow));
            return new CacheResult(CacheResultStatus.Success, game.GameId, true, null, null, plan, robocopy.ExitCode, launchExecutable, launchWorkingDirectory);
        }
        catch (OperationCanceledException)
        {
            if (game is not null)
            {
                store.MarkCacheIncomplete(game.GameId);
                store.MarkTask(taskId, TaskState.Cancelled, "cancelled", "Cache copy was cancelled.");
            }
            return Failure(CacheResultStatus.Cancelled, request.GameId, "cancelled", "Cache copy was cancelled.", plan, plan?.RequiredCopy == true, null);
        }
        catch (CacheException error)
        {
            if (game is not null)
            {
                if (error.Status == CacheResultStatus.VerificationFailed) store.MarkCacheFailed(game.GameId);
                store.MarkTask(taskId, TaskState.Failed, error.Code, error.Message);
            }
            return Failure(error.Status, game?.GameId ?? request.GameId, error.Code, error.Message, plan, false, null);
        }
        catch (Exception error)
        {
            if (game is not null) store.MarkTask(taskId, TaskState.Failed, "unexpected_error", error.Message);
            return Failure(CacheResultStatus.UnexpectedError, game?.GameId ?? request.GameId, "unexpected_error", error.Message, plan, false, null);
        }
    }

    private static CacheResult Failure(CacheResultStatus status, string gameId, string code, string message, CopyPlan? plan, bool robocopyCalled, int? exitCode) =>
        new(status, gameId, robocopyCalled, code, message, plan, exitCode, null, null);

    private static string EscapeJson(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static long DirectorySize(string root)
    {
        if (!Directory.Exists(root)) return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            total += new FileInfo(file).Length;
        }
        return total;
    }

    private static (int Files, long Bytes) CalculateExistingProgress(CopyPlan plan, IReadOnlyList<FileEntryRecord> files)
    {
        if (!Directory.Exists(plan.CacheRoot) || !Directory.EnumerateFileSystemEntries(plan.CacheRoot).Any()) return (0, 0);
        var fileCount = 0;
        var totalBytes = 0L;
        foreach (var file in files.Where(file => file.FileRole != FileRole.Excluded))
        {
            var target = Path.Combine(plan.CacheRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(target)) continue;
            var info = new FileInfo(target);
            if (info.Length != file.SizeBytes) continue;
            fileCount++;
            totalBytes += info.Length;
        }
        return (fileCount, Math.Min(totalBytes, plan.EstimatedTotalBytes));
    }

    private static void WriteProgressSnapshot(RobocopyCommand command, CacheProgress progress)
    {
        try
        {
            var path = command.LogPath + ".progress.json";
            var tempPath = $"{path}.{Environment.ProcessId}.tmp";
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
            File.WriteAllText(tempPath, JsonSerializer.Serialize(progress, options));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string MapWorkingDirectory(string sourceRoot, string cacheRoot, string workingDirectory)
    {
        var normalizedSource = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedWorking = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedSource, normalizedWorking, StringComparison.OrdinalIgnoreCase))
        {
            return cacheRoot;
        }

        var sourceWithSeparator = normalizedSource + Path.DirectorySeparatorChar;
        if (normalizedWorking.StartsWith(sourceWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            var relative = normalizedWorking[sourceWithSeparator.Length..];
            return Path.Combine(cacheRoot, relative);
        }

        return cacheRoot;
    }
}
