namespace VniteGameCache.Persistence;

public sealed class WrapperGameResolutionException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}

public sealed record WrapperGameResolutionHints(string? VniteId = null, string? SourceRoot = null);

public static class WrapperGameResolver
{
    public static GameRecord Resolve(GameRepository repository, string gameId, WrapperGameResolutionHints? hints = null)
    {
        var direct = repository.GetById(gameId) ?? repository.GetByVniteGameId(gameId);
        if (direct is not null)
        {
            return direct;
        }

        var candidates = new Dictionary<string, GameRecord>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(hints?.VniteId))
        {
            AddCandidate(repository.GetByVniteGameId(hints.VniteId), candidates);
        }

        if (!string.IsNullOrWhiteSpace(hints?.SourceRoot))
        {
            foreach (var candidate in repository.List().Where(game => SamePath(game.SourceRoot, hints.SourceRoot)))
            {
                AddCandidate(candidate, candidates);
            }
        }

        if (candidates.Count == 1)
        {
            var game = candidates.Values.Single();
            if (!string.Equals(game.VniteGameId, gameId, StringComparison.OrdinalIgnoreCase))
            {
                game = game with
                {
                    VniteGameId = gameId,
                    UpdatedUtc = DateTimeOffset.UtcNow
                };
                repository.Update(game);
            }

            return game;
        }

        if (candidates.Count > 1)
        {
            throw new WrapperGameResolutionException("AmbiguousGameRecord", $"Multiple GameRecords match launch ID {gameId}; manual confirmation is required.");
        }

        throw new WrapperGameResolutionException("GameRecordNotFound", $"Game not found: {gameId}");
    }

    private static void AddCandidate(GameRecord? game, IDictionary<string, GameRecord> candidates)
    {
        if (game is null) return;
        candidates[game.GameId] = game;
    }

    private static bool SamePath(string left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left.TrimEnd('\\', '/'), right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
