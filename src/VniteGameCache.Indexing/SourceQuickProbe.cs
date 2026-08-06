using VniteGameCache.Persistence;

namespace VniteGameCache.Indexing;

public enum SourceQuickProbeStatus
{
    Unchanged,
    Changed,
    Unavailable,
    InsufficientBaseline
}

public sealed record SourceQuickProbeResult(
    SourceQuickProbeStatus Status,
    string Reason,
    string? RelativePath = null);

public sealed class SourceQuickProbe
{
    private const int MaxSentinelFiles = 96;
    private const int MaxShallowDirectories = 64;
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    public SourceQuickProbeResult Probe(
        GameRecord game,
        IReadOnlyList<FileEntryRecord> files,
        IReadOnlyList<DirectoryFingerprintRecord> directories)
    {
        if (!Directory.Exists(game.SourceRoot))
        {
            return new SourceQuickProbeResult(SourceQuickProbeStatus.Unavailable, "source_unavailable");
        }

        if (files.Count == 0 || directories.Count == 0)
        {
            return new SourceQuickProbeResult(SourceQuickProbeStatus.InsufficientBaseline, "source_probe_baseline_missing");
        }

        try
        {
            foreach (var file in SelectSentinelFiles(files))
            {
                var relativePath = NormalizeRelativePath(file.RelativePath);
                var absolutePath = ResolveUnderRoot(game.SourceRoot, relativePath);
                if (!File.Exists(absolutePath))
                {
                    return new SourceQuickProbeResult(SourceQuickProbeStatus.Changed, "source_file_missing", relativePath);
                }

                var info = new FileInfo(absolutePath);
                if (info.Length != file.SizeBytes)
                {
                    return new SourceQuickProbeResult(SourceQuickProbeStatus.Changed, "source_file_size_changed", relativePath);
                }

                var modifiedUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                if ((modifiedUtc - file.ModifiedUtc).Duration() > TimestampTolerance)
                {
                    return new SourceQuickProbeResult(SourceQuickProbeStatus.Changed, "source_file_time_changed", relativePath);
                }
            }

            foreach (var directory in SelectShallowDirectories(directories))
            {
                var relativePath = NormalizeRelativePath(directory.RelativePath);
                var absolutePath = ResolveUnderRoot(game.SourceRoot, relativePath);
                if (!Directory.Exists(absolutePath))
                {
                    return new SourceQuickProbeResult(SourceQuickProbeStatus.Changed, "source_directory_missing", relativePath);
                }

                var directFileCount = Directory.EnumerateFiles(absolutePath, "*", SearchOption.TopDirectoryOnly).LongCount();
                var directDirectoryCount = Directory.EnumerateDirectories(absolutePath, "*", SearchOption.TopDirectoryOnly).LongCount();
                if (directFileCount != directory.DirectFileCount || directDirectoryCount != directory.DirectDirectoryCount)
                {
                    return new SourceQuickProbeResult(SourceQuickProbeStatus.Changed, "source_directory_shape_changed", relativePath);
                }
            }

            return new SourceQuickProbeResult(SourceQuickProbeStatus.Unchanged, "source_probe_unchanged");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new SourceQuickProbeResult(SourceQuickProbeStatus.Unavailable, "source_probe_unavailable");
        }
    }

    private static IReadOnlyList<FileEntryRecord> SelectSentinelFiles(IReadOnlyList<FileEntryRecord> files)
    {
        var selected = files
            .Where(file => file.FileRole == FileRole.Executable)
            .Concat(files.Where(file => file.FileRole != FileRole.Excluded).OrderByDescending(file => file.ModifiedUtc).Take(48))
            .Concat(files.Where(file => file.FileRole != FileRole.Excluded).OrderByDescending(file => file.SizeBytes).Take(32))
            .Concat(files
                .Where(file => file.FileRole != FileRole.Excluded)
                .GroupBy(file => TopLevelSegment(file.RelativePath), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(file => file.ModifiedUtc).First())
                .Take(32))
            .GroupBy(file => NormalizeRelativePath(file.RelativePath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(MaxSentinelFiles)
            .ToList();
        return selected;
    }

    private static IReadOnlyList<DirectoryFingerprintRecord> SelectShallowDirectories(
        IReadOnlyList<DirectoryFingerprintRecord> directories) =>
        directories
            .Where(directory => RelativeDepth(directory.RelativePath) <= 1)
            .OrderBy(directory => RelativeDepth(directory.RelativePath))
            .ThenBy(directory => directory.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(MaxShallowDirectories)
            .ToList();

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Source probe path escapes source root: {relativePath}");
        }

        return candidate;
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').Trim('/');

    private static string TopLevelSegment(string path)
    {
        var normalized = NormalizeRelativePath(path);
        var separator = normalized.IndexOf('/');
        return separator < 0 ? string.Empty : normalized[..separator];
    }

    private static int RelativeDepth(string path)
    {
        var normalized = NormalizeRelativePath(path);
        return string.IsNullOrEmpty(normalized) ? 0 : normalized.Count(character => character == '/') + 1;
    }
}
