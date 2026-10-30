using System.Diagnostics;
using VniteGameCache.Contracts;
using VniteGameCache.Persistence;
using VniteGameCache.Wrapper;

namespace VniteGameCache.Wrapper.Tests;

public sealed class WrapperTests
{
    [Fact]
    public async Task Closing_game_job_stops_spawned_child_process()
    {
        Assert.True(OperatingSystem.IsWindows());

        var childPidFile = Path.Combine(Path.GetTempPath(), $"vgc-job-child-{Guid.NewGuid():N}.txt");
        var executable = PowerShellPath();
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add($"$child = Start-Process -FilePath '{executable}' -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 30' -PassThru; Set-Content -LiteralPath '{childPidFile}' -Value $child.Id; Start-Sleep -Seconds 30");

        using var job = GameProcessJob.Create()!;
        using var parent = Process.Start(startInfo)!;
        job.Assign(parent);
        try
        {
            Assert.True(await WaitForFileAsync(childPidFile));
            var childId = int.Parse(await File.ReadAllTextAsync(childPidFile));
            job.Dispose();
            Assert.True(parent.WaitForExit(5000));
            await Task.Delay(200);
            Assert.False(IsAlive(childId));
        }
        finally
        {
            if (!parent.HasExited) parent.Kill(entireProcessTree: true);
            File.Delete(childPidFile);
        }
    }

