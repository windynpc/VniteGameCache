using System.Text.Encodings.Web;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VniteGameCache.Persistence;

namespace VniteGameCache.Indexing;

public sealed class ManifestBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    static ManifestBuilder()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public string WriteAtomic(GameRecord game, ScanResult scan, long sourceRevision, IndexingOptions options)
    {
        try
        {
            Directory.CreateDirectory(options.ManifestRoot);
            var manifestPath = Path.Combine(options.ManifestRoot, $"{SafeFileName(game.GameId)}.json");
            var tempPath = manifestPath + ".tmp";
            var manifest = Build(game, scan, sourceRevision);
            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, manifestPath, overwrite: true);
            return manifestPath;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new IndexingException(IndexingErrorCode.ManifestWriteFailed, error.Message, error);
        }
    }

    public GameManifest Build(GameRecord game, ScanResult scan, long sourceRevision) => new(
        SchemaVersion: 1,
        GameId: game.GameId,
        SourceRoot: game.SourceRoot,
        CacheRoot: game.CacheRoot,
        GeneratedAtUtc: DateTimeOffset.UtcNow,
        ScanStartedUtc: scan.ScanStartedUtc,
        ScanCompletedUtc: scan.ScanCompletedUtc,
        SourceRevision: sourceRevision,
        CacheRevision: game.CacheRevision,
        Status: scan.Status.ToString(),
        WarningCount: scan.Warnings.Count,
        FileCount: scan.Files.Count,
        DirectoryCount: scan.Directories.Count,
        TotalSizeBytes: scan.TotalSizeBytes,
        ExecutableCandidates: scan.ExecutableCandidates,
        MutablePathCandidates: scan.MutablePathCandidates,
        EngineHints: scan.EngineHints,
        ExclusionSummary: scan.Exclusions
            .GroupBy(exclusion => new { exclusion.Kind, exclusion.Pattern })
            .OrderBy(group => group.Key.Kind, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Pattern, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ExclusionSummaryItem(group.Key.Kind, group.Key.Pattern, group.Count()))
            .ToList(),
        ExcludedPaths: scan.Exclusions.OrderBy(exclusion => exclusion.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
        Warnings: scan.Warnings.OrderBy(warning => warning.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
        Files: scan.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
        DirectoryFingerprints: scan.Directories.OrderBy(directory => directory.RelativePath, StringComparer.OrdinalIgnoreCase).ToList());

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
    }
}

public sealed record GameManifest(
    int SchemaVersion,
    string GameId,
    string SourceRoot,
    string CacheRoot,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset ScanStartedUtc,
    DateTimeOffset ScanCompletedUtc,
    long SourceRevision,
    long CacheRevision,
    string Status,
    int WarningCount,
    int FileCount,
    int DirectoryCount,
    long TotalSizeBytes,
    IReadOnlyList<ExecutableCandidate> ExecutableCandidates,
    IReadOnlyList<MutablePathCandidate> MutablePathCandidates,
    IReadOnlyList<EngineHint> EngineHints,
    IReadOnlyList<ExclusionSummaryItem> ExclusionSummary,
    IReadOnlyList<ExclusionHit> ExcludedPaths,
    IReadOnlyList<IndexingWarning> Warnings,
    IReadOnlyList<ScannedFile> Files,
    IReadOnlyList<ScannedDirectory> DirectoryFingerprints);

public sealed record ExclusionSummaryItem(string Kind, string Pattern, int Count);
