using System.Text.RegularExpressions;
using VniteGameCache.Persistence;

namespace VniteGameCache.Indexing;

public static class PathNormalizer
{
    public static string ToRelativePath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".") return string.Empty;
        relative = relative.Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) ||
            relative == ".." ||
            relative.StartsWith("/", StringComparison.Ordinal) ||
            Regex.IsMatch(relative, "^[A-Za-z]:", RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException($"Path escapes source root: {path}");
        }
        return relative;
    }
}

public sealed class ExclusionMatcher
{
    private static readonly string[] DefaultHardExcludes =
    [
        "$RECYCLE.BIN/**",
        "System Volume Information/**",
        "Thumbs.db",
        "desktop.ini"
    ];

    private static readonly string[] DefaultSuggestedExcludes =
    [
        "*.tmp",
        "*.dmp",
        "logs/**",
        "log/**",
        "cache/**",
        "temp/**",
        "tmp/**"
    ];

    private readonly IReadOnlyList<string> hardPatterns;
    private readonly IReadOnlyList<string> suggestedPatterns;
    private readonly IReadOnlySet<string> includedSuggestedPatterns;

    public ExclusionMatcher(IndexingOptions options)
    {
        hardPatterns = options.HardExcludePatterns ?? DefaultHardExcludes;
        suggestedPatterns = options.SuggestedExcludePatterns ?? DefaultSuggestedExcludes;
        includedSuggestedPatterns = options.IncludeSuggestedExcludePatterns ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public ExclusionHit? Match(string relativePath)
    {
        foreach (var pattern in hardPatterns)
        {
            if (GlobMatch(relativePath, pattern)) return new ExclusionHit(relativePath, pattern, "Hard");
        }

        foreach (var pattern in suggestedPatterns)
        {
            if (includedSuggestedPatterns.Contains(pattern)) continue;
            if (GlobMatch(relativePath, pattern)) return new ExclusionHit(relativePath, pattern, "Suggested");
        }

        return null;
    }

    public static bool GlobMatch(string path, string pattern)
    {
        path = path.Replace('\\', '/');
        pattern = pattern.Replace('\\', '/');
        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*\\*", "§DOUBLESTAR§", StringComparison.Ordinal)
            .Replace("\\*", "[^/]*", StringComparison.Ordinal)
            .Replace("\\?", "[^/]", StringComparison.Ordinal)
            .Replace("§DOUBLESTAR§", ".*", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(path, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

public static class FileClassifier
{
    public static FileRole Classify(string relativePath, ExclusionHit? exclusion)
    {
        if (exclusion is not null) return FileRole.Excluded;

        var path = relativePath.Replace('\\', '/');
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var lower = path.ToLowerInvariant();

        if (extension == ".exe") return FileRole.Executable;
        if (IsUnder(lower, "save") || IsUnder(lower, "savedata") || IsUnder(lower, "saves") || lower.EndsWith(".rpgsave", StringComparison.Ordinal)) return FileRole.Save;
        if (IsUnder(lower, "config") || extension is ".ini" or ".cfg" or ".json" or ".xml" or ".yaml" or ".yml") return FileRole.Config;
        if (IsUnder(lower, "screenshots") || extension is ".png" or ".jpg" or ".jpeg" or ".webp" && lower.Contains("screenshot", StringComparison.Ordinal)) return FileRole.Screenshot;
        if (IsUnder(lower, "mods") || IsUnder(lower, "mod")) return FileRole.Mod;
        if (extension is ".tmp" or ".temp") return FileRole.Temporary;
        if (extension is ".log" or ".dmp" || IsUnder(lower, "logs") || fileName.Contains("crash", StringComparison.OrdinalIgnoreCase)) return FileRole.Log;
        if (IsUnder(lower, "cache") || IsUnder(lower, "shadercache")) return FileRole.GeneratedCache;

        return FileRole.ImmutableGameData;
    }

    private static bool IsUnder(string lowerPath, string segment) =>
        lowerPath.Equals(segment, StringComparison.Ordinal) ||
        lowerPath.StartsWith(segment + "/", StringComparison.Ordinal) ||
        lowerPath.Contains("/" + segment + "/", StringComparison.Ordinal);
}

public static class CandidateDetectors
{
    public static IReadOnlyList<EngineHint> DetectEngines(IReadOnlyCollection<ScannedFile> files, IReadOnlyCollection<ScannedDirectory> directories)
    {
        var fileSet = files.Select(file => file.RelativePath.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dirSet = directories.Select(dir => dir.RelativePath.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hints = new List<EngineHint>();

        Add("RPG Maker", fileSet.Contains("game.exe") || dirSet.Contains("www") || dirSet.Contains("save") || dirSet.Contains("www/save") || fileSet.Any(path => path.EndsWith(".rpgsave", StringComparison.Ordinal)), ["Game.exe/www/save/rpgsave markers"]);
        Add("Ren'Py", dirSet.Contains("game") || dirSet.Contains("renpy") || fileSet.Any(path => path.EndsWith(".rpy", StringComparison.Ordinal) || path.EndsWith(".rpyc", StringComparison.Ordinal)) || dirSet.Contains("saves"), ["game/renpy/rpy/saves markers"]);
        Add("Kirikiri", fileSet.Any(path => path.EndsWith(".xp3", StringComparison.Ordinal)) || dirSet.Contains("savedata") || dirSet.Contains("save"), ["xp3/savedata/save markers"]);
        Add("Unity", fileSet.Contains("unityplayer.dll") || dirSet.Any(path => path.EndsWith("_data", StringComparison.Ordinal)) || dirSet.Any(path => path.EndsWith("streamingassets", StringComparison.Ordinal)), ["UnityPlayer.dll/*_Data/StreamingAssets markers"]);
        Add("Unreal", dirSet.Contains("engine") || dirSet.Contains("binaries/win64") || fileSet.Any(path => path.EndsWith("-win64-shipping.exe", StringComparison.Ordinal)) || dirSet.Contains("content/paks"), ["Engine/Binaries/Win64/Shipping/Paks markers"]);
        Add("SiglusEngine/BGI", fileSet.Any(path => path.EndsWith(".sig", StringComparison.Ordinal) || path.EndsWith(".arc", StringComparison.Ordinal)), ["sig/arc candidate markers"]);

        return hints.OrderBy(hint => hint.Engine, StringComparer.Ordinal).ToList();

        void Add(string engine, bool matched, IReadOnlyList<string> reasons)
        {
            if (matched) hints.Add(new EngineHint(engine, reasons));
        }
    }

    public static IReadOnlyList<ExecutableCandidate> DetectExecutables(
        IReadOnlyCollection<ScannedFile> files,
        string sourceRoot,
        string configuredExecutablePath)
    {
        var sourceName = new DirectoryInfo(sourceRoot).Name;
        var configuredRelative = string.IsNullOrWhiteSpace(configuredExecutablePath) || !Path.IsPathRooted(configuredExecutablePath)
            ? configuredExecutablePath.Replace('\\', '/')
            : PathNormalizer.ToRelativePath(sourceRoot, configuredExecutablePath);
        var fileSet = files.Select(file => file.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<ExecutableCandidate>();

        foreach (var file in files.Where(file => file.FileRole == FileRole.Executable))
        {
            var score = 0;
            var reasons = new List<string>();
            var name = Path.GetFileNameWithoutExtension(file.RelativePath);
            var fileName = Path.GetFileName(file.RelativePath);

            if (!file.RelativePath.Contains('/')) Add(30, "root executable");
            if (fileName.Equals("Game.exe", StringComparison.OrdinalIgnoreCase)) Add(40, "Game.exe convention");
            if (file.RelativePath.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase)) Add(45, "Unreal shipping executable");
            if (NameSimilar(name, sourceName)) Add(25, "name resembles game directory");
            if (file.RelativePath.Equals(configuredRelative, StringComparison.OrdinalIgnoreCase)) Add(50, "user configured executable");
            if (fileSet.Contains($"{name}_Data")) Add(35, "matching Unity *_Data directory");

            var lower = file.RelativePath.ToLowerInvariant();
            if (lower.Contains("unins", StringComparison.Ordinal) || lower.Contains("uninstall", StringComparison.Ordinal)) Add(-60, "uninstaller penalty");
            if (lower.Contains("setup", StringComparison.Ordinal) || lower.Contains("install", StringComparison.Ordinal) || lower.Contains("update", StringComparison.Ordinal)) Add(-45, "installer/updater penalty");
            if (lower.Contains("crashreporter", StringComparison.Ordinal) || lower.Contains("unitycrashhandler", StringComparison.Ordinal) || lower.Contains("vc_redist", StringComparison.Ordinal) || lower.Contains("dxsetup", StringComparison.Ordinal)) Add(-80, "runtime/crash helper penalty");

            candidates.Add(new ExecutableCandidate(
                file.RelativePath,
                score,
                reasons,
                score >= 20,
                score >= 40,
                file.SizeBytes,
                file.ModifiedUtc));

            void Add(int points, string reason)
            {
                score += points;
                reasons.Add($"{points:+#;-#;0}: {reason}");
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<MutablePathCandidate> DetectMutablePaths(IReadOnlyCollection<ScannedDirectory> directories)
    {
        var known = new Dictionary<string, (SyncMode Mode, double Confidence, string Reason)>(StringComparer.OrdinalIgnoreCase)
        {
            ["save"] = (SyncMode.Bidirectional, 0.95, "common save directory"),
            ["savedata"] = (SyncMode.Bidirectional, 0.95, "common save data directory"),
            ["saves"] = (SyncMode.Bidirectional, 0.95, "common saves directory"),
            ["userdata"] = (SyncMode.Bidirectional, 0.75, "common user data directory"),
            ["config"] = (SyncMode.Bidirectional, 0.7, "common config directory"),
            ["screenshots"] = (SyncMode.LocalToNas, 0.65, "common screenshot output directory"),
            ["mods"] = (SyncMode.Bidirectional, 0.6, "common mod directory"),
            ["www/save"] = (SyncMode.Bidirectional, 0.9, "RPG Maker www save directory")
        };

        return directories
            .Where(directory => known.ContainsKey(directory.RelativePath.ToLowerInvariant()))
            .Select(directory =>
            {
                var rule = known[directory.RelativePath.ToLowerInvariant()];
                return new MutablePathCandidate(directory.RelativePath, PathKind.GameRelative, rule.Mode, rule.Confidence, [rule.Reason]);
            })
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.PathTemplate, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool NameSimilar(string executableName, string directoryName)
    {
        static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9\\u4e00-\\u9fff]", "");
        var left = Normalize(executableName);
        var right = Normalize(directoryName);
        return left.Length > 0 && right.Length > 0 && (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal));
    }
}
