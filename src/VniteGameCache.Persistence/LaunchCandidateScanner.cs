using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VniteGameCache.Persistence;

public sealed record LaunchCandidateScanOptions(int MaxDepth = 1, bool IncludeExistingGameRecord = true);

public sealed record EffectiveLaunchPlanPreview(
    string GameId,
    string SourceRoot,
    string CacheRoot,
    string WrapperArguments,
    string CandidateRelativePath,
    LaunchCandidateType CandidateType,
    LaunchCandidateRole CandidateRole,
    GameLaunchModeOverride LaunchMode,
    string WorkingDirectoryRelativePath,
    IReadOnlyList<string> Warnings);

public sealed class LaunchCandidateScanner
{
    private static readonly string[] CandidateExtensions = [".exe", ".bat", ".cmd", ".lnk"];
    private static readonly string[] SkipDirectories =
    [
        "cache", "caches", "backup", "backups", "save", "saves", "savedata", "userdata",
        "screenshots", "screenshot", "quarantine", "staging", "redist", "_redist", "runtime",
        "mtooldata", "mtoolscripts", "mcenter", "scripts", "www", "node_modules", "locales"
    ];
    private static readonly string[] TranslationKeywords =
    [
        "_cn", "-cn", " cn", "cn_", "cn-", "chs", "chinese", "zh-cn", "zh_cn", "zhcn",
        "sc", "simplified", "中文", "简中", "簡中", "汉化", "漢化", "汉化版", "漢化版",
        "机翻", "機翻", "ai翻译", "ai翻譯", "gpt", "chatgpt", "claude", "deepseek"
    ];
    private static readonly string[] DangerousKeywords = ["setup", "install", "uninstall", "unins", "update", "patch", "config", "crash", "redist", "vcredist", "dxwebsetup"];
    private static readonly string[] MToolBatchNames = ["与工具一同启动.bat", "StartWithTool.bat"];
    private static readonly Regex MToolInjectCommand = new(
        "(?im)^\\s*\"(?<inject>[^\"\\r\\n]*loaders[\\\\/]+inject\\.exe)\"\\s+\"(?<game>[^\"\\r\\n]+\\.exe)\"\\s+\"(?<loader>[^\"\\r\\n]+\\.dll)\"\\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public IReadOnlyList<GameLaunchCandidateRecord> Scan(GameRecord game, LaunchCandidateScanOptions? options = null)
    {
        options ??= new LaunchCandidateScanOptions();
        var root = NormalizeRoot(game.SourceRoot);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return [];

        var discovered = EnumerateCandidateFiles(root, Math.Max(0, options.MaxDepth)).ToList();
        var mtoolTargets = discovered
            .Where(path => IsMToolTemplate(path))
            .SelectMany(path => ReadMToolTargetNames(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = discovered
            .Select(path => BuildCandidate(game.GameId, root, path, LaunchCandidateSource.DiskScan, mtoolTargets))
            .ToList();

        if (options.IncludeExistingGameRecord && TryRelativePath(root, game.ExecutablePath) is { Length: > 0 } existingRelative)
        {
            var existing = Path.Combine(root, existingRelative);
            if (File.Exists(existing) && candidates.All(item => !item.RelativePath.Equals(existingRelative, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(BuildCandidate(game.GameId, root, existing, LaunchCandidateSource.ExistingGameRecord, mtoolTargets));
            }
        }

        var ordered = candidates
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Confidence)
            .ThenBy(item => item.RelativePath, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var primaryCandidateId = ordered.FirstOrDefault(item => !item.IsIgnored && item.LastTestStatus != LaunchCandidateTestStatus.HardBlocked)?.CandidateId;
        return ordered
            .Select((item, index) => item with
            {
                IsPrimary = !string.IsNullOrWhiteSpace(primaryCandidateId) && item.CandidateId == primaryCandidateId,
                FallbackPriority = item.IsIgnored ? null : index + 1
            })
            .ToList();
    }

    public EffectiveLaunchPlanPreview BuildPreview(GameRecord game, GameLaunchCandidateRecord? candidate)
    {
        var selected = candidate ?? Scan(game).FirstOrDefault(item => !item.IsIgnored);
        if (selected is null)
        {
            return new EffectiveLaunchPlanPreview(
                game.GameId,
                game.SourceRoot,
                game.CacheRoot,
                VniteGameCache.Contracts.LaunchArgumentCodec.Build(game.GameId),
                "",
                LaunchCandidateType.Unknown,
                LaunchCandidateRole.Unknown,
                game.LaunchModeOverride,
                "",
                ["没有可用启动候选。"]);
        }

        var warnings = new List<string>();
        if (selected.LastTestStatus == LaunchCandidateTestStatus.HardBlocked) warnings.Add("候选被硬阻止，不能设为默认。 ");
        if (selected.CandidateType is LaunchCandidateType.Batch or LaunchCandidateType.CommandScript && selected.CandidateRole != LaunchCandidateRole.MToolTemplate)
        {
            warnings.Add("未知脚本不会自动启动，只能人工确认。 ");
        }

        return new EffectiveLaunchPlanPreview(
            game.GameId,
            game.SourceRoot,
            game.CacheRoot,
            VniteGameCache.Contracts.LaunchArgumentCodec.Build(game.GameId),
            selected.RelativePath,
            selected.CandidateType,
            selected.CandidateRole,
            ToLaunchMode(selected, game.LaunchModeOverride),
            Path.GetDirectoryName(selected.RelativePath) ?? "",
            warnings);
    }

    public static GameLaunchModeOverride ToLaunchMode(GameLaunchCandidateRecord candidate, GameLaunchModeOverride fallback)
    {
        return candidate.CandidateRole switch
        {
            LaunchCandidateRole.MToolTemplate or LaunchCandidateRole.MToolTarget => GameLaunchModeOverride.MTool,
            LaunchCandidateRole.TranslationGame => GameLaunchModeOverride.TranslationPreferred,
            LaunchCandidateRole.CustomLaunchScript => GameLaunchModeOverride.CustomScript,
            LaunchCandidateRole.MainGame => GameLaunchModeOverride.Direct,
            _ => fallback == GameLaunchModeOverride.Inherit ? GameLaunchModeOverride.Direct : fallback
        };
    }

    public static string? ResolveAbsolutePath(GameRecord game, GameLaunchCandidateRecord candidate)
    {
        if (string.IsNullOrWhiteSpace(game.SourceRoot) || string.IsNullOrWhiteSpace(candidate.RelativePath)) return null;
        var root = NormalizeRoot(game.SourceRoot);
        var path = Path.GetFullPath(Path.Combine(root, candidate.RelativePath));
        return IsPathUnder(path, root) ? path : null;
    }

    private static IEnumerable<string> EnumerateCandidateFiles(string root, int maxDepth)
    {
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Dequeue();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly).ToList();
            }
            catch (Exception) when (!string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in files)
            {
                if (CandidateExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) yield return file;
            }

            if (depth >= maxDepth) continue;
            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).ToList();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var child in directories)
            {
                if (SkipDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase)) continue;
                pending.Enqueue((child, depth + 1));
            }
        }
    }

    private static GameLaunchCandidateRecord BuildCandidate(string gameId, string root, string path, LaunchCandidateSource source, HashSet<string> mtoolTargets)
    {
        var relative = Path.GetRelativePath(root, path).Replace('/', '\\');
        var file = new FileInfo(path);
        var fileName = file.Name;
        var normalizedName = NormalizeCandidateText(Path.GetFileNameWithoutExtension(fileName));
        var extension = file.Extension.ToLowerInvariant();
        var reasons = new List<string>();
        var exclusions = new List<string>();
        var score = 0;
        var confidence = 30;
        var type = extension switch
        {
            ".exe" => LaunchCandidateType.Executable,
            ".bat" => LaunchCandidateType.Batch,
            ".cmd" => LaunchCandidateType.CommandScript,
            ".lnk" => LaunchCandidateType.Shortcut,
            _ => LaunchCandidateType.Unknown
        };
        var role = LaunchCandidateRole.Unknown;
        var candidateSource = source;
        var testStatus = LaunchCandidateTestStatus.Untested;

        if (MToolBatchNames.Contains(fileName, StringComparer.OrdinalIgnoreCase) || IsMToolTemplate(path))
        {
            role = LaunchCandidateRole.MToolTemplate;
            candidateSource = LaunchCandidateSource.MToolAdapter;
            score = 130;
            confidence = 90;
            reasons.Add("识别为 MTool 与工具一同启动模板。 ");
        }
        else if (mtoolTargets.Contains(fileName))
        {
            role = LaunchCandidateRole.MToolTarget;
            candidateSource = LaunchCandidateSource.MToolAdapter;
            score = 115;
            confidence = 85;
            reasons.Add("识别为 MTool 模板中的目标游戏程序。 ");
        }
        else if (type == LaunchCandidateType.Executable && IsTranslationExecutable(fileName))
        {
            role = LaunchCandidateRole.TranslationGame;
            score = 110;
            confidence = 82;
            reasons.Add("命中翻译版启动关键字。 ");
        }
        else if (type == LaunchCandidateType.Executable && (normalizedName is "game" or "rpg_rt" or "nw" || normalizedName.Contains("game")))
        {
            role = LaunchCandidateRole.MainGame;
            score = 85;
            confidence = 68;
            reasons.Add("命中常见游戏主程序命名。 ");
        }
        else if (type == LaunchCandidateType.Executable)
        {
            role = LaunchCandidateRole.MainGame;
            score = 55;
            confidence = 45;
            reasons.Add("可执行文件，需人工确认是否为主程序。 ");
        }
        else if (type == LaunchCandidateType.Shortcut)
        {
            role = LaunchCandidateRole.Launcher;
            score = 20;
            confidence = 30;
            reasons.Add("快捷方式候选，仅做参考。 ");
        }
        else if (type is LaunchCandidateType.Batch or LaunchCandidateType.CommandScript)
        {
            role = LaunchCandidateRole.CustomLaunchScript;
            score = 10;
            confidence = 20;
            exclusions.Add("未知脚本默认不自动接管，需人工确认。 ");
            testStatus = LaunchCandidateTestStatus.HardBlocked;
        }

        var lowerName = fileName.ToLowerInvariant();
        foreach (var danger in DangerousKeywords)
        {
            if (!lowerName.Contains(danger, StringComparison.OrdinalIgnoreCase)) continue;
            role = danger switch
            {
                "config" => LaunchCandidateRole.ConfigurationTool,
                "update" or "patch" => LaunchCandidateRole.Updater,
                "uninstall" or "unins" => LaunchCandidateRole.Uninstaller,
                "crash" => LaunchCandidateRole.CrashReporter,
                "redist" or "vcredist" or "dxwebsetup" => LaunchCandidateRole.Runtime,
                _ => LaunchCandidateRole.Helper
            };
            score -= 100;
            confidence = Math.Max(confidence, 75);
            exclusions.Add($"命中非游戏启动关键字：{danger}。 ");
            testStatus = LaunchCandidateTestStatus.HardBlocked;
            break;
        }

        if (score <= 0 && exclusions.Count == 0)
        {
            exclusions.Add("候选置信度过低，默认不自动接管。 ");
        }

        return new GameLaunchCandidateRecord
        {
            CandidateId = BuildCandidateId(gameId, relative, type),
            GameId = gameId,
            RelativePath = relative,
            CandidateType = type,
            CandidateRole = role,
            CandidateSource = candidateSource,
            Score = Math.Clamp(score, -100, 200),
            Confidence = Math.Clamp(confidence, 0, 100),
            IsPrimary = false,
            IsIgnored = score <= 0,
            UserConfirmed = false,
            FileSize = file.Exists ? file.Length : 0,
            LastWriteUtc = file.Exists ? file.LastWriteTimeUtc : null,
            MetadataFingerprint = file.Exists ? $"{relative}|{file.Length}|{file.LastWriteTimeUtc.Ticks}" : relative,
            LastTestStatus = testStatus,
            MetadataJson = JsonSerializer.Serialize(new { extension, directory = Path.GetDirectoryName(relative) ?? "" }),
            ReasonsJson = JsonSerializer.Serialize(reasons),
            ExclusionReasonsJson = JsonSerializer.Serialize(exclusions)
        };
    }

    private static bool IsTranslationExecutable(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        if (TranslationKeywords.Any(keyword => lower.Contains(keyword.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))) return true;
        var normalized = NormalizeCandidateText(fileName);
        return Regex.IsMatch(normalized, "(^|[^a-z])ai([^a-z]|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    private static bool IsMToolTemplate(string path)
    {
        if (!path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            return MToolBatchNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) || MToolInjectCommand.IsMatch(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<string> ReadMToolTargetNames(string path)
    {
        string content;
        try
        {
            content = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (Match match in MToolInjectCommand.Matches(content))
        {
            var value = match.Groups["game"].Value;
            if (!string.IsNullOrWhiteSpace(value)) yield return Path.GetFileName(value);
        }
    }

    private static string BuildCandidateId(string gameId, string relativePath, LaunchCandidateType type)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{gameId}|{relativePath}|{type}"));
        return new Guid(bytes[..16]).ToString();
    }

    private static string NormalizeCandidateText(string value)
    {
        return value.Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant()
            .Replace(" ", "")
            .Replace("_", "")
            .Replace("-", "")
            .Replace(".", "");
    }

    private static string? TryRelativePath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path);
            if (!IsPathUnder(full, root)) return null;
            return Path.GetRelativePath(root, full).Replace('/', '\\');
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string NormalizeRoot(string value) => string.IsNullOrWhiteSpace(value)
        ? ""
        : Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsPathUnder(string value, string root)
    {
        var normalizedValue = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = NormalizeRoot(root);
        return normalizedValue.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedValue.StartsWith($"{normalizedRoot}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }
}


