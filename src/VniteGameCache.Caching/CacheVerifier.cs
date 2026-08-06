using VniteGameCache.Persistence;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VniteGameCache.Caching;

public sealed class CacheVerifier
{
    private const string ReadyMarkerFileName = ".vgc-cache-ready.json";

    public void VerifyFast(CopyPlan plan, IReadOnlyList<FileEntryRecord> files)
    {
        if (!Directory.Exists(plan.CacheRoot))
        {
            throw new CacheException(CacheResultStatus.VerificationFailed, "cache_root_missing", "Cache root missing after copy.");
        }

        var executable = Path.Combine(plan.CacheRoot, plan.ExecutableRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(executable))
        {
            throw new CacheException(CacheResultStatus.VerificationFailed, "cache_executable_missing", "Cache executable missing after copy.");
        }

        if (TryReadValidReadyMarker(plan))
        {
            return;
        }

        var requiredFiles = files
            .Where(file => file.FileRole is FileRole.Executable or FileRole.ImmutableGameData)
            .ToList();
        var existingCount = 0;
        foreach (var file in requiredFiles)
        {
            var target = Path.Combine(plan.CacheRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(target)) continue;
            var info = new FileInfo(target);
            if (info.Length != file.SizeBytes)
            {
                throw new CacheException(CacheResultStatus.VerificationFailed, "cache_file_size_mismatch", $"Cache file size mismatch: {file.RelativePath}");
            }
            existingCount++;
        }

        if (existingCount != requiredFiles.Count)
        {
            throw new CacheException(CacheResultStatus.VerificationFailed, "cache_file_count_mismatch", $"Cache file count mismatch: {existingCount}/{requiredFiles.Count}");
        }
    }

    public void WriteReadyMarker(CopyPlan plan)
    {
        Directory.CreateDirectory(plan.CacheRoot);
        var marker = new CacheReadyMarker
        {
            GameId = plan.GameId,
            SourceRevision = plan.SourceRevision,
            ExecutableRelativePath = plan.ExecutableRelativePath,
            WrittenUtc = DateTimeOffset.UtcNow
        };
        var markerPath = Path.Combine(plan.CacheRoot, ReadyMarkerFileName);
        var tempPath = $"{markerPath}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(marker, JsonOptions));
        File.Move(tempPath, markerPath, overwrite: true);
    }

    private static bool TryReadValidReadyMarker(CopyPlan plan)
    {
        var markerPath = Path.Combine(plan.CacheRoot, ReadyMarkerFileName);
        if (!File.Exists(markerPath)) return false;

        try
        {
            var marker = JsonSerializer.Deserialize<CacheReadyMarker>(File.ReadAllText(markerPath), JsonOptions);
            return marker is not null &&
                   string.Equals(marker.GameId, plan.GameId, StringComparison.OrdinalIgnoreCase) &&
                   marker.SourceRevision == plan.SourceRevision &&
                   string.Equals(NormalizeRelativePath(marker.ExecutableRelativePath), NormalizeRelativePath(plan.ExecutableRelativePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string NormalizeRelativePath(string value) => value.Replace('\\', '/').TrimStart('/');

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private sealed record CacheReadyMarker
    {
        [JsonPropertyName("game_id")]
        public required string GameId { get; init; }

        [JsonPropertyName("source_revision")]
        public long SourceRevision { get; init; }

        [JsonPropertyName("executable_relative_path")]
        public required string ExecutableRelativePath { get; init; }

        [JsonPropertyName("written_utc")]
        public DateTimeOffset WrittenUtc { get; init; }
    }
}
