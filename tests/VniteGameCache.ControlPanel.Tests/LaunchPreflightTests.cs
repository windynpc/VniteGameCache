using System.Reflection;
using VniteGameCache.Contracts;
using VniteGameCache.Persistence;

namespace VniteGameCache.ControlPanel.Tests;

public sealed class LaunchPreflightTests
{
    [Fact]
    public void Ready_mtool_game_selects_cached_exe_without_creating_junction()
    {
        var root = Path.Combine(Path.GetTempPath(), "vgc-launch-preflight-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(cache);
        try
        {
            File.WriteAllText(Path.Combine(cache, "Game.exe"), "fixture");
            File.WriteAllText(Path.Combine(cache, "MTool_Game.exe"), "fixture");
            var game = CreateGame(source, cache, CacheStatus.Ready);
            var result = Describe(game, new VgcSettings { DefaultLaunchMode = "MTool" });

            Assert.Contains(Path.Combine(cache, "MTool_Game.exe"), result);
            Assert.Contains("翻译 EXE 回退：游戏自带 MTool_Game.exe", result);
            Assert.Equal(2, Directory.GetFiles(cache).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Uncached_game_does_not_claim_a_final_target()
    {
        var game = CreateGame(@"G:\Games\Sample", @"E:\Cache\Sample", CacheStatus.NotCached);
        var result = Describe(game, new VgcSettings { DefaultLaunchMode = "MTool" });
        Assert.Contains("待缓存就绪后确认", result);
        Assert.DoesNotContain("启动程序：", result);
    }

    private static GameRecord CreateGame(string source, string cache, CacheStatus status) => new()
    {
        GameId = "preflight-test",
        DisplayName = "Sample",
        SourceRoot = source,
        CacheRoot = cache,
        ExecutablePath = Path.Combine(source, "Game.exe"),
        ExecutableRelativePath = "Game.exe",
        WorkingDirectory = source,
        WorkingDirectoryRelativePath = ".",
        CacheStatus = status
    };

    private static string Describe(GameRecord game, VgcSettings settings)
    {
        var service = typeof(UiFeatureRegistry).Assembly.GetType("VniteGameCache.ControlPanel.LaunchPreflight")!;
        var method = service.GetMethod("Describe", BindingFlags.Static | BindingFlags.Public)!;
        return (string)method.Invoke(null, [game, settings])!;
    }
}
