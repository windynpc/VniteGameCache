using System.IO.Compression;
using System.Text;

namespace VniteGameCache.ControlPanel;

internal sealed record CopyDiagnosticSnapshot(
    string Name,
    string GameId,
    string VniteId,
    string SourceRoot,
    string CacheRoot,
    string ExecutablePath,
    string CacheStatus,
    string LaunchStatus,
    string Issue);

internal static class CopyDiagnosticArchive
{
    internal static void Export(string outputPath, string logRoot, CopyDiagnosticSnapshot game)
    {
        var logDirectory = Path.Combine(logRoot, "robocopy");
        var logs = Directory.Exists(logDirectory)
            ? Directory.EnumerateFiles(logDirectory, "*.robocopy.log", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path).StartsWith($"{game.GameId}-", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(3).ToList()
            : [];

        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var report = archive.CreateEntry("diagnosis.txt");
        using (var writer = new StreamWriter(report.Open(), new UTF8Encoding(false)))
        {
            writer.WriteLine($"导出时间 UTC: {DateTimeOffset.UtcNow:O}");
            writer.WriteLine($"游戏: {game.Name}");
            writer.WriteLine($"Game ID: {game.GameId}");
            writer.WriteLine($"Vnite ID: {game.VniteId}");
            writer.WriteLine($"源目录: {game.SourceRoot}");
            writer.WriteLine($"源目录存在: {Directory.Exists(game.SourceRoot)}");
            writer.WriteLine($"缓存目录: {game.CacheRoot}");
            writer.WriteLine($"缓存目录存在: {Directory.Exists(game.CacheRoot)}");
            writer.WriteLine($"启动程序: {game.ExecutablePath}");
            writer.WriteLine($"缓存状态: {game.CacheStatus}");
            writer.WriteLine($"启动状态: {game.LaunchStatus}");
            writer.WriteLine($"问题: {game.Issue}");
            writer.WriteLine($"匹配的最近复制日志: {logs.Count} 个（最多 3 个）");
            writer.WriteLine("诊断只记录当前状态；最近处理文件不一定是失败文件。");
            writer.WriteLine("压缩包包含本机路径和原始日志；分享前请检查并删除隐私信息。");
        }

        foreach (var log in logs)
        {
            archive.CreateEntryFromFile(log, $"robocopy/{Path.GetFileName(log)}");
            var progress = log + ".progress.json";
            if (File.Exists(progress)) archive.CreateEntryFromFile(progress, $"robocopy/{Path.GetFileName(progress)}");
        }
    }
}
