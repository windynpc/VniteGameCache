using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VniteGameCache.Contracts;
using VniteGameCache.Persistence;

namespace VniteGameCache.Wrapper;

public sealed record WrapperOptions(
    string? RequestJson,
    string? RequestFile,
    string? GameId,
    string? DatabasePath,
    string? AgentPath,
    string? LogDirectory,
    string? DotnetPath,
    string? VniteId = null,
    string? SourceRoot = null,
    string? ShortcutPath = null);

public sealed record WrapperRunResult(int ExitCode, LaunchResult Result);

public static class WrapperProgram
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    public static async Task<int> MainAsync(string[] args)
    {
        WrapperRunResult runResult;
        try
        {
            var options = ParseArgs(args);
            var request = await ReadRequestAsync(options);
            runResult = await WrapperRunner.RunAsync(request, options, CancellationToken.None);
        }
        catch (Exception error)
        {
            var requestId = Guid.Empty;
            var gameId = string.Empty;
            var errorCode = error switch
            {
                LaunchArgumentException launchError => launchError.Code,
                WrapperGameResolutionException resolutionError => resolutionError.Code,
                _ => "invalid_request"
            };
            runResult = new WrapperRunResult(
                (int)WrapperExitCode.InvalidRequest,
                new LaunchResult
                {
                    RequestId = requestId,
                    GameId = gameId,
                    Success = false,
                    ExitCode = (int)WrapperExitCode.InvalidRequest,
                    Error = new ErrorResult
                    {
                        RequestId = requestId,
                        GameId = gameId,
                        Code = errorCode,
                        Message = error.Message
                    }
                });
        }

        await Console.Out.WriteLineAsync(ProtocolJson.Serialize(runResult.Result));

        if (!runResult.Result.Success && runResult.Result.Error is { } err)
        {
            var msg = err.Message;
            if (err.Code == "GameRecordNotFound" || msg.StartsWith("Game not found:", StringComparison.OrdinalIgnoreCase))
            {
                msg = $"{msg}\n\n原因：此游戏在 Vnite 中重新添加后 ID 已变更，与插件本地数据库脱节。\n请在 VGC 控制台或 Vnite 插件界面点击【重新同步/一键接管】。";
            }
            MessageBox(IntPtr.Zero, $"无法通过 Vnite 游戏缓存启动：\n\n{msg}", "Vnite 游戏缓存启动器 (VGC)", 0x10);
        }

        return runResult.ExitCode;
    }

    public static WrapperOptions ParseArgs(IReadOnlyList<string> args)
    {
        string? requestJson = null;
        string? requestFile = null;
        string? gameId = null;
        string? databasePath = null;
        string? agentPath = null;
        string? logDirectory = null;
        string? dotnetPath = null;
        string? vniteId = null;
        string? sourceRoot = null;
        string? shortcutPath = null;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            string NextValue()
            {
                if (index + 1 >= args.Count)
                {
                    throw new ArgumentException($"Missing value for {arg}");
                }

                index++;
                return NormalizeArgumentValue(args[index]);
            }

            void AssignGameId(string value)
            {
                if (!string.IsNullOrWhiteSpace(gameId))
                {
                    throw new LaunchArgumentException("DuplicateGameIdArgument", "Duplicate --game-id argument.");
                }

                gameId = LaunchArgumentCodec.NormalizeGameId(value);
            }

            if (arg.StartsWith("--game-id=", StringComparison.OrdinalIgnoreCase))
            {
                AssignGameId(arg["--game-id=".Length..]);
                continue;
            }

            switch (arg)
            {
                case "--request-json":
                    requestJson = NextValue();
                    break;
                case "--request-file":
                    requestFile = NextValue();
                    break;
                case "--game-id":
                    AssignGameId(NextValue());
                    break;
                case "--vnite-id":
                    vniteId = NextValue();
                    break;
                case "--source-root":
                    sourceRoot = NextValue();
                    break;
                case "--shortcut-path":
                    shortcutPath = NextValue();
                    break;
                case "--db-path":
                    databasePath = NextValue();
                    break;
                case "--agent-path":
                    agentPath = NextValue();
                    break;
                case "--log-dir":
                    logDirectory = NextValue();
                    break;
                case "--dotnet-path":
                    dotnetPath = NextValue();
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        return new WrapperOptions(requestJson, requestFile, gameId, databasePath, agentPath, logDirectory, dotnetPath, vniteId, sourceRoot, shortcutPath);
    }

    private static string NormalizeArgumentValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '\'' && trimmed[^1] == '\'') ||
             (trimmed[0] == '"' && trimmed[^1] == '"')))
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }

    public static async Task<LaunchRequest> ReadRequestAsync(WrapperOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.GameId))
        {
            return ReadRequestFromDatabase(options.GameId, options);
        }

        var json = options.RequestJson;
        if (string.IsNullOrWhiteSpace(json) && !string.IsNullOrWhiteSpace(options.RequestFile))
        {
            json = await File.ReadAllTextAsync(options.RequestFile);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Either --game-id, --request-json, or --request-file is required");
        }

        return ProtocolJson.Deserialize<LaunchRequest>(json)
               ?? throw new ArgumentException("Launch request could not be parsed");
    }

    private static LaunchRequest ReadRequestFromDatabase(string gameId, WrapperOptions wrapperOptions)
    {
        var options = string.IsNullOrWhiteSpace(wrapperOptions.DatabasePath)
            ? new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath())
            : new DatabaseOptions(wrapperOptions.DatabasePath);
        var factory = new SqliteConnectionFactory(options);
        new MigrationRunner(factory).Initialize();
        var repository = new GameRepository(factory);
        var game = WrapperGameResolver.Resolve(
            repository,
            gameId,
            new WrapperGameResolutionHints(wrapperOptions.VniteId, wrapperOptions.SourceRoot));

        return new LaunchRequest
        {
            RequestId = Guid.NewGuid(),
            GameId = game.GameId,
            ExecutablePath = game.ExecutablePath,
            WorkingDirectory = game.WorkingDirectory,
            Arguments = ParseArguments(game.ArgumentsJson),
            RequireSourceExecutable = false
        };
    }

    private static IReadOnlyList<string> ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<string>>(argumentsJson) ?? Array.Empty<string>();
        }
        catch (JsonException error)
        {
            throw new ArgumentException($"Game arguments_json could not be parsed: {error.Message}", error);
        }
    }
}

