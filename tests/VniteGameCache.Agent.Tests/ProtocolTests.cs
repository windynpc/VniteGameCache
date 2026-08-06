using VniteGameCache.Contracts;
using VniteGameCache.Persistence;

namespace VniteGameCache.Agent.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void LaunchRequest_round_trips_snake_case_json()
    {
        var request = new LaunchRequest
        {
            RequestId = Guid.NewGuid(),
            GameId = "游戏 with space",
            ExecutablePath = @"C:\测试 路径\Game.exe",
            WorkingDirectory = @"C:\测试 路径",
            Arguments = new[] { "--name", "带 空格 \"quoted\"" },
            Environment = new Dictionary<string, string> { ["VGC_TEST"] = "1" },
            AdditionalProcessNames = new[] { "Game-Win64-Shipping" }
        };

        var json = ProtocolJson.Serialize(request);
        var parsed = ProtocolJson.Deserialize<LaunchRequest>(json);

        Assert.Contains("protocol_version", json);
        Assert.Contains("require_source_executable", json);
        Assert.Contains("request_id", json);
        Assert.NotNull(parsed);
        Assert.Equal(request.GameId, parsed.GameId);
        Assert.Equal(request.Arguments[1], parsed.Arguments[1]);
        Assert.Equal(request.WorkingDirectory, parsed.WorkingDirectory);
        Assert.True(parsed.RequireSourceExecutable);
    }

    [Fact]
    public void Invalid_prepare_request_is_rejected()
    {
        var request = new PrepareLaunchRequest
        {
            RequestId = Guid.NewGuid(),
            GameId = "",
            ExecutablePath = "",
            WorkingDirectory = ""
        };

        var error = LaunchRequestValidator.Validate(request);

        Assert.NotNull(error);
    }

    [Fact]
    public async Task Prepare_launch_simulated_failure_marks_task_failed()
    {
        var root = Path.Combine(Path.GetTempPath(), "vgc-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "index.db");
        var settingsPath = Path.Combine(root, "settings.json");
        File.WriteAllText(settingsPath, $$"""
        {
          "schema_version": 1,
          "database_path": "{{databasePath.Replace("\\", "\\\\")}}"
        }
        """);
        Environment.SetEnvironmentVariable("VGC_SETTINGS_PATH", settingsPath);
        Environment.SetEnvironmentVariable("VGC_AGENT_SIMULATE_FAILURE", "1");
        var originalIn = Console.In;
        var originalOut = Console.Out;
        try
        {
            var request = new PrepareLaunchRequest
            {
                RequestId = Guid.NewGuid(),
                GameId = "missing-game",
                ExecutablePath = @"C:\Game\Game.exe",
                WorkingDirectory = @"C:\Game"
            };
            using var input = new StringReader(ProtocolJson.Serialize(request));
            using var output = new StringWriter();
            Console.SetIn(input);
            Console.SetOut(output);

            var exitCode = await Agent.AgentProgram.MainAsync([]);

            Assert.Equal(21, exitCode);
            var factory = new SqliteConnectionFactory(new DatabaseOptions(databasePath));
            var task = new TaskRepository(factory).GetById($"prepare-launch-{request.RequestId}")!;
            Assert.Equal(TaskState.Failed, task.State);
            Assert.Equal("simulated_failure", task.LastErrorCode);
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
            Environment.SetEnvironmentVariable("VGC_SETTINGS_PATH", null);
            Environment.SetEnvironmentVariable("VGC_AGENT_SIMULATE_FAILURE", null);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task Game_upsert_reconciles_existing_vnite_game_id()
    {
        var root = Path.Combine(Path.GetTempPath(), "vgc-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "index.db");
        var factory = new SqliteConnectionFactory(new DatabaseOptions(databasePath));
        new MigrationRunner(factory).Initialize();
        var games = new GameRepository(factory);
        games.Create(new GameRecord
        {
            GameId = "vgc-existing-source",
            VniteGameId = "vnite-existing",
            DisplayName = "Existing",
            SourceRoot = @"G:\Old",
            CacheRoot = @"E:\Cache\Old",
            ExecutablePath = @"G:\Old\Game.exe",
            ArgumentsJson = "[]",
            WorkingDirectory = @"G:\Old",
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow
        });

        var originalOut = Console.Out;
        try
        {
            using var output = new StringWriter();
            Console.SetOut(output);

            var exitCode = await Agent.AgentProgram.MainAsync([
                "game-upsert",
                "--db-path", databasePath,
                "--game-id", "vnite-existing",
                "--source-root", @"G:\New",
                "--cache-root", @"E:\Cache\New",
                "--executable-path", @"G:\New\Game.exe",
                "--display-name", "New"
            ]);

            Assert.Equal(0, exitCode);
            Assert.Single(games.List());
            var loaded = games.GetByVniteGameId("vnite-existing")!;
            Assert.Equal("vgc-existing-source", loaded.GameId);
            Assert.Equal(@"G:\New", loaded.SourceRoot);
            Assert.Equal(@"G:\New\Game.exe", loaded.ExecutablePath);
            Assert.Equal(@"G:\New", loaded.WorkingDirectory);
            Assert.Equal("Existing", loaded.DisplayName);
        }
        finally
        {
            Console.SetOut(originalOut);
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
