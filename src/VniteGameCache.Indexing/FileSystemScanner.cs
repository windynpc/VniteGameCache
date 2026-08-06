using System.Collections.Concurrent;
using VniteGameCache.Persistence;

namespace VniteGameCache.Indexing;

public sealed class FileSystemScanner
{
    public ScanResult Scan(
        string sourceRoot,
        string configuredExecutablePath,
        IndexingOptions options,
        IProgress<IndexingProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceRoot))
        {
            throw new IndexingException(IndexingErrorCode.SourceRootNotFound, $"Source root not found: {sourceRoot}");
        }

        var started = DateTimeOffset.UtcNow;
        var files = new List<ScannedFile>();
        var directories = new List<ScannedDirectory>();
        var warnings = new List<IndexingWarning>();
        var exclusions = new List<ExclusionHit>();
        var matcher = new ExclusionMatcher(options);
        var counters = new ScanCounters();

        Walk(NormalizeRoot(sourceRoot), string.Empty);

        var completed = DateTimeOffset.UtcNow;
        var executableCandidates = CandidateDetectors.DetectExecutables(files, sourceRoot, configuredExecutablePath);
        var mutableCandidates = CandidateDetectors.DetectMutablePaths(directories);
        var engineHints = CandidateDetectors.DetectEngines(files, directories);

        return new ScanResult(
            files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
            directories.OrderBy(directory => directory.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
            executableCandidates,
            mutableCandidates,
            exclusions.OrderBy(exclusion => exclusion.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
            engineHints,
            warnings,
            started,
            completed);

        DirectoryStats Walk(string absoluteDirectory, string relativeDirectory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new IndexingProgress("Scanning", relativeDirectory, counters.FileCount, counters.DirectoryCount, counters.ProcessedBytes, warnings.Count));

            var directFileCount = 0L;
            var directDirectoryCount = 0L;
            var aggregateSize = 0L;
            DateTimeOffset? latestModified = null;

            IEnumerable<string> childDirectories;
            IEnumerable<string> childFiles;
            try
            {
                childDirectories = Directory.EnumerateDirectories(absoluteDirectory).ToList();
                childFiles = Directory.EnumerateFiles(absoluteDirectory).ToList();
            }
            catch (UnauthorizedAccessException error)
            {
                warnings.Add(new IndexingWarning(IndexingErrorCode.AccessDenied, relativeDirectory, error.Message));
                return new DirectoryStats(0, null);
            }
            catch (IOException error)
            {
                warnings.Add(new IndexingWarning(IndexingErrorCode.EnumerationFailed, relativeDirectory, error.Message));
                return new DirectoryStats(0, null);
            }

            foreach (var filePath in childFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = PathNormalizer.ToRelativePath(sourceRoot, filePath);
                var exclusion = matcher.Match(relativePath);
                if (exclusion is not null) exclusions.Add(exclusion);

                try
                {
                    var info = new FileInfo(filePath);
                    var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                    var role = FileClassifier.Classify(relativePath, exclusion);
                    files.Add(new ScannedFile(relativePath, info.Length, modified, role));
                    directFileCount++;
                    counters.FileCount++;
                    counters.ProcessedBytes += info.Length;
                    if (role != FileRole.Excluded) aggregateSize += info.Length;
                    latestModified = Max(latestModified, modified);
                }
                catch (UnauthorizedAccessException error)
                {
                    warnings.Add(new IndexingWarning(IndexingErrorCode.AccessDenied, relativePath, error.Message));
                }
                catch (IOException error)
                {
                    warnings.Add(new IndexingWarning(IndexingErrorCode.EnumerationFailed, relativePath, error.Message));
                }
            }

            foreach (var directoryPath in childDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = PathNormalizer.ToRelativePath(sourceRoot, directoryPath);
                var exclusion = matcher.Match(relativePath + "/");
                if (exclusion is not null)
                {
                    exclusions.Add(exclusion);
                    directDirectoryCount++;
                    continue;
                }

                var attributes = File.GetAttributes(directoryPath);
                if (!options.FollowReparsePoints && attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    warnings.Add(new IndexingWarning(IndexingErrorCode.ReparsePointSkipped, relativePath, "Reparse point skipped."));
                    directDirectoryCount++;
                    continue;
                }

                directDirectoryCount++;
                counters.DirectoryCount++;
                var childStats = Walk(directoryPath, relativePath);
                aggregateSize += childStats.AggregateSizeBytes;
                latestModified = Max(latestModified, childStats.LatestKnownModifiedUtc);
            }

            directories.Add(new ScannedDirectory(relativeDirectory, directFileCount, directDirectoryCount, aggregateSize, latestModified));
            return new DirectoryStats(aggregateSize, latestModified);
        }
    }

    private static string NormalizeRoot(string sourceRoot) => Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left > right ? left : right;
    }

    private sealed record DirectoryStats(long AggregateSizeBytes, DateTimeOffset? LatestKnownModifiedUtc);

    private sealed class ScanCounters
    {
        public long FileCount;
        public long DirectoryCount = 1;
        public long ProcessedBytes;
    }
}

public sealed class IndexingException(IndexingErrorCode code, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public IndexingErrorCode Code { get; } = code;
}