public static class WrapperRunner
{
    public static async Task<WrapperRunResult> RunAsync(
        LaunchRequest request,
        WrapperOptions options,
        CancellationToken cancellationToken)
    {
        var log = new StructuredLogger(options.LogDirectory);
        var validationError = LaunchRequestValidator.Validate(request);
        if (validationError is not null)
        {
            return Failure(request, WrapperExitCode.InvalidRequest, "invalid_request", validationError);
        }

        await log.WriteAsync(request, "wrapper", "received_request", Environment.ProcessId, request.ExecutablePath, "accepted", null);

        using var gameLock = RuntimeGameLock.TryAcquire(request.GameId);
        if (gameLock is null)
        {
            await log.WriteAsync(request, "wrapper", "duplicate_launch_ignored", null, request.ExecutablePath, "already_running", null);
            return Success(request, "Game is already running; duplicate launch was ignored.", Array.Empty<int>());
        }

        var executablePath = Path.GetFullPath(request.ExecutablePath);
        if (request.RequireSourceExecutable && !File.Exists(executablePath))
        {
            await log.WriteAsync(request, "wrapper", "executable_missing", null, executablePath, "failed", null);
            return Failure(request, WrapperExitCode.ExecutableNotFound, "executable_not_found", executablePath);
        }

        var workingDirectory = Path.GetFullPath(request.WorkingDirectory);
        if (request.RequireSourceExecutable && !Directory.Exists(workingDirectory))
        {
            await log.WriteAsync(request, "wrapper", "working_directory_missing", null, workingDirectory, "failed", null);
            return Failure(request, WrapperExitCode.InvalidRequest, "working_directory_missing", workingDirectory);
        }

        var agentClient = new AgentJsonLineClient(options.AgentPath, options.DotnetPath, options.DatabasePath, log);
        PrepareLaunchResponse prepareResponse;
        try
        {
            prepareResponse = await agentClient.PrepareAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Failure(request, WrapperExitCode.Cancelled, "cancelled", "Wrapper request was cancelled");
        }
        catch (Exception error)
        {
            await log.WriteAsync(request, "wrapper", "agent_unavailable", null, null, "failed", error.Message);
            return Failure(request, WrapperExitCode.AgentUnavailable, "agent_unavailable", error.Message);
        }

        if (!prepareResponse.Ready)
        {
            await log.WriteAsync(request, "wrapper", "agent_rejected", null, null, "failed", prepareResponse.Error?.Message);
            return Failure(request, WrapperExitCode.AgentRejected, prepareResponse.Error?.Code ?? "agent_rejected", prepareResponse.Error?.Message ?? "Agent rejected request");
        }

        if (!string.IsNullOrWhiteSpace(prepareResponse.LaunchExecutablePath))
        {
            executablePath = Path.GetFullPath(prepareResponse.LaunchExecutablePath);
        }

        if (!string.IsNullOrWhiteSpace(prepareResponse.LaunchWorkingDirectory))
        {
            workingDirectory = Path.GetFullPath(prepareResponse.LaunchWorkingDirectory);
        }

        if (!File.Exists(executablePath))
        {
            await log.WriteAsync(request, "wrapper", "cache_executable_missing", null, executablePath, "failed", null);
            return Failure(request, WrapperExitCode.ExecutableNotFound, "cache_executable_not_found", executablePath);
        }

        if (!Directory.Exists(workingDirectory))
        {
            await log.WriteAsync(request, "wrapper", "cache_working_directory_missing", null, workingDirectory, "failed", null);
            return Failure(request, WrapperExitCode.InvalidRequest, "cache_working_directory_missing", workingDirectory);
        }

        await log.WriteAsync(request, "wrapper", "agent_ready", null, executablePath, "ready", prepareResponse.Message);

        try
        {
            var settings = ApplyPerGameLaunchModeOverride(request.GameId, VgcSettingsStore.LoadRequired(), options.DatabasePath);
            var launchPlan = new LaunchPreferenceResolver().Resolve(request.GameId, executablePath, workingDirectory, settings);
            ValidateLaunchPlan(launchPlan);
            await log.WriteAsync(
                request,
                "wrapper",
                "launch_mode_resolved",
                null,
                launchPlan.TrackedExecutablePath,
                launchPlan.Mode.ToString(),
                launchPlan.Message);

            var process = StartGameProcess(request, launchPlan, executablePath, workingDirectory, request.ExecutablePath, request.WorkingDirectory);
            var processId = process.Id;
            await log.WriteAsync(request, "wrapper", "game_started", processId, launchPlan.TrackedExecutablePath, "started", launchPlan.Message);
            await agentClient.UpdateRuntimeStateAsync(request, "Running", processId, cancellationToken);
            IReadOnlyList<int> observedProcessIds;
            try
            {
                var monitor = new ProcessMonitor();
                observedProcessIds = await monitor.WaitForExitTreeAsync(
                    process,
                    request.AdditionalProcessNames.Concat(new[] { Path.GetFileNameWithoutExtension(launchPlan.TrackedExecutablePath) }).ToArray(),
                    ReadExpectedProcessRoot(request.GameId, options.DatabasePath) ?? workingDirectory,
                    DateTimeOffset.UtcNow,
                    TimeSpan.FromSeconds(20),
                    TimeSpan.FromSeconds(2),
                    cancellationToken,
                    launchPlan.RestrictDescendantsToExpectedRoot,
                    launchPlan.AdditionalTrackingRoots);
            }
            finally
            {
                process.Dispose();
                await agentClient.UpdateRuntimeStateAsync(request, "Ready", processId, CancellationToken.None);
            }

            if (launchPlan.Mode == VgcLaunchMode.MTool && observedProcessIds.All(id => id == processId))
            {
                await log.WriteAsync(request, "wrapper", "mtool_game_process_not_observed", processId, launchPlan.TrackedExecutablePath, "failed", null);
                return Failure(
                    request,
                    WrapperExitCode.LaunchFailed,
                    "mtool_game_process_not_observed",
                    $"MTool 启动器已退出，但未观察到缓存目录内的游戏进程：{launchPlan.TrackedExecutablePath}");
            }

            await log.WriteAsync(request, "wrapper", "game_process_tree_exited", processId, launchPlan.TrackedExecutablePath, "success", launchPlan.Mode.ToString());

            var result = new LaunchResult
            {
                RequestId = request.RequestId,
                GameId = request.GameId,
                Success = true,
                InitialProcessId = processId,
                ObservedProcessIds = observedProcessIds,
                ExitCode = (int)WrapperExitCode.Success,
                Message = $"Launch chain completed with {launchPlan.Mode}; wrapper supervised the game process tree until exit"
            };
            return new WrapperRunResult((int)WrapperExitCode.Success, result);
        }
        catch (OperationCanceledException)
        {
            return Failure(request, WrapperExitCode.Cancelled, "cancelled", "Wrapper request was cancelled");
        }
        catch (LaunchPreferenceException error)
        {
            await log.WriteAsync(request, "wrapper", "launch_preference_failed", null, executablePath, "failed", error.Message);
            return Failure(request, WrapperExitCode.LaunchFailed, error.Code, error.Message);
        }
        catch (Exception error)
        {
            await log.WriteAsync(request, "wrapper", "launch_failed", null, executablePath, "failed", error.Message);
            return Failure(request, WrapperExitCode.LaunchFailed, "launch_failed", error.Message);
        }
    }

