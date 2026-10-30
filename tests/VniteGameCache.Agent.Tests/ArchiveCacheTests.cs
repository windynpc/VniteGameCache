using System.IO.Compression;
using Microsoft.Data.Sqlite;
using VniteGameCache.Agent;
using VniteGameCache.Caching;
using VniteGameCache.LibraryImport;
using VniteGameCache.Persistence;

namespace VniteGameCache.Agent.Tests;

public sealed class ArchiveCacheTests
{
    [Fact]
    public async Task Archive_update_requires_empty_cache_and_reuses_game_id()
    {
        const string sevenZip = @"C:\Program Files\7-Zip\7z.exe";
        if (!File.Exists(sevenZip)) return;
        var root = Path.Combine(Path.GetTempPath(), "vgc-archive-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var archivePath = Path.Combine(root, "游戏.zip");
        var cacheBase = Path.Combine(root, "cache");
        var databasePath = Path.Combine(root, "index.db");
        async Task WriteArchive(string executable)
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            await using var writer = new StreamWriter(zip.CreateEntry(executable).Open());
            await writer.WriteAsync("game body");
        }
        string[] Args(string executable) => ["archive-import", "--archive-path", archivePath,
            "--executable-relative-path", executable, "--cache-root", cacheBase, "--db-path", databasePath];
        try
        {
            await WriteArchive("Game.exe");
            Assert.Equal(0, await AgentProgram.MainAsync(Args("Game.exe")));
            var factory = new SqliteConnectionFactory(new DatabaseOptions(databasePath));
            var gameId = CandidateIds.GameIdFromSourceRoot(archivePath);
            var game = new GameRepository(factory).GetById(gameId)!;
            var savePath = Path.Combine(game.CacheRoot, "save.dat");
            await File.WriteAllTextAsync(savePath, "keep this save");

            await WriteArchive("Game_CN.exe");
            File.SetLastWriteTimeUtc(archivePath, DateTime.UtcNow.AddMinutes(1));
            Assert.Equal(20, await AgentProgram.MainAsync(Args("Game_CN.exe")));
            Assert.Equal("keep this save", await File.ReadAllTextAsync(savePath));
            Assert.Equal("Game.exe", new ArchiveSourceRepository(factory).GetByGameId(gameId)!.ExecutableRelativePath);

            File.Delete(savePath);
            Assert.Equal(0, await AgentProgram.MainAsync(Args("Game_CN.exe")));
            Assert.Equal("Game_CN.exe", new ArchiveSourceRepository(factory).GetByGameId(gameId)!.ExecutableRelativePath);
            Assert.EndsWith("Game_CN.exe", new GameRepository(factory).GetById(gameId)!.ExecutablePath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Zip_archive_extracts_once_and_reuses_ready_cache()
    {
        const string sevenZip = @"C:\Program Files\7-Zip\7z.exe";
        if (!File.Exists(sevenZip)) return;
        var root = Path.Combine(Path.GetTempPath(), "vgc-archive-test-" + Guid.NewGuid().ToString("N"));
        var sourceDirectory = Path.Combine(root, "source");
        var cacheRoot = Path.Combine(root, "cache", "game");
        Directory.CreateDirectory(sourceDirectory);
        var archivePath = Path.Combine(sourceDirectory, "中文 游戏.zip");
        try
        {
            using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("游戏/Game.exe");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("fake executable");
            }
            var info = new FileInfo(archivePath);
            var game = new GameRecord
            {
                GameId = Guid.NewGuid().ToString("D"),
                DisplayName = "Test",
                SourceRoot = sourceDirectory,
                CacheRoot = cacheRoot,
                ExecutablePath = Path.Combine(cacheRoot, "游戏", "Game.exe"),
                WorkingDirectory = cacheRoot
            };
            var source = new ArchiveSourceRecord
            {
                GameId = game.GameId,
                ArchivePath = archivePath,
                ExecutableRelativePath = "游戏/Game.exe",
                ExpandedBytes = 15,
                FileCount = 1,
                ArchiveSizeBytes = info.Length,
                ArchiveLastWriteUtc = info.LastWriteTimeUtc
            };

            var first = await ArchiveCachePreparer.PrepareAsync(game, source, 0);
            var preview = await ArchivePreview.ReadAsync(archivePath, sevenZip);
            Assert.True(first.Status == CacheResultStatus.Success,
                $"{first.ErrorCode}: {first.ErrorMessage}; preview={preview.Supported}/{preview.ExpandedBytes}/{preview.FileCount}/{string.Join(',', preview.ExecutableCandidates)}/{string.Join(',', preview.Warnings)}");
            Assert.True(File.Exists(first.LaunchExecutablePath));
            var second = await ArchiveCachePreparer.PrepareAsync(game, source, 0);
            Assert.Equal(CacheResultStatus.CacheAlreadyReady, second.Status);
            Assert.Equal(first.LaunchExecutablePath, second.LaunchExecutablePath);

            await File.AppendAllTextAsync(archivePath, "changed");
            var changed = await ArchiveCachePreparer.PrepareAsync(game, source, 0);
            Assert.Equal("archive_cache_requires_review", changed.ErrorCode);
            Assert.True(File.Exists(first.LaunchExecutablePath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
