using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VniteGameCache.Indexing;
using VniteGameCache.Persistence;

namespace VniteGameCache.LibraryImport;

public sealed class LibraryDiscoveryService(GameRepository games)
{
    private static readonly string[] DefaultIgnoredNames =
    [
        "Data", "www", "StreamingAssets", "save", "savedata", "patch", "mods",
        "Binaries", "Engine", "Content", "game", "renpy"
    ];

    public LibraryDryRunResult Discover(LibraryImportConfig config)
    {
        var scanDepth = Math.Clamp(config.ScanDepth, 1, 3);
        var candidates = new List<LibraryCandidate>();
        var existing = games.List();
        foreach (var libraryRoot in config.LibraryRoots)
        {
            if (!Directory.Exists(libraryRoot)) continue;
            DiscoverUnderRoot(libraryRoot, scanDepth, config.IgnoredDirectoryPatterns, existing, candidates);
        }

        return new LibraryDryRunResult(
            candidates,
            candidates.Count(candidate => candidate.ImportStatus == ImportStatus.AutoImportable),
            candidates.Count(candidate => candidate.ImportStatus == ImportStatus.Existing),
            candidates.Count(candidate => candidate.ImportStatus == ImportStatus.NeedsConfirmation),
            candidates.Count(candidate => candidate.ImportStatus == ImportStatus.Ignored));
    }

    private void DiscoverUnderRoot(
        string libraryRoot,
        int scanDepth,
        IReadOnlyList<string>? ignoredDirectoryPatterns,
        IReadOnlyList<GameRecord> existing,
        List<LibraryCandidate> candidates)
    {
        foreach (var child in Directory.EnumerateDirectories(libraryRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            DiscoverDirectory(child, depth: 1, scanDepth, ignoredDirectoryPatterns, existing, candidates);
        }
    }

    private void DiscoverDirectory(
        string sourceRoot,
        int depth,
        int scanDepth,
        IReadOnlyList<string>? ignoredDirectoryPatterns,
        IReadOnlyList<GameRecord> existing,
        List<LibraryCandidate> candidates)
    {
        var folderName = Path.GetFileName(sourceRoot);
        if (ShouldIgnore(folderName, ignoredDirectoryPatterns))
        {
            if (depth == 1)
            {
                candidates.Add(CreateIgnored(sourceRoot, folderName));
            }
            return;
        }

        var childDirectories = Directory.EnumerateDirectories(sourceRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        var candidate = CreateCandidate(sourceRoot, folderName, existing);
        if (candidate.PreferredExecutable is not null || depth >= scanDepth || childDirectories.Count == 0)
        {
            candidates.Add(candidate);
            return;
        }

        foreach (var child in childDirectories)
        {
            DiscoverDirectory(child, depth + 1, scanDepth, ignoredDirectoryPatterns, existing, candidates);
        }
    }

    private static bool ShouldIgnore(string folderName, IReadOnlyList<string>? configuredPatterns)
    {
        if (DefaultIgnoredNames.Any(name => name.Equals(folderName, StringComparison.OrdinalIgnoreCase))) return true;
        return configuredPatterns?.Any(pattern => ExclusionMatcher.GlobMatch(folderName, pattern)) == true;
    }

    private LibraryCandidate CreateCandidate(string sourceRoot, string folderName, IReadOnlyList<GameRecord> existing)
    {
        var files = DiscoverExecutableFiles(sourceRoot);
        var directories = DiscoverShallowDirectories(sourceRoot);
        var executables = CandidateDetectors.DetectExecutables(files, sourceRoot, string.Empty);
        var engines = CandidateDetectors.DetectEngines(files, directories);
        var preferred = executables.FirstOrDefault();
        var existingGame = existing.FirstOrDefault(game => PathsEqual(game.SourceRoot, sourceRoot));
        var confidence = ComputeConfidence(executables, preferred);
        var warnings = new List<string>();
        var status = ImportStatus.AutoImportable;

        if (existingGame is not null)
        {
            status = ImportStatus.Existing;
        }
        else if (preferred is null)
        {
            status = ImportStatus.NeedsConfirmation;
            warnings.Add("No executable candidate found.");
        }
        else if (executables.Count > 1 && executables[1].Score >= 20)
        {
            status = ImportStatus.NeedsConfirmation;
            warnings.Add("Multiple executable candidates have similar scores.");
        }
        else if (confidence < 0.75)
        {
            status = ImportStatus.NeedsConfirmation;
            warnings.Add("Executable confidence is below automatic import threshold.");
        }

        return new LibraryCandidate(
            CandidateIds.FromSourceRoot(sourceRoot),
            sourceRoot,
            folderName,
            executables,
            preferred,
            confidence,
            engines,
            existingGame?.VniteGameId,
            existingGame?.GameId,
            status,
            warnings);
    }

    private static LibraryCandidate CreateIgnored(string sourceRoot, string folderName) => new(
        CandidateIds.FromSourceRoot(sourceRoot),
        sourceRoot,
        folderName,
        [],
        null,
        0,
        [],
        null,
        null,
        ImportStatus.Ignored,
        ["Directory name is ignored by discovery rules."]);

    private static IReadOnlyList<ScannedFile> DiscoverExecutableFiles(string sourceRoot)
    {
        var files = new List<ScannedFile>();
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.exe", SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(file);
            files.Add(new ScannedFile(
                Path.GetFileName(file),
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                FileRole.Executable));
        }
        return files;
    }

    private static IReadOnlyList<ScannedDirectory> DiscoverShallowDirectories(string sourceRoot)
    {
        var result = new List<ScannedDirectory> { new(string.Empty, 0, 0, 0, null) };
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.TopDirectoryOnly))
        {
            result.Add(new ScannedDirectory(Path.GetFileName(directory), 0, 0, 0, null));
        }
        return result;
    }

    private static double ComputeConfidence(IReadOnlyList<ExecutableCandidate> executables, ExecutableCandidate? preferred)
    {
        if (preferred is null) return 0;
        var confidence = Math.Clamp(preferred.Score / 100.0, 0, 1);
        if (executables.Count == 1 && preferred.Score >= 20) confidence = Math.Max(confidence, 0.85);
        return Math.Clamp(confidence, 0, 1);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}