    private static Process StartGameProcess(
        LaunchRequest request,
        ResolvedLaunchPlan launchPlan,
        string cacheExecutablePath,
        string cacheWorkingDirectory,
        string sourceExecutablePath,
        string sourceWorkingDirectory)
    {
        var originalScript = TryReadOriginalScript(request.Arguments);
        if (launchPlan.UseOriginalScript && originalScript is { Count: > 0 })
        {
            return StartOriginalScript(originalScript, cacheExecutablePath, cacheWorkingDirectory, sourceExecutablePath, sourceWorkingDirectory);
        }

        var primaryArguments = launchPlan.UseRequestArguments
            ? request.Arguments.Where(argument => !IsOriginalScriptMarker(argument)).ToArray()
            : launchPlan.PrimaryProcess.Arguments;
        var startInfo = CreateStartInfo(launchPlan.PrimaryProcess, primaryArguments, request.Environment);
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null");
        foreach (var secondaryProcess in launchPlan.SecondaryProcesses)
        {
            using var secondary = Process.Start(CreateStartInfo(secondaryProcess, secondaryProcess.Arguments, request.Environment))
                                  ?? throw new InvalidOperationException($"Process.Start returned null for {secondaryProcess.FileName}");
        }

        return process;
    }

    private static ProcessStartInfo CreateStartInfo(
        LaunchProcessSpec processSpec,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = processSpec.FileName,
            WorkingDirectory = processSpec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = processSpec.CreateNoWindow
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var item in environment)
        {
            startInfo.Environment[item.Key] = item.Value;
        }

