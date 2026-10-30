using System.IO.Compression;
using VniteGameCache.ControlPanel;

namespace VniteGameCache.ControlPanel.Tests;

public sealed class CopyDiagnosticArchiveTests
{
    [Fact]
    public void Export_includes_only_selected_games_three_latest_logs_and_progress()
    {
        var root = Directory.CreateTempSubdirectory("vgc-copy-diagnostic-").FullName;
        try
        {
            var logRoot = Path.Combine(root, "logs");
            var robocopyRoot = Path.Combine(logRoot, "robocopy");
            Directory.CreateDirectory(robocopyRoot);
            var gameId = "11111111-1111-1111-1111-111111111111";
            for (var index = 1; index <= 4; index++)
            {
                var path = Path.Combine(robocopyRoot, $"{gameId}-{index}.robocopy.log");
                File.WriteAllText(path, $"selected-{index}");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(index - 10));
                if (index == 4) File.WriteAllText(path + ".progress.json", "{}");
            }
            File.WriteAllText(Path.Combine(robocopyRoot, "22222222-2222-2222-2222-222222222222-5.robocopy.log"), "other game");

            var output = Path.Combine(root, "diagnosis.zip");
            CopyDiagnosticArchive.Export(output, logRoot, new CopyDiagnosticSnapshot(
                "测试游戏", gameId, "vnite-id", root, root, "Game.exe", "失败", "未启动", "复制失败"));

            using var archive = ZipFile.OpenRead(output);
            var names = archive.Entries.Select(entry => entry.FullName).ToArray();
            Assert.Contains("diagnosis.txt", names);
            Assert.Contains($"robocopy/{gameId}-4.robocopy.log", names);
            Assert.Contains($"robocopy/{gameId}-4.robocopy.log.progress.json", names);
            Assert.DoesNotContain($"robocopy/{gameId}-1.robocopy.log", names);
            Assert.DoesNotContain(names, name => name.Contains("22222222", StringComparison.Ordinal));
            Assert.Equal(3, names.Count(name => name.EndsWith(".robocopy.log", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
