using System.Text.Json;
using VniteGameCache.Indexing;
using VniteGameCache.Persistence;

namespace VniteGameCache.Indexing.Tests;

public sealed class IndexingTests
{
    [Fact]
    public void Index_game_writes_files_fingerprints_manifest_and_revision()
    {
        using var fixture = TestFixture.Create();
        var source = fixture.CreateGameDirectory("游戏 A");
        File.WriteAllText(Path.Combine(source, "Game.exe"), "exe");
        Directory.CreateDirectory(Path.Combine(source, "www", "save"));
        File.WriteAllText(Path.Combine(source, "www", "save", "file1.rpgsave"), "save");
        Directory.CreateDirectory(Path.Combine(source, "logs"));
        File.WriteAllText(Path.Combine(source, "logs", "debug.log"), "log");
        File.WriteAllText(Path.Combine(source, "data.xp3"), "data");
        fixture.CreateGame("game-1", source, Path.Combine(source, "Game.exe"));

        var result = fixture.Service.IndexGame("game-1");

        Assert.True(result.Status is ManifestStatus.Complete or ManifestStatus.CompleteWithWarnings);
        Assert.Equal(1, result.SourceRevision);
        Assert.True(File.Exists(result.ManifestPath));

        var game = fixture.Games.GetById("game-1")!;
        Assert.Equal(1, game.SourceRevision);
        Assert.NotNull(game.LastFullScanUtc);

        var files = fixture.Files.ListByGame("game-1");
        Assert.Contains(files, file => file.RelativePath == "Game.exe" && file.FileRole == FileRole.Executable);
        Assert.Contains(files, file => file.RelativePath == "www/save/file1.rpgsave" && file.FileRole == FileRole.Save);
        Assert.All(files, file => Assert.DoesNotContain('\\', file.RelativePath));

        var directories = fixture.Directories.ListByGame("game-1");
        Assert.Contains(directories, directory => directory.RelativePath == string.Empty);
        Assert.Contains(directories, directory => directory.RelativePath == "www/save");

        using var manifest = JsonDocument.Parse(File.ReadAllText(result.ManifestPath!));
        Assert.Equal("game-1", manifest.RootElement.GetProperty("game_id").GetString());
        Assert.Equal(1, manifest.RootElement.GetProperty("source_revision").GetInt64());
        Assert.True(manifest.RootElement.GetProperty("executable_candidates").GetArrayLength() >= 1);
        Assert.True(manifest.RootElement.GetProperty("mutable_path_candidates").GetArrayLength() >= 1);
        Assert.Contains(manifest.RootElement.GetProperty("excluded_paths").EnumerateArray(), item => item.GetProperty("relative_path").GetString() == "logs/");
    }

