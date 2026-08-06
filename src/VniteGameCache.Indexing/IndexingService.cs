using VniteGameCache.Persistence;

namespace VniteGameCache.Indexing;

public sealed class IndexingService(
    IndexingStore store,
    FileSystemScanner scanner,
    ManifestBuilder manifestBuilder,
    IndexingOptions options)
{
    public IndexingResult IndexGame(string gameId, IProgress<IndexingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var taskId = $"index-game-{gameId}-{Guid.NewGuid():N}";
        GameRecord? game = null;

        try
        {
            game = store.GetGame(gameId);
            if (game is null)
            {
                return new IndexingResult(gameId, ManifestStatus.Failed, 0, 0, 0, 0, null, [], IndexingErrorCode.GameNotFound.ToString(), $"Game not found: {gameId}");
            }

            store.CreateTask(new TaskRecord
            {
                TaskId = taskId,
                GameId = game.GameId,
                TaskType = "IndexGame",
                State = TaskState.Checking,
                StartedUtc = DateTimeOffset.UtcNow,
                RecoveryPayloadJson = $"{{\"game_id\":\"{EscapeJson(game.GameId)}\",\"source_root\":\"{EscapeJson(game.SourceRoot)}\"}}"
            });

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new IndexingProgress("Scanning", game.SourceRoot, 0, 0, 0, 0));
            var scan = scanner.Scan(game.SourceRoot, game.ExecutablePath, options, progress, cancellationToken);
            var committedUtc = DateTimeOffset.UtcNow;
            store.CommitFullScan(game, scan, taskId, committedUtc);

            var updatedGame = game with
            {
                SourceRevision = game.SourceRevision + 1,
                LastFullScanUtc = committedUtc,
                CacheSizeBytes = scan.TotalSizeBytes,
                UpdatedUtc = committedUtc
            };
            var manifestPath = manifestBuilder.WriteAtomic(updatedGame, scan, updatedGame.SourceRevision, options);

            return new IndexingResult(
                game.GameId,
                scan.Status,
                updatedGame.SourceRevision,
                scan.Files.Count,
                scan.Directories.Count,
                scan.TotalSizeBytes,
                manifestPath,
                scan.Warnings);
        }
        catch (OperationCanceledException)
        {
            if (game is not null) store.MarkTaskFailed(taskId, TaskState.Cancelled, IndexingErrorCode.Cancelled, "Indexing was cancelled.");
            return new IndexingResult(gameId, ManifestStatus.Cancelled, game?.SourceRevision ?? 0, 0, 0, 0, null, [], IndexingErrorCode.Cancelled.ToString(), "Indexing was cancelled.");
        }
        catch (IndexingException error)
        {
            if (game is not null) store.MarkTaskFailed(taskId, TaskState.Failed, error.Code, error.Message);
            return new IndexingResult(gameId, ManifestStatus.Failed, game?.SourceRevision ?? 0, 0, 0, 0, null, [], error.Code.ToString(), error.Message);
        }
        catch (Exception error)
        {
            if (game is not null) store.MarkTaskFailed(taskId, TaskState.Failed, IndexingErrorCode.UnexpectedError, error.Message);
            return new IndexingResult(gameId, ManifestStatus.Failed, game?.SourceRevision ?? 0, 0, 0, 0, null, [], IndexingErrorCode.UnexpectedError.ToString(), error.Message);
        }
    }

    private static string EscapeJson(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
