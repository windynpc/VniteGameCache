using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VniteGameCache.Contracts;

namespace VniteGameCache.Wrapper;

public sealed record LaunchProcessSpec(
    string FileName,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    bool CreateNoWindow = false);

public sealed record ResolvedLaunchPlan(
    VgcLaunchMode Mode,
    LaunchProcessSpec PrimaryProcess,
    IReadOnlyList<LaunchProcessSpec> SecondaryProcesses,
    string TrackedExecutablePath,
    bool UseRequestArguments,
    bool UseOriginalScript,
    bool RestrictDescendantsToExpectedRoot,
    IReadOnlyList<string> AdditionalTrackingRoots,
    string Message);

public sealed record MToolBatchTemplate(
    string BatchPath,
    string GameExecutableName,
    string LoaderRelativePath);

public sealed class LaunchPreferenceException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public interface IMToolJunctionManager
{
    string EnsureJunction(string gameId, string targetDirectory);
}

public sealed class LaunchPreferenceResolver(IMToolJunctionManager? junctionManager = null)
{
    private static readonly string[] PreferredBatchNames = ["与工具一同启动.bat", "StartWithTool.bat"];
    private static readonly string[] AiModelMarkers = ["claude", "gpt", "gemini", "deepseek", "qwen", "opus", "sonnet"];
    private static readonly string[] ExcludedExecutableMarkers =
    [
        "setup", "install", "uninstall", "unins", "update", "updater", "config", "crash", "helper", "notification", "vcredist", "dxsetup"
    ];
    private static readonly Regex HumanTranslationMarker = new(
        @"(?:^|[_\-.\s()\[\]])(?:cn|chs|sc|zh[_\-]?cn)(?=$|[_\-.\s()\[\]])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AiTranslationMarker = new(
        @"(?:^|[_\-.\s()\[\]])ai(?=$|[_\-.\s()\[\]])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MToolInjectCommand = new(
        "(?im)^\\s*\"(?<inject>[^\"\\r\\n]*loaders[\\\\/]+inject\\.exe)\"\\s+\"(?<game>[^\"\\r\\n]+\\.exe)\"\\s+\"(?<loader>[^\"\\r\\n]+\\.dll)\"\\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IMToolJunctionManager _junctionManager = junctionManager ?? new WindowsMToolJunctionManager();

    public ResolvedLaunchPlan Resolve(
        string gameId,
        string cacheExecutablePath,
        string cacheWorkingDirectory,
        VgcSettings settings)
    {
        var executablePath = Path.GetFullPath(cacheExecutablePath);
        var workingDirectory = Path.GetFullPath(cacheWorkingDirectory);
        return VgcSettingsStore.ParseLaunchMode(settings.DefaultLaunchMode) switch
        {
            VgcLaunchMode.TranslationPreferred => ResolveTranslation(executablePath, workingDirectory),
            VgcLaunchMode.MTool => ResolveMTool(gameId, executablePath, workingDirectory, settings.MToolExecutablePath),
            _ => Direct(executablePath, workingDirectory, "直接启动：使用数据库记录的缓存可执行文件。")
        };
    }

    public string SelectPreferredTranslationExecutable(string cacheExecutablePath)
    {
        var executablePath = Path.GetFullPath(cacheExecutablePath);
        var directory = Path.GetDirectoryName(executablePath)
                        ?? throw new LaunchPreferenceException("translation_directory_missing", $"无法确定可执行文件目录：{executablePath}");
        if (!Directory.Exists(directory))
        {
            return executablePath;
        }

        var currentStem = Path.GetFileNameWithoutExtension(executablePath);
        var candidates = Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly)
            .Select(path => new
            {
                Path = Path.GetFullPath(path),
                Stem = Path.GetFileNameWithoutExtension(path)
            })
            .Where(candidate => !IsExcludedExecutable(candidate.Stem))
            .Select(candidate => new
            {
                candidate.Path,
                candidate.Stem,
                Human = HasHumanTranslationMarker(candidate.Stem),
                Ai = HasAiTranslationMarker(candidate.Stem)
            })
            .Where(candidate => (candidate.Human || candidate.Ai) && IsRelatedExecutable(currentStem, candidate.Stem))
            .Select(candidate => new
            {
                candidate.Path,
                Score = (candidate.Human ? 300 : 200)
                        + (PathsEqual(candidate.Path, executablePath) ? 40 : 0)
                        + (NormalizeStem(RemoveTranslationMarkers(candidate.Stem)) == NormalizeStem(RemoveTranslationMarkers(currentStem)) ? 80 : 0),
                NameLength = Path.GetFileName(candidate.Path).Length
            })
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.NameLength)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return candidates?.Path ?? executablePath;
    }

    public MToolBatchTemplate? FindMToolTemplate(string cacheWorkingDirectory)
    {
        var workingDirectory = Path.GetFullPath(cacheWorkingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            return null;
        }

        var batchFiles = Directory.EnumerateFiles(workingDirectory, "*.bat", SearchOption.TopDirectoryOnly)
            .OrderBy(path => BatchPriority(Path.GetFileName(path)))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var batchPath in batchFiles)
        {
            var text = File.ReadAllText(batchPath);
            var preferredName = PreferredBatchNames.Contains(Path.GetFileName(batchPath), StringComparer.OrdinalIgnoreCase);
            var recognized = text.Contains("automatically generated by the MTool", StringComparison.OrdinalIgnoreCase)
                             || (preferredName && text.Contains("loaders\\inject.exe", StringComparison.OrdinalIgnoreCase));
            if (!recognized)
            {
                continue;
            }

            var match = MToolInjectCommand.Match(text);
            if (!match.Success)
            {
                throw new LaunchPreferenceException(
                    "mtool_batch_invalid",
                    $"已识别 MTool 批处理，但无法解析 inject.exe 启动命令：{batchPath}");
            }

            var gameExecutableName = Path.GetFileName(match.Groups["game"].Value);
            var loaderRelativePath = ExtractLoaderRelativePath(match.Groups["loader"].Value);
            if (string.IsNullOrWhiteSpace(gameExecutableName) || string.IsNullOrWhiteSpace(loaderRelativePath))
            {
                throw new LaunchPreferenceException("mtool_batch_invalid", $"MTool 批处理缺少游戏可执行文件或 Loader DLL：{batchPath}");
            }

            return new MToolBatchTemplate(Path.GetFullPath(batchPath), gameExecutableName, loaderRelativePath);
        }

        return null;
    }

    private ResolvedLaunchPlan ResolveTranslation(string executablePath, string workingDirectory, string? prefix = null)
    {
        var translatedPath = SelectPreferredTranslationExecutable(executablePath);
        if (PathsEqual(translatedPath, executablePath))
        {
            return Direct(executablePath, workingDirectory, $"{prefix}未找到与当前主程序配对的翻译 EXE，使用原缓存可执行文件。".Trim());
        }

        return new ResolvedLaunchPlan(
            VgcLaunchMode.TranslationPreferred,
            new LaunchProcessSpec(translatedPath, Path.GetDirectoryName(translatedPath) ?? workingDirectory, Array.Empty<string>()),
            Array.Empty<LaunchProcessSpec>(),
            translatedPath,
            UseRequestArguments: true,
            UseOriginalScript: false,
            RestrictDescendantsToExpectedRoot: false,
            AdditionalTrackingRoots: Array.Empty<string>(),
            $"{prefix}翻译 EXE 优先：{Path.GetFileName(translatedPath)}".Trim());
    }

    private ResolvedLaunchPlan ResolveMTool(
        string gameId,
        string executablePath,
        string workingDirectory,
        string? configuredMToolExecutablePath)
    {
        var template = FindMToolTemplate(workingDirectory);
        if (template is null)
        {
            return ResolveTranslation(executablePath, workingDirectory, "未发现已识别的 MTool 批处理；");
        }

        if (string.IsNullOrWhiteSpace(configuredMToolExecutablePath))
        {
            throw new LaunchPreferenceException("mtool_not_configured", $"检测到 {Path.GetFileName(template.BatchPath)}，但尚未配置 MTool.exe 路径。");
        }

        var mtoolExecutablePath = Path.GetFullPath(configuredMToolExecutablePath);
        if (!File.Exists(mtoolExecutablePath))
        {
            throw new LaunchPreferenceException("mtool_executable_missing", $"配置的 MTool.exe 不存在：{mtoolExecutablePath}");
        }

        var mtoolRoot = Path.GetDirectoryName(mtoolExecutablePath)
                        ?? throw new LaunchPreferenceException("mtool_root_invalid", $"无法确定 MTool 安装目录：{mtoolExecutablePath}");
        var injectPath = EnsureFileUnderRoot(mtoolRoot, Path.Combine("loaders", "inject.exe"), "MTool inject.exe");
        var loaderPath = EnsureFileUnderRoot(mtoolRoot, template.LoaderRelativePath, "MTool Loader DLL");
        var nwPath = EnsureFileUnderRoot(mtoolRoot, "nw.exe", "MTool nw.exe");
        var gameExecutablePath = LocateUniqueGameExecutable(workingDirectory, template.GameExecutableName);
        var relativeGamePath = Path.GetRelativePath(workingDirectory, gameExecutablePath);
        if (relativeGamePath.StartsWith("..", StringComparison.Ordinal))
        {
            throw new LaunchPreferenceException("mtool_game_path_unsafe", $"MTool 游戏可执行文件不在缓存工作目录内：{gameExecutablePath}");
        }

        var junctionPath = _junctionManager.EnsureJunction(gameId, workingDirectory);
        var junctionGamePath = Path.Combine(junctionPath, relativeGamePath);
        var primary = new LaunchProcessSpec(
            injectPath,
            junctionPath,
            [junctionGamePath, loaderPath],
            CreateNoWindow: true);
        var secondary = new LaunchProcessSpec(
            nwPath,
            mtoolRoot,
            [mtoolRoot]);

        return new ResolvedLaunchPlan(
            VgcLaunchMode.MTool,
            primary,
            [secondary],
            gameExecutablePath,
            UseRequestArguments: false,
            UseOriginalScript: false,
            RestrictDescendantsToExpectedRoot: true,
            AdditionalTrackingRoots: [junctionPath],
            $"MTool 批处理优先：已读取 {Path.GetFileName(template.BatchPath)}，并改用当前 MTool 组件启动缓存副本。 ");
    }

    private static ResolvedLaunchPlan Direct(string executablePath, string workingDirectory, string message)
    {
        return new ResolvedLaunchPlan(
            VgcLaunchMode.Direct,
            new LaunchProcessSpec(executablePath, workingDirectory, Array.Empty<string>()),
            Array.Empty<LaunchProcessSpec>(),
            executablePath,
            UseRequestArguments: true,
            UseOriginalScript: true,
            RestrictDescendantsToExpectedRoot: false,
            AdditionalTrackingRoots: Array.Empty<string>(),
            message);
    }

    private static string LocateUniqueGameExecutable(string workingDirectory, string executableName)
    {
        var directPath = Path.Combine(workingDirectory, executableName);
        if (File.Exists(directPath))
        {
            return Path.GetFullPath(directPath);
        }

        var matches = Directory.EnumerateFiles(workingDirectory, executableName, SearchOption.AllDirectories)
            .Take(2)
            .Select(Path.GetFullPath)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new LaunchPreferenceException("mtool_game_executable_missing", $"缓存中找不到 MTool 批处理指定的游戏程序：{executableName}"),
            _ => throw new LaunchPreferenceException("mtool_game_executable_ambiguous", $"缓存中存在多个同名 MTool 游戏程序，需要人工确认：{executableName}")
        };
    }

    private static string EnsureFileUnderRoot(string root, string relativePath, string displayName)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
        {
            throw new LaunchPreferenceException("mtool_component_missing", $"{displayName} 不存在或不在当前 MTool 目录内：{candidate}");
        }

        return candidate;
    }

    private static string ExtractLoaderRelativePath(string loaderPath)
    {
        var normalized = loaderPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var segment = $"{Path.DirectorySeparatorChar}loaders{Path.DirectorySeparatorChar}";
        var index = normalized.LastIndexOf(segment, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            return normalized[(index + 1)..];
        }

        return normalized.StartsWith($"loaders{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : string.Empty;
    }

    private static int BatchPriority(string fileName)
    {
        var index = Array.FindIndex(PreferredBatchNames, name => name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }

    private static bool HasHumanTranslationMarker(string stem)
    {
        return stem.Contains("中文", StringComparison.OrdinalIgnoreCase)
               || stem.Contains("简中", StringComparison.OrdinalIgnoreCase)
               || stem.Contains("简体", StringComparison.OrdinalIgnoreCase)
               || stem.Contains("汉化", StringComparison.OrdinalIgnoreCase)
               || HumanTranslationMarker.IsMatch(stem)
               || Regex.IsMatch(stem, "(?:CN|CHS|SC)$", RegexOptions.CultureInvariant);
    }

    private static bool HasAiTranslationMarker(string stem)
    {
        return AiTranslationMarker.IsMatch(stem)
               || Regex.IsMatch(stem, "AI$", RegexOptions.CultureInvariant)
               || AiModelMarkers.Any(marker => stem.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRelatedExecutable(string currentStem, string candidateStem)
    {
        if (currentStem.Equals(candidateStem, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var current = NormalizeStem(RemoveTranslationMarkers(currentStem));
        var candidate = NormalizeStem(RemoveTranslationMarkers(candidateStem));
        if (current.Length < 3 || candidate.Length < 3)
        {
            return false;
        }

        return current.Equals(candidate, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(current, StringComparison.OrdinalIgnoreCase)
               || current.StartsWith(candidate, StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveTranslationMarkers(string stem)
    {
        var value = HumanTranslationMarker.Replace(stem, " ");
        value = AiTranslationMarker.Replace(value, " ");
        foreach (var marker in AiModelMarkers)
        {
            value = Regex.Replace(value, Regex.Escape(marker), " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        value = Regex.Replace(value, "(?:CN|CHS|SC|AI)$", "", RegexOptions.CultureInvariant);
        return value;
    }

    private static string NormalizeStem(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static bool IsExcludedExecutable(string stem)
    {
        return ExcludedExecutableMarkers.Any(marker => stem.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool PathsEqual(string left, string right)
    {
        return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class WindowsMToolJunctionManager(string? junctionRoot = null) : IMToolJunctionManager
{
    private readonly string _junctionRoot = junctionRoot ?? ResolveDefaultRoot();

    public string EnsureJunction(string gameId, string targetDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new LaunchPreferenceException("mtool_windows_required", "MTool 启动模式仅支持 Windows。");
        }

        var target = Path.GetFullPath(targetDirectory);
        if (!Directory.Exists(target))
        {
            throw new LaunchPreferenceException("mtool_cache_directory_missing", $"MTool 缓存工作目录不存在：{target}");
        }

        var root = Path.GetFullPath(_junctionRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Directory.CreateDirectory(root);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(gameId))).ToLowerInvariant()[..16];
        var junctionPath = Path.Combine(root, hash);
        if (!junctionPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new LaunchPreferenceException("mtool_junction_path_unsafe", $"MTool Junction 路径越界：{junctionPath}");
        }

        RemoveExistingJunction(junctionPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = $"/d /c mklink /J \"{junctionPath}\" \"{target}\""
        };
        using var process = Process.Start(startInfo)
                            ?? throw new LaunchPreferenceException("mtool_junction_failed", "无法启动 mklink 创建 MTool Junction。");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(junctionPath))
        {
            throw new LaunchPreferenceException(
                "mtool_junction_failed",
                $"创建 MTool Junction 失败（ExitCode={process.ExitCode}）：{string.Join(" ", output, error).Trim()}");
        }

        return junctionPath;
    }

    private static void RemoveExistingJunction(string junctionPath)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(junctionPath);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        if (!attributes.HasFlag(FileAttributes.Directory) || !attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new LaunchPreferenceException("mtool_junction_occupied", $"MTool Junction 位置已被普通文件或目录占用：{junctionPath}");
        }

        Directory.Delete(junctionPath, recursive: false);
    }

    private static string ResolveDefaultRoot()
    {
        var localRoot = Path.Combine(VgcSettingsStore.LocalAppDataRoot, "mtool-links");
        if (localRoot.All(character => character <= 127))
        {
            return localRoot;
        }

        var publicRoot = Environment.GetEnvironmentVariable("PUBLIC");
        if (!string.IsNullOrWhiteSpace(publicRoot))
        {
            var fallback = Path.Combine(publicRoot, "VniteGameCache", "mtool-links");
            if (fallback.All(character => character <= 127))
            {
                return fallback;
            }
        }

        throw new LaunchPreferenceException("mtool_ascii_path_unavailable", "无法创建仅含 ASCII 字符的 MTool Junction 根目录。");
    }
}