    [Fact]
    public void Executable_scoring_demotes_installers_and_preserves_configured_executable()
    {
        using var fixture = TestFixture.Create();
        var source = fixture.CreateGameDirectory("My Game");
        File.WriteAllText(Path.Combine(source, "My Game.exe"), "main");
        File.WriteAllText(Path.Combine(source, "setup.exe"), "setup");
        File.WriteAllText(Path.Combine(source, "UnityCrashHandler64.exe"), "crash");
        fixture.CreateGame("game-1", source, Path.Combine(source, "My Game.exe"));

        var result = fixture.Service.IndexGame("game-1");

        Assert.Equal(ManifestStatus.Complete, result.Status);
        using var manifest = JsonDocument.Parse(File.ReadAllText(result.ManifestPath!));
        var candidates = manifest.RootElement.GetProperty("executable_candidates").EnumerateArray().ToList();
        Assert.Equal("My Game.exe", candidates[0].GetProperty("relative_path").GetString());
        Assert.True(candidates[0].GetProperty("is_probable_game_executable").GetBoolean());
        Assert.True(candidates.Single(candidate => candidate.GetProperty("relative_path").GetString() == "setup.exe").GetProperty("score").GetInt32() < candidates[0].GetProperty("score").GetInt32());

        var game = fixture.Games.GetById("game-1")!;
        Assert.EndsWith("My Game.exe", game.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Suggested_exclude_can_be_overridden()
    {
        using var fixture = TestFixture.Create(includeSuggested: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "logs/**" });
        var source = fixture.CreateGameDirectory("With Logs");
        Directory.CreateDirectory(Path.Combine(source, "logs"));
        File.WriteAllText(Path.Combine(source, "logs", "game.log"), "needed");
        fixture.CreateGame("game-1", source, string.Empty);

        fixture.Service.IndexGame("game-1");

        var file = fixture.Files.Get("game-1", "logs/game.log")!;
        Assert.Equal(FileRole.Log, file.FileRole);
    }

    [Fact]
    public void Missing_source_root_preserves_old_index_revision_and_manifest()
    {
        using var fixture = TestFixture.Create();
        var source = fixture.CreateGameDirectory("Initial");
        File.WriteAllText(Path.Combine(source, "Game.exe"), "exe");
        fixture.CreateGame("game-1", source, Path.Combine(source, "Game.exe"));
        var first = fixture.Service.IndexGame("game-1");
        var oldManifestText = File.ReadAllText(first.ManifestPath!);

        Directory.Delete(source, recursive: true);
        var second = fixture.Service.IndexGame("game-1");

        Assert.Equal(ManifestStatus.Failed, second.Status);
        Assert.Equal(IndexingErrorCode.SourceRootNotFound.ToString(), second.ErrorCode);
        Assert.Equal(1, fixture.Games.GetById("game-1")!.SourceRevision);
        Assert.Single(fixture.Files.ListByGame("game-1"));
        Assert.Equal(oldManifestText, File.ReadAllText(first.ManifestPath!));
    }

    [Fact]
    public void Cancelled_index_preserves_old_index_revision_and_manifest()
    {
        using var fixture = TestFixture.Create();
        var source = fixture.CreateGameDirectory("Cancel");
        File.WriteAllText(Path.Combine(source, "Game.exe"), "exe");
        fixture.CreateGame("game-1", source, Path.Combine(source, "Game.exe"));
        var first = fixture.Service.IndexGame("game-1");
        File.WriteAllText(Path.Combine(source, "new.dat"), "new");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var second = fixture.Service.IndexGame("game-1", cancellationToken: cancellation.Token);

        Assert.Equal(ManifestStatus.Cancelled, second.Status);
        Assert.Equal(1, fixture.Games.GetById("game-1")!.SourceRevision);
        Assert.DoesNotContain(fixture.Files.ListByGame("game-1"), file => file.RelativePath == "new.dat");
        Assert.True(File.Exists(first.ManifestPath));
    }

    [Fact]
    public void Empty_directory_gets_root_fingerprint_and_manifest()
    {
        using var fixture = TestFixture.Create();
        var source = fixture.CreateGameDirectory("Empty");
        fixture.CreateGame("game-1", source, string.Empty);

        var result = fixture.Service.IndexGame("game-1");

        Assert.Equal(ManifestStatus.Complete, result.Status);
        Assert.Empty(fixture.Files.ListByGame("game-1"));
        Assert.Contains(fixture.Directories.ListByGame("game-1"), directory => directory.RelativePath == string.Empty);
    }

    [Fact]
    public void Quick_probe_detects_known_file_change_without_full_scan()
    {
        using var fixture = TestFixture.Create();
        var source = fixture.CreateGameDirectory("Probe File");
        var executable = Path.Combine(source, "Game.exe");
        File.WriteAllText(executable, "exe");
        fixture.CreateGame("game-1", source, executable);
        fixture.Service.IndexGame("game-1");
        var probe = new SourceQuickProbe();
        var game = fixture.Games.GetById("game-1")!;
        var files = fixture.Files.ListByGame("game-1");
        var directories = fixture.Directories.ListByGame("game-1");

        Assert.Equal(SourceQuickProbeStatus.Unchanged, probe.Probe(game, files, directories).Status);
        File.WriteAllText(executable, "changed executable content");

        var changed = probe.Probe(game, files, directories);
        Assert.Equal(SourceQuickProbeStatus.Changed, changed.Status);
        Assert.Equal("Game.exe", changed.RelativePath);
    }

    [Fact]
    public void Quick_probe_detects_shallow_addition_and_allows_offline_cache()
    {
        using var fixture = TestFixture.Create();
        var source = fixture.CreateGameDirectory("Probe Shape");
        var executable = Path.Combine(source, "Game.exe");
        File.WriteAllText(executable, "exe");
        Directory.CreateDirectory(Path.Combine(source, "data"));
        fixture.CreateGame("game-1", source, executable);
        fixture.Service.IndexGame("game-1");
        var probe = new SourceQuickProbe();
        var game = fixture.Games.GetById("game-1")!;
        var files = fixture.Files.ListByGame("game-1");
        var directories = fixture.Directories.ListByGame("game-1");

        File.WriteAllText(Path.Combine(source, "data", "patch.bin"), "patch");
        Assert.Equal(SourceQuickProbeStatus.Changed, probe.Probe(game, files, directories).Status);

        Directory.Delete(source, recursive: true);
        Assert.Equal(SourceQuickProbeStatus.Unavailable, probe.Probe(game, files, directories).Status);
    }

    private sealed class TestFixture : IDisposable
    {
        private readonly string root;
        public SqliteConnectionFactory Factory { get; }
        public GameRepository Games { get; }
        public FileEntryRepository Files { get; }
        public DirectoryFingerprintRepository Directories { get; }
        public IndexingService Service { get; }

        private TestFixture(string root, IReadOnlySet<string>? includeSuggested)
        {
            this.root = root;
            Factory = new SqliteConnectionFactory(new DatabaseOptions(Path.Combine(root, "index.db")));
            new MigrationRunner(Factory).Initialize();
            Games = new GameRepository(Factory);
            Files = new FileEntryRepository(Factory);
            Directories = new DirectoryFingerprintRepository(Factory);
            var options = new IndexingOptions(Path.Combine(root, "manifests"), IncludeSuggestedExcludePatterns: includeSuggested);
            Service = new IndexingService(new IndexingStore(Factory), new FileSystemScanner(), new ManifestBuilder(), options);
        }

        public static TestFixture Create(IReadOnlySet<string>? includeSuggested = null) =>
            new(Path.Combine(Path.GetTempPath(), "vgc-indexing-tests", Guid.NewGuid().ToString("N")), includeSuggested);

        public string CreateGameDirectory(string name)
        {
            var path = Path.Combine(root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void CreateGame(string gameId, string sourceRoot, string executablePath)
        {
            Games.Create(new GameRecord
            {
                GameId = gameId,
                DisplayName = gameId,
                SourceRoot = sourceRoot,
                CacheRoot = Path.Combine(root, "cache", gameId),
                ExecutablePath = executablePath,
                WorkingDirectory = sourceRoot
            });
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
