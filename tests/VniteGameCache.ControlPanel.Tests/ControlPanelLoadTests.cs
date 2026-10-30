using System.Collections;
using System.Reflection;
using VniteGameCache.Persistence;

namespace VniteGameCache.ControlPanel.Tests;

public sealed class ControlPanelLoadTests
{
    [Fact]
    public void Game_rows_load_with_parallel_path_checks()
    {
        var root = Path.Combine(Path.GetTempPath(), "vgc-control-panel-load-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "Game.exe");
            File.WriteAllText(executable, "fixture");
            var game = new GameRecord
            {
                GameId = "control-panel-load-test",
                DisplayName = "Load test",
                SourceRoot = root,
                CacheRoot = Path.Combine(root, "cache"),
                ExecutablePath = executable,
                WorkingDirectory = root
            };
            var form = typeof(UiFeatureRegistry).Assembly.GetType("VniteGameCache.ControlPanel.MainForm")!;
            var buildRows = form.GetMethod("BuildRows", BindingFlags.NonPublic | BindingFlags.Static)!;

            var rows = (IEnumerable)buildRows.Invoke(null,
                [new[] { game }, new Dictionary<string, ArchiveSourceRecord>(), null, null,
                    new Dictionary<string, string>(), new Dictionary<string, string>()])!;

            Assert.Single(rows.Cast<object>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
