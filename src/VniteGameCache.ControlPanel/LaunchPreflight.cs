using VniteGameCache.Contracts;
using VniteGameCache.Persistence;
using VniteGameCache.Wrapper;

namespace VniteGameCache.ControlPanel;

internal static class LaunchPreflight
{
    public static string Describe(GameRecord game, VgcSettings settings)
    {
        if (game.CacheStatus != CacheStatus.Ready)
            return $"缓存状态：{game.CacheStatus}\r\n结果：待缓存就绪后确认实际启动程序。";

        try
        {
            var cacheRoot = Path.GetFullPath(game.CacheRoot);
            var relativeExecutable = !string.IsNullOrWhiteSpace(game.ExecutableRelativePath)
                ? game.ExecutableRelativePath
                : Path.IsPathRooted(game.ExecutablePath)
                    ? Path.GetRelativePath(game.SourceRoot, game.ExecutablePath)
                    : game.ExecutablePath;
            var cacheExecutable = MapCachePath(cacheRoot, relativeExecutable);
            if (!File.Exists(cacheExecutable))
                return $"缓存程序缺失：{cacheExecutable}\r\n结果：待缓存修复后确认。";

            var relativeWorkingDirectory = !string.IsNullOrWhiteSpace(game.WorkingDirectoryRelativePath)
                ? game.WorkingDirectoryRelativePath
                : !string.IsNullOrWhiteSpace(game.WorkingDirectory) &&
                  Path.GetFullPath(game.WorkingDirectory).StartsWith(
                      Path.GetFullPath(game.SourceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                      StringComparison.OrdinalIgnoreCase)
                    ? Path.GetRelativePath(game.SourceRoot, game.WorkingDirectory)
                    : ".";
            var cacheWorkingDirectory = MapCachePath(cacheRoot, relativeWorkingDirectory);
            if (!Directory.Exists(cacheWorkingDirectory))
                return $"缓存工作目录缺失：{cacheWorkingDirectory}\r\n结果：待缓存修复后确认。";

            var effectiveSettings = game.LaunchModeOverride switch
            {
                GameLaunchModeOverride.Direct => settings with { DefaultLaunchMode = nameof(VgcLaunchMode.Direct) },
                GameLaunchModeOverride.TranslationPreferred => settings with { DefaultLaunchMode = nameof(VgcLaunchMode.TranslationPreferred) },
                GameLaunchModeOverride.MTool => settings with { DefaultLaunchMode = nameof(VgcLaunchMode.MTool) },
                _ => settings
            };
            var junction = new PreviewJunctionManager();
            var plan = new LaunchPreferenceResolver(junction).Resolve(game.GameId, cacheExecutable, cacheWorkingDirectory, effectiveSettings);
            return string.Join("\r\n", new[]
            {
                $"有效模式：{VgcSettingsStore.ParseLaunchMode(effectiveSettings.DefaultLaunchMode)}",
                $"启动程序：{plan.PrimaryProcess.FileName}",
                $"游戏进程：{plan.TrackedExecutablePath}",
                $"选择依据：{plan.Message}",
                junction.WasRequested ? "运行时仍需创建临时 Junction；预检没有创建。" : "预检没有启动程序或修改文件。"
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or LaunchPreferenceException)
        {
            return $"预检未通过：{error.Message}\r\n结果：未启动游戏，也未修改文件。";
        }
    }

    private static string MapCachePath(string cacheRoot, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new ArgumentException("启动路径不是游戏根目录的相对路径。");
        var mapped = Path.GetFullPath(Path.Combine(cacheRoot, relative));
        if (!string.Equals(mapped, cacheRoot, StringComparison.OrdinalIgnoreCase) &&
            !mapped.StartsWith(cacheRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("启动路径越过缓存目录。");
        return mapped;
    }

    private sealed class PreviewJunctionManager : IMToolJunctionManager
    {
        public bool WasRequested { get; private set; }

        public string EnsureJunction(string gameId, string targetDirectory)
        {
            WasRequested = true;
            return targetDirectory;
        }
    }
}
