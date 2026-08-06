using VniteGameCache.LibraryImport;
using VniteGameCache.Persistence;

namespace VniteGameCache.LibraryImport.Tests;

public sealed class LibraryImportTests
{
    [Fact]
    public void Discovery_respects_scan_depth_one_when_configured()
    {
        using var fixture = TestFixture.Create();
        fixture.Game("樱之诗", "Game.exe");
        fixture.Game("Galgame B", "Galgame B.exe");
        fixture.CreateDirectory("www");
        fixture.CreateDirectory(Path.Combine("Nested", "InnerGame"));

        var result = fixture.Discovery.Discover(fixture.Config with { ScanDepth = 1 });

        Assert.Contains(result.Candidates, candidate => candidate.FolderName == "樱之诗");
        Assert.Contains(result.Candidates, candidate => candidate.FolderName == "Galgame B");
        Assert.Contains(result.Candidates, candidate => candidate.FolderName == "www" && candidate.ImportStatus == ImportStatus.Ignored);
        Assert.DoesNotContain(result.Candidates, candidate => candidate.FolderName == "InnerGame");
    }

    [Fact]
    public void Discovery_finds_second_and_third_level_game_directories()
    {
        using var fixture = TestFixture.Create();
        fixture.Game(Path.Combine("分类", "作者A", "三级游戏"), "三级游戏.exe");
        fixture.Game(Path.Combine("分类", "二级游戏"), "二级游戏.exe");

        var result = fixture.Discovery.Discover(fixture.Config with { ScanDepth = 3 });

        Assert.Contains(result.Candidates, candidate => candidate.FolderName == "三级游戏" && candidate.ImportStatus == ImportStatus.AutoImportable);
        Assert.Contains(result.Candidates, candidate => candidate.FolderName == "二级游戏" && candidate.ImportStatus == ImportStatus.AutoImportable);
        Assert.DoesNotContain(result.Candidates, candidate => candidate.FolderName == "分类");
        Assert.DoesNotContain(result.Candidates, candidate => candidate.FolderName == "作者A");
    }

    [Fact]
    public void High_confidence_single_exe_is_auto_importable()
    {
        using var fixture = TestFixture.Create();
        fixture.Game("RPG游戏A", "Game.exe");

        var candidate = fixture.Discovery.Discover(fixture.Config).Candidates.Single(candidate => candidate.FolderName == "RPG游戏A");

        Assert.Equal(ImportStatus.AutoImportable, candidate.ImportStatus);
        Assert.NotNull(candidate.PreferredExecutable);
        Assert.True(candidate.Confidence >= 0.75);
    }

    [Fact]
    public void Multiple_similar_executables_require_confirmation()
    {
        using var fixture = TestFixture.Create();
        fixture.Game("MultiExe", "MultiExe.exe", "Game.exe");

        var candidate = fixture.Discovery.Discover(fixture.Config).Candidates.Single();

        Assert.Equal(ImportStatus.NeedsConfirmation, candidate.ImportStatus);
        Assert.Contains(candidate.Warnings, warning => warning.Contains("Multiple", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Import_creates_internal_game_with_stable_cache_root()
    {
        using var fixture = TestFixture.Create();
        fixture.Game("魔法使之夜", "Game.exe");
        var dryRun = fixture.Discovery.Discover(fixture.Config);

        var result = fixture.Import.Import(fixture.Config, dryRun.Candidates);

        var imported = Assert.Single(result.ImportedGames);
        var game = fixture.Games.GetById(imported.GameId)!;
        Assert.Equal(imported.SourceRoot, game.SourceRoot);
        Assert.Equal(imported.CacheRoot, game.CacheRoot);
        Assert.StartsWith(fixture.CacheRoot, imported.CacheRoot, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(imported.GameId[^8..], imported.CacheRoot);
        Assert.True(File.Exists(game.ExecutablePath));
    }

    [Fact]
    public void Repeated_scan_and_import_are_idempotent()
    {
        using var fixture = TestFixture.Create();
        fixture.Game("Stable", "Game.exe");
        var firstDryRun = fixture.Discovery.Discover(fixture.Config);
        var firstImport = fixture.Import.Import(fixture.Config, firstDryRun.Candidates);
        var firstGame = Assert.Single(firstImport.ImportedGames);

        var secondDryRun = fixture.Discovery.Discover(fixture.Config);
        var secondImport = fixture.Import.Import(fixture.Config, secondDryRun.Candidates);

        Assert.Contains(secondDryRun.Candidates, candidate => candidate.ImportStatus == ImportStatus.Existing && candidate.ExistingInternalGameId == firstGame.GameId);
        Assert.Empty(secondImport.ImportedGames);
        Assert.Single(fixture.Games.List());
    }

    [Fact]
    public void Existing_manual_fields_are_not_overwritten()
    {
        using var fixture = TestFixture.Create();
        var source = fixture.Game("Manual", "Game.exe");
        var manualExe = Path.Combine(source, "Manual.exe");
        File.WriteAllText(manualExe, "manual");
        var gameId = CandidateIds.GameIdFromSourceRoot(source);
        fixture.Games.Create(new GameRecord
        {
            GameId = gameId,
            DisplayName = "User Edited Name",
            SourceRoot = source,
            CacheRoot = Path.Combine(fixture.CacheRoot, "manual-cache"),
            ExecutablePath = manualExe,
            WorkingDirectory = source,
            ArgumentsJson = "[\"--user\"]"
        });

        var dryRun = fixture.Discovery.Discover(fixture.Config);
        fixture.Import.Import(fixture.Config, dryRun.Candidates, importNeedsConfirmation: true);

        var game = fixture.Games.GetById(gameId)!;
        Assert.Equal("User Edited Name", game.DisplayName);
        Assert.Equal(manualExe, game.ExecutablePath);
        Assert.Equal("[\"--user\"]", game.ArgumentsJson);
    }

    private sealed class TestFixture : IDisposable
    {
        private readonly string root;
        public string LibraryRoot { get; }
        public string CacheRoot { get; }
        public GameRepository Games { get; }
        public LibraryDiscoveryService Discovery { get; }
        public LibraryImportService Import { get; }
        public LibraryImportConfig Config { get; }

        private TestFixture(string root)
        {
            this.root = root;
            LibraryRoot = Path.Combine(root, "library root");
            CacheRoot = Path.Combine(root, "cache root");
            System.IO.Directory.CreateDirectory(LibraryRoot);
            System.IO.Directory.CreateDirectory(CacheRoot);
            var factory = new SqliteConnectionFactory(new DatabaseOptions(Path.Combine(root, "index.db")));
            new MigrationRunner(factory).Initialize();
            Games = new GameRepository(factory);
            Discovery = new LibraryDiscoveryService(Games);
            Import = new LibraryImportService(Games);
            Config = new LibraryImportConfig([LibraryRoot], CacheRoot);
        }

        public static TestFixture Create() => new(Path.Combine(Path.GetTempPath(), "vgc-library-import-tests", Guid.NewGuid().ToString("N")));

        public string Game(string folder, params string[] exeFiles)
        {
            var path = CreateDirectory(folder);
            foreach (var exe in exeFiles)
            {
                File.WriteAllText(Path.Combine(path, exe), exe);
            }
            return path;
        }

        public string CreateDirectory(string relative)
        {
            var path = Path.Combine(LibraryRoot, relative);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
