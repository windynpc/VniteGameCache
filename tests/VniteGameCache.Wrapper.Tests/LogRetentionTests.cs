using VniteGameCache.Contracts;

namespace VniteGameCache.Wrapper.Tests;

public sealed class LogRetentionTests
{
    [Fact]
    public void Shared_budget_removes_oldest_logs_and_preserves_other_files_and_links()
    {
        var root = Path.Combine(Path.GetTempPath(), "vgc-logs", Guid.NewGuid().ToString("N"));
        var plugin = Path.Combine(root, "plugin");
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(plugin);
        Directory.CreateDirectory(logs);
        try
        {
            Assert.Equal(500L * 1024 * 1024, LogRetention.MaximumTotalBytes);
            var oldest = Path.Combine(plugin, "vnite-game-cache.jsonl");
            var copy = Path.Combine(logs, "game.robocopy.log");
            var latest = Path.Combine(logs, "20261021.jsonl");
            File.WriteAllText(oldest, new string('a', 60));
            File.WriteAllText(copy, new string('b', 60));
            File.WriteAllText(latest, new string('c', 20));
            File.SetLastWriteTimeUtc(oldest, DateTime.UtcNow.AddDays(-2));
            File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddDays(-1));
            var unrelated = Path.Combine(logs, "settings.json");
            File.WriteAllText(unrelated, "keep");
            // Directory symlinks require extra privileges on Windows; exercise this guard on Unix.
            if (!OperatingSystem.IsWindows())
            {
                var external = Path.Combine(root, "external");
                Directory.CreateDirectory(external);
                File.WriteAllText(Path.Combine(external, "20200101.jsonl"), "keep");
                Directory.CreateSymbolicLink(Path.Combine(logs, "linked"), external);
            }
            LogRetention.Trim([plugin, logs, logs], 100);
            Assert.False(File.Exists(oldest));
            Assert.True(File.Exists(copy));
            Assert.Equal(new string('c', 20), File.ReadAllText(latest));
            Assert.Equal("keep", File.ReadAllText(unrelated));
            if (!OperatingSystem.IsWindows())
                Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "external", "20200101.jsonl")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Rotation_keeps_new_event_and_oversized_entries_cannot_bypass_budget()
    {
        var root = Path.Combine(Path.GetTempPath(), "vgc-logs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var current = Path.Combine(root, "20261021.jsonl");
            File.WriteAllText(current, new string('x', 90));
            LogRetention.Append(current, "new event\n", [root], 95, 95);
            Assert.Equal("new event\n", File.ReadAllText(current));
            Assert.Single(Directory.GetFiles(root));
            LogRetention.Append(current, new string('x', 101), [root], 100, 95);
            Assert.True(Directory.GetFiles(root).Sum(path => new FileInfo(path).Length) <= 100);
            var blocked = Path.Combine(root, "blocked");
            File.WriteAllText(blocked, "file");
            LogRetention.Append(Path.Combine(blocked, "20261021.jsonl"), "event", [root]);
        }
        finally { Directory.Delete(root, true); }
    }
}
