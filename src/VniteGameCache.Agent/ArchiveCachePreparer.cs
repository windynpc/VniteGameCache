using System.Diagnostics;
using System.Text.Json;
using VniteGameCache.Caching;
using VniteGameCache.LibraryImport;
using VniteGameCache.Persistence;

namespace VniteGameCache.Agent;

public static class ArchiveCachePreparer
{
    private const string ReadyFileName = ".vgc-archive-ready.json";
    private sealed record ReadyMarker(string ArchivePath, long SizeBytes, DateTimeOffset LastWriteUtc,
        string ExecutableRelativePath, long? PrepareDurationMs = null);

    public static async Task<CacheResult> PrepareAsync(GameRecord game, ArchiveSourceRecord source,
        long minimumFreeSpaceBytes, CancellationToken cancellationToken = default)
    {
        var prepareWatch = Stopwatch.StartNew();
        var cacheRoot = Path.GetFullPath(game.CacheRoot);
        var archivePath = Path.GetFullPath(source.ArchivePath);
        var executable = Path.GetFullPath(Path.Combine(cacheRoot, source.ExecutableRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsChildOf(executable, cacheRoot) || string.Equals(cacheRoot, Path.GetPathRoot(cacheRoot), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cacheRoot, Path.GetDirectoryName(archivePath), StringComparison.OrdinalIgnoreCase) ||
            IsChildOf(archivePath, cacheRoot))
        {
            return Failure(game.GameId, "invalid_archive_cache_path", "Archive cache path is unsafe.");
        }

        var markerPath = Path.Combine(cacheRoot, ReadyFileName);
        if (File.Exists(markerPath) && File.Exists(executable))
        {
            try
            {
                var marker = JsonSerializer.Deserialize<ReadyMarker>(await File.ReadAllTextAsync(markerPath, cancellationToken));
                if (marker is not null && string.Equals(marker.ArchivePath, archivePath, StringComparison.OrdinalIgnoreCase) &&
                    marker.ExecutableRelativePath == source.ExecutableRelativePath &&
                    (!File.Exists(archivePath) || MatchesSource(archivePath, marker)))
                {
                    return new CacheResult(CacheResultStatus.CacheAlreadyReady, game.GameId, false, null, null,
                        null, null, executable, Path.GetDirectoryName(executable));
                }
            }
            catch (JsonException) { }
        }

        if (Directory.Exists(cacheRoot) && Directory.EnumerateFileSystemEntries(cacheRoot).Any())
        {
            return Failure(game.GameId, "archive_cache_requires_review",
                "An existing cache contains data. Automatic replacement is blocked to protect saves and mods.");
        }
        if (!File.Exists(archivePath)) return Failure(game.GameId, "archive_unavailable", "Archive source is unavailable.");
        var archiveInfo = new FileInfo(archivePath);
        if (archiveInfo.Length != source.ArchiveSizeBytes || archiveInfo.LastWriteTimeUtc != source.ArchiveLastWriteUtc.UtcDateTime)
        {
            return Failure(game.GameId, "archive_changed", "Archive changed since import; preview it again before extraction.");
        }

        var sevenZip = Environment.GetEnvironmentVariable("VGC_7ZIP_PATH") ?? @"C:\Program Files\7-Zip\7z.exe";
        var preview = await ArchivePreview.ReadAsync(archivePath, sevenZip, cancellationToken);
        if (!preview.Supported || !preview.ExecutableCandidates.Contains(source.ExecutableRelativePath, StringComparer.OrdinalIgnoreCase) ||
            preview.FileCount != source.FileCount || preview.ExpandedBytes != source.ExpandedBytes)
        {
            return Failure(game.GameId, "archive_preview_mismatch", "Archive preview failed or no longer matches its import record.");
        }

        var drive = new DriveInfo(Path.GetPathRoot(cacheRoot)!);
        if (source.ExpandedBytes > long.MaxValue - Math.Max(0, minimumFreeSpaceBytes))
            return Failure(game.GameId, "archive_size_invalid", "Archive expanded size is too large.");
        var required = source.ExpandedBytes + Math.Max(0, minimumFreeSpaceBytes);
        if (drive.AvailableFreeSpace < required)
        {
            return Failure(game.GameId, "insufficient_space", "Not enough free space for staged extraction.");
        }

        var parent = Path.GetDirectoryName(cacheRoot)!;
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".vgc-archive-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var start = new ProcessStartInfo(sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("x");
            start.ArgumentList.Add("-y");
            start.ArgumentList.Add("-bso0");
            start.ArgumentList.Add("-bsp0");
            start.ArgumentList.Add("-bse1");
            start.ArgumentList.Add("-o" + stage);
            start.ArgumentList.Add(archivePath);
            using var process = Process.Start(start) ?? throw new IOException("Unable to start 7-Zip.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            try { await process.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
            await output;
            var errorText = await error;
            if (process.ExitCode != 0) throw new IOException($"7-Zip extraction failed ({process.ExitCode}): {errorText.Trim()}");

            long actualBytes = 0;
            var actualFiles = 0;
            var directories = new Stack<string>();
            directories.Push(stage);
            while (directories.Count > 0)
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directories.Pop()))
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Archive extracted a linked entry.");
                    if ((attributes & FileAttributes.Directory) != 0) { directories.Push(path); continue; }
                    actualFiles++;
                    actualBytes = checked(actualBytes + new FileInfo(path).Length);
                    if (actualFiles > source.FileCount || actualBytes > source.ExpandedBytes)
                        throw new IOException("Archive extraction exceeded its preview.");
                }
            }
            if (actualFiles != source.FileCount || actualBytes != source.ExpandedBytes ||
                !File.Exists(Path.Combine(stage, source.ExecutableRelativePath.Replace('/', Path.DirectorySeparatorChar))))
                throw new IOException("Extracted files do not match the preview or the selected executable is missing.");
            if (!MatchesSource(archivePath, new ReadyMarker(archivePath, archiveInfo.Length,
                    archiveInfo.LastWriteTimeUtc, source.ExecutableRelativePath)))
                throw new IOException("Archive changed during extraction.");

            var marker = new ReadyMarker(archivePath, archiveInfo.Length, archiveInfo.LastWriteTimeUtc,
                source.ExecutableRelativePath, prepareWatch.ElapsedMilliseconds);
            await File.WriteAllTextAsync(Path.Combine(stage, ReadyFileName), JsonSerializer.Serialize(marker), cancellationToken);
            if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot);
            Directory.Move(stage, cacheRoot);
            return new CacheResult(CacheResultStatus.Success, game.GameId, false, null, null,
                null, null, executable, Path.GetDirectoryName(executable));
        }
        catch (OperationCanceledException)
        {
            return Failure(game.GameId, "cancelled", "Archive extraction was cancelled.", CacheResultStatus.Cancelled);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OverflowException)
        {
            return Failure(game.GameId, "archive_extraction_failed", error.Message);
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    private static bool MatchesSource(string archivePath, ReadyMarker marker)
    {
        var info = new FileInfo(archivePath);
        return info.Length == marker.SizeBytes && info.LastWriteTimeUtc == marker.LastWriteUtc.UtcDateTime;
    }

    private static bool IsChildOf(string path, string root) =>
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static CacheResult Failure(string gameId, string code, string message,
        CacheResultStatus status = CacheResultStatus.VerificationFailed) =>
        new(status, gameId, false, code, message, null, null, null, null);
}
