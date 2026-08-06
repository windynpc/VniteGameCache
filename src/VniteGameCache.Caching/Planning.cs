using VniteGameCache.Persistence;

namespace VniteGameCache.Caching;

public sealed class CopyPlanner
{
    public CopyPlan Build(GameRecord game, IReadOnlyList<FileEntryRecord> files, bool forceRefresh, VerificationPolicy verificationPolicy)
    {
        var executableRelativePath = ResolveExecutableRelativePath(game);
        var cacheExecutable = Path.Combine(game.CacheRoot, executableRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var cacheRootExists = Directory.Exists(game.CacheRoot);
        var cacheExecutableExists = File.Exists(cacheExecutable);
        var cacheBadState = game.CacheStatus is CacheStatus.Incomplete or CacheStatus.Failed;
        var revisionReady = game.CacheStatus == CacheStatus.Ready && game.SourceRevision == game.CacheRevision;
        var required = forceRefresh || !cacheRootExists || !cacheExecutableExists || cacheBadState || !revisionReady || game.CacheRevision < game.SourceRevision;
        CachePathSafety.Validate(game.SourceRoot, game.CacheRoot, requireSource: required);

        var reason = required switch
        {
            false when revisionReady => "cache_revision_matches_source_revision",
            false => "cache_ready",
            _ when forceRefresh => "force_refresh",
            _ when !cacheRootExists => "cache_root_missing",
            _ when !cacheExecutableExists => "cache_executable_missing",
            _ when cacheBadState => "cache_status_requires_refresh",
            _ => "cache_revision_behind"
        };

        return new CopyPlan(
            game.GameId,
            game.SourceRoot,
            game.CacheRoot,
            game.SourceRevision,
            game.CacheRevision,
            required,
            reason,
            files.Count(file => file.FileRole != FileRole.Excluded),
            files.Where(file => file.FileRole != FileRole.Excluded).Sum(file => file.SizeBytes),
            executableRelativePath,
            verificationPolicy);
    }

    private static string ResolveExecutableRelativePath(GameRecord game)
    {
        if (string.IsNullOrWhiteSpace(game.ExecutablePath)) return "Game.exe";
        if (!Path.IsPathRooted(game.ExecutablePath)) return game.ExecutablePath.Replace('\\', '/');
        var relative = Path.GetRelativePath(game.SourceRoot, game.ExecutablePath).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) || relative == ".." || relative.StartsWith("/", StringComparison.Ordinal))
        {
            throw new CacheException(CacheResultStatus.InvalidCachePath, "executable_outside_source", "Configured executable_path is outside source_root.");
        }
        return relative;
    }
}

public static class CachePathSafety
{
    public const long DefaultMinimumFreeSpaceBytes = 20L * 1024 * 1024 * 1024;

    public static void Validate(string sourceRoot, string cacheRoot, bool requireSource = true)
    {
        if (requireSource && !Directory.Exists(sourceRoot))
        {
            throw new CacheException(CacheResultStatus.SourceUnavailable, "source_unavailable", $"Source root not found: {sourceRoot}");
        }

        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            throw new CacheException(CacheResultStatus.InvalidCachePath, "cache_root_empty", "cache_root is empty.");
        }

        var fullSource = Normalize(sourceRoot);
        var fullCache = Normalize(cacheRoot);

        if (string.Equals(fullSource, fullCache, StringComparison.OrdinalIgnoreCase))
        {
            throw new CacheException(CacheResultStatus.InvalidCachePath, "cache_equals_source", "cache_root must not equal source_root.");
        }

        if (IsSubPath(fullCache, fullSource) || IsSubPath(fullSource, fullCache))
        {
            throw new CacheException(CacheResultStatus.InvalidCachePath, "source_cache_overlap", "source_root and cache_root must not overlap.");
        }

        if (IsDangerousRoot(fullCache))
        {
            throw new CacheException(CacheResultStatus.InvalidCachePath, "dangerous_cache_root", "cache_root must not be a drive, system, or user profile root.");
        }
    }

    public static void EnsureSpace(string cacheRoot, long estimatedTotalBytes, long minimumFreeSpaceBytes)
    {
        var parent = ExistingParent(cacheRoot);
        var drive = new DriveInfo(Path.GetPathRoot(parent)!);
        if (drive.AvailableFreeSpace - estimatedTotalBytes < minimumFreeSpaceBytes)
        {
            throw new CacheException(CacheResultStatus.InsufficientSpace, "insufficient_space", $"Insufficient free space on cache volume: {drive.Name}");
        }
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsSubPath(string child, string parent) =>
        child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsDangerousRoot(string path)
    {
        var root = Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(root, normalized, StringComparison.OrdinalIgnoreCase)) return true;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalized, userProfile, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, localAppData, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExistingParent(string path)
    {
        var current = Path.GetFullPath(path);
        while (!Directory.Exists(current))
        {
            current = Directory.GetParent(current)?.FullName ?? throw new CacheException(CacheResultStatus.InvalidCachePath, "cache_parent_missing", "No existing parent directory for cache_root.");
        }
        return current;
    }
}

public sealed class RobocopyCommandBuilder(string logRoot)
{
    public RobocopyCommand Build(CopyPlan plan)
    {
        Directory.CreateDirectory(logRoot);
        var logPath = Path.Combine(logRoot, $"{plan.GameId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.robocopy.log");
        var args = new List<string>
        {
            plan.SourceRoot,
            plan.CacheRoot,
            "/E",
            "/Z",
            "/FFT",
            "/R:2",
            "/W:2",
            "/MT:8",
            "/COPY:DAT",
            "/DCOPY:T",
            "/XJ",
            "/TEE",
            $"/LOG:{logPath}"
        };
        return new RobocopyCommand(plan.SourceRoot, plan.CacheRoot, args, logPath);
    }
}