        return startInfo;
    }

    private static void ValidateLaunchPlan(ResolvedLaunchPlan launchPlan)
    {
        var processes = new[] { launchPlan.PrimaryProcess }.Concat(launchPlan.SecondaryProcesses);
        foreach (var process in processes)
        {
            if (!File.Exists(process.FileName))
            {
                throw new LaunchPreferenceException("launch_component_missing", $"启动组件不存在：{process.FileName}");
            }

            if (!Directory.Exists(process.WorkingDirectory))
            {
                throw new LaunchPreferenceException("launch_working_directory_missing", $"启动工作目录不存在：{process.WorkingDirectory}");
            }
        }

        if (!File.Exists(launchPlan.TrackedExecutablePath))
        {
            throw new LaunchPreferenceException("tracked_executable_missing", $"需要追踪的缓存游戏程序不存在：{launchPlan.TrackedExecutablePath}");
        }
    }

    private static IReadOnlyList<string>? TryReadOriginalScript(IReadOnlyList<string> arguments)
    {
        const string prefix = "__VGC_ORIGINAL_SCRIPT_B64=";
        var marker = arguments.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.Ordinal));
        if (marker is null)
        {
            return null;
        }

        var bytes = Convert.FromBase64String(marker[prefix.Length..]);
        return JsonSerializer.Deserialize<IReadOnlyList<string>>(Encoding.UTF8.GetString(bytes));
    }

    private static bool IsOriginalScriptMarker(string argument)
    {
        return argument.StartsWith("__VGC_ORIGINAL_SCRIPT_B64=", StringComparison.Ordinal);
    }

    private static Process StartOriginalScript(
        IReadOnlyList<string> originalCommands,
        string cacheExecutablePath,
        string cacheWorkingDirectory,
        string sourceExecutablePath,
        string sourceWorkingDirectory)
    {
        var commands = originalCommands
            .Select(command => RewriteCommand(command, sourceExecutablePath, cacheExecutablePath, sourceWorkingDirectory, cacheWorkingDirectory))
            .Where(command => !string.IsNullOrWhiteSpace(command))
            .ToArray();
        if (commands.Length == 0)
        {
            throw new InvalidOperationException("Original script command is empty.");
        }

        var commandLine = string.Join(" && ", new[]
        {
            "chcp 65001>nul",
            $"cd /d \"{cacheWorkingDirectory}\""
        }.Concat(commands));
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            WorkingDirectory = cacheWorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(commandLine);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null");
    }

    private static string RewriteCommand(
        string command,
        string sourceExecutablePath,
        string cacheExecutablePath,
        string sourceWorkingDirectory,
        string cacheWorkingDirectory)
    {
        return command
            .Replace(sourceExecutablePath, cacheExecutablePath, StringComparison.OrdinalIgnoreCase)
            .Replace(sourceWorkingDirectory, cacheWorkingDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadExpectedProcessRoot(string gameId, string? databasePath)
    {
        try
        {
            var options = string.IsNullOrWhiteSpace(databasePath)
                ? new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath())
                : new DatabaseOptions(databasePath);
            var factory = new SqliteConnectionFactory(options);
            new MigrationRunner(factory).Initialize();
            var repository = new GameRepository(factory);
            var game = repository.GetById(gameId) ?? repository.GetByVniteGameId(gameId);
            return string.IsNullOrWhiteSpace(game?.CacheRoot) ? null : Path.GetFullPath(game.CacheRoot);
        }
        catch
        {
            return null;
        }
    }

    private static VgcSettings ApplyPerGameLaunchModeOverride(string gameId, VgcSettings settings, string? databasePath)
    {
        try
        {
            var options = string.IsNullOrWhiteSpace(databasePath)
                ? new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath())
                : new DatabaseOptions(databasePath);
            var repository = new GameRepository(new SqliteConnectionFactory(options));
            var game = repository.GetById(gameId) ?? repository.GetByVniteGameId(gameId);
            return game?.LaunchModeOverride switch
            {
                GameLaunchModeOverride.Direct => settings with { DefaultLaunchMode = nameof(VgcLaunchMode.Direct) },
                GameLaunchModeOverride.TranslationPreferred => settings with { DefaultLaunchMode = nameof(VgcLaunchMode.TranslationPreferred) },
                GameLaunchModeOverride.MTool => settings with { DefaultLaunchMode = nameof(VgcLaunchMode.MTool) },
                _ => settings
            };
        }
        catch
        {
            return settings;
        }
    }

    private static WrapperRunResult Success(LaunchRequest request, string message, IReadOnlyList<int> observedProcessIds)
    {
        var result = new LaunchResult
        {
            RequestId = request.RequestId,
            GameId = request.GameId,
            Success = true,
            ObservedProcessIds = observedProcessIds,
            ExitCode = (int)WrapperExitCode.Success,
            Message = message
        };
        return new WrapperRunResult((int)WrapperExitCode.Success, result);
    }

    private static WrapperRunResult Failure(LaunchRequest request, WrapperExitCode exitCode, string code, string message)
    {
        var result = new LaunchResult
        {
            RequestId = request.RequestId,
            GameId = request.GameId,
            Success = false,
            ExitCode = (int)exitCode,
            Error = new ErrorResult
            {
                RequestId = request.RequestId,
                GameId = request.GameId,
                Code = code,
                Message = message
            }
        };
        return new WrapperRunResult((int)exitCode, result);
    }
}