    [Fact]
    public async Task Process_monitor_waits_for_job_child_outside_expected_root()
    {
        Assert.True(OperatingSystem.IsWindows());
        var executable = PowerShellPath();
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add($"Start-Sleep -Milliseconds 300; Start-Process -FilePath '{executable}' -ArgumentList '-NoProfile','-Command','Start-Sleep -Milliseconds 900'; Start-Sleep -Milliseconds 200");

        using var job = GameProcessJob.Create()!;
        using var parent = Process.Start(startInfo)!;
        job.Assign(parent);
        var watch = Stopwatch.StartNew();

        await new ProcessMonitor().WaitForExitTreeAsync(
            parent, [], Path.GetTempPath(), DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150),
            CancellationToken.None, restrictDescendantsToExpectedRoot: true,
            hasActiveJobProcesses: job.HasActiveProcesses);

        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds >= 900);
        Assert.False(job.HasActiveProcesses());
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public async Task Wrapper_returns_executable_not_found_for_missing_file()
    {
        var request = NewRequest(
            executablePath: Path.Combine(Path.GetTempPath(), "missing-game.exe"),
            workingDirectory: Path.GetTempPath());

        var result = await WrapperRunner.RunAsync(
            request,
            NewOptions(),
            CancellationToken.None);

        Assert.Equal((int)WrapperExitCode.ExecutableNotFound, result.ExitCode);
        Assert.False(result.Result.Success);
    }

    [Fact]
    public async Task Wrapper_waits_for_agent_ready_before_launching_process()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"vgc-marker-{Guid.NewGuid():N}.txt");
        var command = $"Start-Sleep -Milliseconds 50; Set-Content -LiteralPath '{marker}' -Value 'started'";
        var request = NewRequest(
            executablePath: PowerShellPath(),
            workingDirectory: Path.GetTempPath(),
            arguments: new[] { "-NoProfile", "-Command", command });
        var stopwatch = Stopwatch.StartNew();

        var result = await WrapperRunner.RunAsync(request, NewOptions(), CancellationToken.None);

        stopwatch.Stop();
        Assert.Equal((int)WrapperExitCode.Success, result.ExitCode);
        Assert.True(result.Result.Success);
        Assert.True(await WaitForFileAsync(marker));
        Assert.True(stopwatch.ElapsedMilliseconds >= 450);
    }

    [Fact]
    public async Task Wrapper_preserves_chinese_space_path_working_directory_and_quoted_arguments()
    {
        var root = Path.Combine(Path.GetTempPath(), $"VGC 中文 路径 {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var output = Path.Combine(root, "args.txt");
        var script = Path.Combine(root, "write-args.ps1");
        await File.WriteAllTextAsync(script, "$args | Set-Content -LiteralPath $env:VGC_OUTPUT -Encoding utf8");
        var request = NewRequest(
            executablePath: PowerShellPath(),
            workingDirectory: root,
            arguments: new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "alpha beta", "中文参数", "quoted \"value\"" })
            with
            {
                Environment = new Dictionary<string, string> { ["VGC_OUTPUT"] = output }
            };

        var result = await WrapperRunner.RunAsync(request, NewOptions(), CancellationToken.None);

        Assert.Equal((int)WrapperExitCode.Success, result.ExitCode);
        Assert.True(await WaitForFileAsync(output));
        var lines = await File.ReadAllLinesAsync(output);
        Assert.Contains("alpha beta", lines);
        Assert.Contains("中文参数", lines);
        Assert.Contains("quoted \"value\"", lines);
    }

    [Fact]
    public async Task Wrapper_returns_agent_rejected_when_agent_simulates_failure()
    {
        Environment.SetEnvironmentVariable("VGC_AGENT_SIMULATE_FAILURE", "1");
        try
        {
            var request = NewRequest(PowerShellPath(), Path.GetTempPath(), new[] { "-NoProfile", "-Command", "exit 0" });

            var result = await WrapperRunner.RunAsync(request, NewOptions(), CancellationToken.None);

            Assert.Equal((int)WrapperExitCode.AgentRejected, result.ExitCode);
            Assert.False(result.Result.Success);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VGC_AGENT_SIMULATE_FAILURE", null);
        }
    }

    [Fact]
    public async Task Wrapper_supervises_game_process_tree_until_exit()
    {
        var childName = Path.GetFileNameWithoutExtension(PowerShellPath());
        var command = "Start-Process powershell.exe -ArgumentList '-NoProfile','-Command','Start-Sleep -Milliseconds 700'; Start-Sleep -Milliseconds 700";
        var request = NewRequest(
            PowerShellPath(),
            Path.GetTempPath(),
            new[] { "-NoProfile", "-Command", command })
            with
            {
                AdditionalProcessNames = new[] { childName }
            };
        var stopwatch = Stopwatch.StartNew();

        var result = await WrapperRunner.RunAsync(request, NewOptions(), CancellationToken.None);

        stopwatch.Stop();
        Assert.Equal((int)WrapperExitCode.Success, result.ExitCode);
        Assert.True(stopwatch.ElapsedMilliseconds >= 650);
        Assert.True(result.Result.ObservedProcessIds.Count >= 1);
        Assert.Contains("supervised", result.Result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Process_monitor_discovers_child_spawned_after_initial_discovery_window()
    {
        var executable = PowerShellPath();
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add($"Start-Sleep -Milliseconds 400; Start-Process -FilePath '{executable}' -ArgumentList '-NoProfile','-Command','Start-Sleep -Milliseconds 700'");
        using var initialProcess = Process.Start(startInfo)!;
        var stopwatch = Stopwatch.StartNew();

        var observed = await new ProcessMonitor().WaitForExitTreeAsync(
            initialProcess,
            [Path.GetFileNameWithoutExtension(executable)],
            Path.GetDirectoryName(executable),
            DateTimeOffset.UtcNow.AddSeconds(-1),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(150),
            CancellationToken.None);

        stopwatch.Stop();
        Assert.True(observed.Count >= 2);
        Assert.True(stopwatch.ElapsedMilliseconds >= 900);
    }

    [Fact]
    public async Task Wrapper_honors_cancellation()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var request = NewRequest(PowerShellPath(), Path.GetTempPath(), new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 5" });

        var result = await WrapperRunner.RunAsync(request, NewOptions(), cancellation.Token);

        Assert.Equal((int)WrapperExitCode.Cancelled, result.ExitCode);
    }

    [Fact]
    public async Task Wrapper_ignores_duplicate_launch_for_same_game()
    {
        var gameId = $"duplicate-{Guid.NewGuid():N}";
        var first = NewRequest(PowerShellPath(), Path.GetTempPath(), new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 2" }, gameId);
        var second = NewRequest(PowerShellPath(), Path.GetTempPath(), new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 2" }, gameId);
        var firstTask = WrapperRunner.RunAsync(first, NewOptions(), CancellationToken.None);

        await Task.Delay(200);
        var secondResult = await WrapperRunner.RunAsync(second, NewOptions(), CancellationToken.None);
        var firstResult = await firstTask;

        Assert.Equal((int)WrapperExitCode.Success, firstResult.ExitCode);
        Assert.Equal((int)WrapperExitCode.Success, secondResult.ExitCode);
        Assert.Contains("already running", secondResult.Result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrapper_game_id_reads_database_and_launches_ready_cache_without_source()
    {
        const string gameId = "11111111-1111-4111-8111-111111111111";
        var root = Path.Combine(Path.GetTempPath(), "vgc-wrapper-db-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dbPath = Path.Combine(root, "index.db");
            var sourceRoot = Path.Combine(root, "missing-source");
            var cacheRoot = Path.Combine(root, "cache root");
            Directory.CreateDirectory(cacheRoot);
            var cacheExe = Path.Combine(cacheRoot, "Game.exe");
            File.Copy(PowerShellPath(), cacheExe);
            var marker = Path.Combine(root, "marker.txt");

            var factory = new SqliteConnectionFactory(new DatabaseOptions(dbPath));
            new MigrationRunner(factory).Initialize();
            new GameRepository(factory).Create(new GameRecord
            {
                GameId = gameId,
                DisplayName = "game-db",
                SourceRoot = sourceRoot,
                CacheRoot = cacheRoot,
                ExecutablePath = Path.Combine(sourceRoot, "Game.exe"),
                ArgumentsJson = System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    "-NoProfile",
                    "-Command",
                    $"Set-Content -LiteralPath '{marker}' -Value $PWD"
                }),
                WorkingDirectory = sourceRoot,
                SourceRevision = 1,
                CacheRevision = 1,
                CacheStatus = CacheStatus.Ready
            });
            new FileEntryRepository(factory).UpsertMany(new[]
            {
                new FileEntryRecord
                {
                    GameId = gameId,
                    RelativePath = "Game.exe",
                    SizeBytes = new FileInfo(cacheExe).Length,
                    ModifiedUtc = DateTimeOffset.UtcNow,
                    FileRole = FileRole.Executable,
                    SourceRevision = 1,
                    CacheRevision = 1
                }
            });

            var options = NewOptions(databasePath: dbPath);
            var request = await WrapperProgram.ReadRequestAsync(options with { GameId = gameId });

            var result = await WrapperRunner.RunAsync(request, options, CancellationToken.None);

            Assert.Equal((int)WrapperExitCode.Success, result.ExitCode);
            Assert.True(await WaitForFileAsync(marker));
            Assert.Contains(cacheRoot, await File.ReadAllTextAsync(marker), StringComparison.OrdinalIgnoreCase);
            var finishedGame = new GameRepository(factory).GetById(gameId)!;
            Assert.Equal(CacheStatus.Ready, finishedGame.CacheStatus);
            Assert.NotNull(finishedGame.LastPlayedUtc);
        }
        finally
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

    [Fact]
    public async Task Wrapper_game_id_accepts_vnite_game_id_alias()
    {
        const string internalGameId = "22222222-2222-4222-8222-222222222222";
        const string vniteGameId = "33333333-3333-4333-8333-333333333333";
        var root = Path.Combine(Path.GetTempPath(), "vgc-wrapper-db-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dbPath = Path.Combine(root, "index.db");
            var sourceRoot = Path.Combine(root, "source");
            var cacheRoot = Path.Combine(root, "cache");
            Directory.CreateDirectory(cacheRoot);
            var cacheExe = Path.Combine(cacheRoot, "Game.exe");
            File.Copy(PowerShellPath(), cacheExe);

            var factory = new SqliteConnectionFactory(new DatabaseOptions(dbPath));
            new MigrationRunner(factory).Initialize();
            new GameRepository(factory).Create(new GameRecord
            {
                GameId = internalGameId,
                VniteGameId = vniteGameId,
                DisplayName = "alias",
                SourceRoot = sourceRoot,
                CacheRoot = cacheRoot,
                ExecutablePath = Path.Combine(sourceRoot, "Game.exe"),
                ArgumentsJson = "[]",
                WorkingDirectory = sourceRoot,
                SourceRevision = 1,
                CacheRevision = 1,
                CacheStatus = CacheStatus.Ready
            });

            var request = await WrapperProgram.ReadRequestAsync(NewOptions(databasePath: dbPath) with { GameId = vniteGameId });

            Assert.Equal(internalGameId, request.GameId);
            Assert.Equal(Path.Combine(sourceRoot, "Game.exe"), request.ExecutablePath);
        }
        finally
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

    [Fact]
    public async Task Wrapper_game_id_auto_rebinds_unique_source_root_without_changing_internal_id()
    {
        const string internalGameId = "44444444-4444-4444-8444-444444444444";
        const string oldVniteGameId = "55555555-5555-4555-8555-555555555555";
        const string newVniteGameId = "66666666-6666-4666-8666-666666666666";
        var root = Path.Combine(Path.GetTempPath(), "vgc-wrapper-db-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dbPath = Path.Combine(root, "index.db");
            var sourceRoot = Path.Combine(root, "source");
            var cacheRoot = Path.Combine(root, "cache");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(cacheRoot);
            var cacheExe = Path.Combine(cacheRoot, "Game.exe");
            File.Copy(PowerShellPath(), cacheExe);

            var factory = new SqliteConnectionFactory(new DatabaseOptions(dbPath));
            new MigrationRunner(factory).Initialize();
            new GameRepository(factory).Create(new GameRecord
            {
                GameId = internalGameId,
                VniteGameId = oldVniteGameId,
                DisplayName = "auto-rebind",
                SourceRoot = sourceRoot,
                CacheRoot = cacheRoot,
                ExecutablePath = Path.Combine(sourceRoot, "Game.exe"),
                ArgumentsJson = "[]",
                WorkingDirectory = sourceRoot,
                SourceRevision = 1,
                CacheRevision = 1,
                CacheStatus = CacheStatus.Ready
            });

            var options = NewOptions(databasePath: dbPath) with
            {
                GameId = newVniteGameId,
                SourceRoot = sourceRoot
            };

            var request = await WrapperProgram.ReadRequestAsync(options);
            var rebound = new GameRepository(factory).GetById(internalGameId)!;

            Assert.Equal(internalGameId, request.GameId);
            Assert.Equal(newVniteGameId, rebound.VniteGameId);
        }
        finally
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

    [Fact]
    public void Wrapper_parse_args_accepts_rebind_metadata()
    {
        const string gameId = "77777777-7777-4777-8777-777777777777";
        var options = WrapperProgram.ParseArgs([
            "--game-id", gameId,
            "--vnite-id", "new-vnite-id",
            "--source-root", @"G:\Game\Demo",
            "--shortcut-path", @"C:\Temp\Demo.lnk"
        ]);

        Assert.Equal(gameId, options.GameId);
        Assert.Equal("new-vnite-id", options.VniteId);
        Assert.Equal(@"G:\Game\Demo", options.SourceRoot);
        Assert.Equal(@"C:\Temp\Demo.lnk", options.ShortcutPath);
    }

    [Fact]
    public void Wrapper_parse_args_trims_shortcut_quotes()
    {
        var options = WrapperProgram.ParseArgs([
            "--game-id", "'57137a85-c50a-40d4-951e-d0fa9978be83'",
            "--source-root", "\"G:\\Series Game\\ntrman\\The Rural Homecoming\""
        ]);

        Assert.Equal("57137a85-c50a-40d4-951e-d0fa9978be83", options.GameId);
        Assert.Equal(@"G:\Series Game\ntrman\The Rural Homecoming", options.SourceRoot);
    }

    [Theory]
    [InlineData("--game-id=57137a85-c50a-40d4-951e-d0fa9978be83")]
    [InlineData("--game-id 57137a85-c50a-40d4-951e-d0fa9978be83")]
    [InlineData("--game-id \"57137a85-c50a-40d4-951e-d0fa9978be83\"")]
    [InlineData("--game-id '57137a85-c50a-40d4-951e-d0fa9978be83'")]
    [InlineData("\"--game-id\" \"57137a85-c50a-40d4-951e-d0fa9978be83\"")]
    [InlineData("  --game-id  57137A85-C50A-40D4-951E-D0FA9978BE83  ")]
    public void Launch_argument_codec_accepts_supported_contracts(string arguments)
    {
        var parsed = LaunchArgumentCodec.Parse(arguments);

        Assert.Equal("57137a85-c50a-40d4-951e-d0fa9978be83", parsed.GameId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--game-id")]
    [InlineData("--game-id not-a-guid")]
    [InlineData("--game-id '57137a85-c50a-40d4-951e-d0fa9978be83")]
    [InlineData("--game-id=57137a85-c50a-40d4-951e-d0fa9978be83 --game-id=57137a85-c50a-40d4-951e-d0fa9978be83")]
    [InlineData("--game-id=57137a85-c50a-40d4-951e-d0fa9978be83 --source-root G:\\Game")]
    public void Launch_argument_codec_rejects_invalid_contracts(string arguments)
    {
        Assert.Throws<LaunchArgumentException>(() => LaunchArgumentCodec.Parse(arguments));
    }

    [Fact]
    public void Launch_argument_codec_builds_canonical_contract()
    {
        Assert.Equal(
            "--game-id=57137a85-c50a-40d4-951e-d0fa9978be83",
            LaunchArgumentCodec.Build("57137A85-C50A-40D4-951E-D0FA9978BE83"));
    }

    private static LaunchRequest NewRequest(
        string executablePath,
        string workingDirectory,
        IReadOnlyList<string>? arguments = null,
        string? gameId = null)
    {
        return new LaunchRequest
        {
            RequestId = Guid.NewGuid(),
            GameId = gameId ?? $"test-game-{Guid.NewGuid():N}",
            ExecutablePath = executablePath,
            WorkingDirectory = workingDirectory,
            Arguments = arguments ?? Array.Empty<string>()
        };
    }

    private static WrapperOptions NewOptions(string? databasePath = null)
    {
        return new WrapperOptions(
            null,
            null,
            null,
            databasePath,
            FindAgentDll(),
            Path.Combine(Path.GetTempPath(), "VniteGameCache.Tests", "logs"),
            null);
    }

    private static string PowerShellPath()
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    private static string FindAgentDll()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            foreach (var configuration in new[] { "Release", "Debug" })
            {
                var candidate = Path.Combine(
                    directory.FullName,
                    "src",
                    "VniteGameCache.Agent",
                    "bin",
                    configuration,
                    "net8.0",
                    "VniteGameCache.Agent.dll");
                if (File.Exists(candidate)) return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find built VniteGameCache.Agent.dll");
    }

    private static async Task<bool> WaitForFileAsync(string path, int timeoutMs = 3000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                return true;
            }

            await Task.Delay(50);
        }

        return File.Exists(path);
    }
}