public sealed class LibraryImportService(GameRepository games)
{
    public LibraryImportResult Import(LibraryImportConfig config, IReadOnlyList<LibraryCandidate> candidates, bool importNeedsConfirmation = false)
    {
        var imported = new List<ImportedGame>();
        var skipped = new List<LibraryCandidate>();

        foreach (var candidate in candidates)
        {
            if (candidate.ImportStatus is ImportStatus.Ignored or ImportStatus.Existing)
            {
                skipped.Add(candidate);
                continue;
            }

            if (candidate.ImportStatus == ImportStatus.NeedsConfirmation && !importNeedsConfirmation)
            {
                skipped.Add(candidate);
                continue;
            }

            if (candidate.PreferredExecutable is null)
            {
                skipped.Add(candidate with { ImportStatus = ImportStatus.Failed });
                continue;
            }

            var gameId = CandidateIds.GameIdFromSourceRoot(candidate.SourceRoot);
            var cacheRoot = CacheRootBuilder.Build(config.DefaultCacheRoot, candidate.FolderName, gameId);
            var executable = Path.Combine(candidate.SourceRoot, candidate.PreferredExecutable.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var now = DateTimeOffset.UtcNow;
            var existing = games.GetById(gameId);
            var record = new GameRecord
            {
                GameId = gameId,
                VniteGameId = existing?.VniteGameId,
                DisplayName = existing?.DisplayName ?? candidate.FolderName,
                SourceRoot = candidate.SourceRoot,
                CacheRoot = existing?.CacheRoot ?? cacheRoot,
                ExecutablePath = existing?.ExecutablePath is { Length: > 0 } ? existing.ExecutablePath : executable,
                ArgumentsJson = existing?.ArgumentsJson ?? "[]",
                WorkingDirectory = existing?.WorkingDirectory is { Length: > 0 } ? existing.WorkingDirectory : candidate.SourceRoot,
                LaunchPolicy = existing?.LaunchPolicy ?? LaunchPolicy.FastLaunch,
                CachePolicy = existing?.CachePolicy ?? CachePolicy.CacheBeforeLaunch,
                VerificationPolicy = existing?.VerificationPolicy ?? VerificationPolicy.Fast,
                SourceRevision = existing?.SourceRevision ?? 0,
                CacheRevision = existing?.CacheRevision ?? 0,
                LastFullScanUtc = existing?.LastFullScanUtc,
                LastIncrementalScanUtc = existing?.LastIncrementalScanUtc,
                LastPlayedUtc = existing?.LastPlayedUtc,
                CacheSizeBytes = existing?.CacheSizeBytes ?? 0,
                IsPinned = existing?.IsPinned ?? false,
                CacheStatus = existing?.CacheStatus ?? CacheStatus.NotCached,
                CreatedUtc = existing?.CreatedUtc ?? now,
                UpdatedUtc = now
            };
            games.Upsert(record);
            imported.Add(new ImportedGame(candidate.CandidateId, gameId, candidate.SourceRoot, record.CacheRoot, record.ExecutablePath, config.DefaultLaunchMode));
        }

        return new LibraryImportResult(imported, skipped);
    }
}

public static class CandidateIds
{
    public static string FromSourceRoot(string sourceRoot) => "candidate-" + ShortHash(Normalize(sourceRoot), 16);
    public static string GameIdFromSourceRoot(string sourceRoot)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(sourceRoot)));
        var hex = Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
    private static string ShortHash(string value, int length)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..length];
    }
}

public static class CacheRootBuilder
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string Build(string defaultCacheRoot, string folderName, string gameId)
    {
        var sanitized = Regex.Replace(folderName, "[<>:\"/\\\\|?*\\x00-\\x1F]", "_").Trim().TrimEnd('.');
        sanitized = Regex.Replace(sanitized, "\\s+", " ");
        if (string.IsNullOrWhiteSpace(sanitized) || Reserved.Contains(sanitized)) sanitized = "game";
        if (sanitized.Length > 48) sanitized = sanitized[..48].Trim();
        var suffix = gameId.Length > 8 ? gameId[^8..] : gameId;
        return Path.Combine(defaultCacheRoot, $"{sanitized}-{suffix}");
    }
}
