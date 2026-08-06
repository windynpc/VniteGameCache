using System.Security.Cryptography;
using System.Text.Json;
using VniteGameCache.Caching;
using VniteGameCache.Contracts;
using VniteGameCache.Indexing;
using VniteGameCache.LibraryImport;
using VniteGameCache.Persistence;

namespace VniteGameCache.Agent;

public static class AgentProgram
{
    public static async Task<int> MainAsync(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("index-game", StringComparison.OrdinalIgnoreCase))
        {
            return await IndexGameAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("prepare-cache", StringComparison.OrdinalIgnoreCase))
        {
            return await PrepareCacheCliAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("runtime-state", StringComparison.OrdinalIgnoreCase))
        {
            return await RuntimeStateCliAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("library-dry-run", StringComparison.OrdinalIgnoreCase))
        {
            return await LibraryDryRunCliAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("library-import", StringComparison.OrdinalIgnoreCase))
        {
            return await LibraryImportCliAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("game-upsert", StringComparison.OrdinalIgnoreCase))
        {
            return await GameUpsertCliAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("game-list", StringComparison.OrdinalIgnoreCase))
        {
            return await GameListCliAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("game-delete", StringComparison.OrdinalIgnoreCase))
        {
            return await GameDeleteCliAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("mtool-validate", StringComparison.OrdinalIgnoreCase))
        {
            return await MToolValidateCliAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("cache-lru-suggest", StringComparison.OrdinalIgnoreCase))
        {
            return await CacheLruSuggestCliAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("cache-lru-apply", StringComparison.OrdinalIgnoreCase))
        {
            return await CacheLruApplyCliAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("cache-quick-refresh", StringComparison.OrdinalIgnoreCase))
        {
            return await CacheQuickRefreshCliAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("cache-clean-rebuild", StringComparison.OrdinalIgnoreCase))
        {
            return await CacheCleanRebuildCliAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("cache-clear", StringComparison.OrdinalIgnoreCase))
        {
            return await CacheClearCliAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("quarantine-restore", StringComparison.OrdinalIgnoreCase))
        {
            return await QuarantineRestoreCliAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("quarantine-clean-expired", StringComparison.OrdinalIgnoreCase))
        {
            return await QuarantineCleanExpiredCliAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("save-profile-add", StringComparison.OrdinalIgnoreCase))
        {
            return await SaveProfileAddCliAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("save-profile-list", StringComparison.OrdinalIgnoreCase))
        {
            return await SaveProfileListCliAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("save-profile-validate", StringComparison.OrdinalIgnoreCase))
        {
            return await SaveProfileValidateCliAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("save-backup", StringComparison.OrdinalIgnoreCase))
        {
            return await SaveBackupCliAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("save-export", StringComparison.OrdinalIgnoreCase))
        {
            return await SaveExportCliAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("save-import", StringComparison.OrdinalIgnoreCase))
        {
            return await SaveImportCliAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("save-restore", StringComparison.OrdinalIgnoreCase))
        {
            return await SaveRestoreCliAsync(args);
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var line = await Console.In.ReadLineAsync(cancellation.Token);
        if (string.IsNullOrWhiteSpace(line))
        {
            return 10;
        }

        PrepareLaunchRequest? request;
        try
        {
            request = ProtocolJson.Deserialize<PrepareLaunchRequest>(line);
        }
        catch (Exception error)
        {
            await WriteErrorAsync(Guid.Empty, string.Empty, "invalid_json", error.Message);
            return 10;
        }

        if (request is null)
        {
            await WriteErrorAsync(Guid.Empty, string.Empty, "invalid_request", "Request body is empty");
            return 10;
        }

        TaskRepository? taskRepository = null;
        SqliteConnectionFactory? factory = null;
        try
        {
            var options = new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath());
            factory = new SqliteConnectionFactory(options);
            new MigrationRunner(factory).Initialize();
            RecoverStaleActiveTasks(factory);
            taskRepository = new TaskRepository(factory);
        }
        catch (Exception error)
        {
            await WriteErrorAsync(request.RequestId, request.GameId, "database_init_failed", error.Message);
            return 20;
        }

        var validationError = LaunchRequestValidator.Validate(request);
        if (validationError is not null)
        {
            await WriteErrorAsync(request.RequestId, request.GameId, "invalid_request", validationError);
            return 10;
        }

        var taskId = $"prepare-launch-{request.RequestId}";
        try
        {
            var trackedGame = factory is null ? null : new GameRepository(factory).GetById(request.GameId);
            taskRepository?.Create(new TaskRecord
            {
                TaskId = taskId,
                GameId = trackedGame?.GameId,
                TaskType = "PrepareLaunch",
                State = TaskState.Checking,
                StartedUtc = DateTimeOffset.UtcNow,
                RecoveryPayloadJson = ProtocolJson.Serialize(request)
            });
        }
        catch (Exception error)
        {
            await WriteErrorAsync(request.RequestId, request.GameId, "task_record_failed", error.Message);
            return 20;
        }

        if (Environment.GetEnvironmentVariable("VGC_AGENT_SIMULATE_FAILURE") == "1")
        {
            taskRepository?.UpdateState(taskId, TaskState.Failed, "simulated_failure", "Agent simulated failure");
            await WriteErrorAsync(request.RequestId, request.GameId, "simulated_failure", "Agent simulated failure");
            return 21;
        }

        try
        {
            var game = factory is null ? null : new GameRepository(factory).GetById(request.GameId);
            if (game is not null)
            {
                var indexError = EnsureFreshIndexForPrepareLaunch(factory!, game, cancellation.Token);
                if (indexError is not null)
                {
                    taskRepository?.UpdateState(taskId, TaskState.Failed, indexError.Value.Code, indexError.Value.Message);
                    await WriteErrorAsync(request.RequestId, request.GameId, indexError.Value.Code, indexError.Value.Message);
                    return 21;
                }

                var cache = CreateCacheService(factory!, ReadMinimumFreeSpace());
                var cacheResult = await cache.PrepareCacheAsync(new CacheRequest(request.RequestId, request.GameId), cancellation.Token);
                if (cacheResult.Status is not (CacheResultStatus.Success or CacheResultStatus.CacheAlreadyReady))
                {
                    var errorCode = cacheResult.ErrorCode ?? cacheResult.Status.ToString();
                    var errorMessage = cacheResult.ErrorMessage ?? "Cache prepare failed";
                    taskRepository?.UpdateState(taskId, TaskState.Failed, errorCode, errorMessage);
                    await WriteErrorAsync(request.RequestId, request.GameId, errorCode, errorMessage);
                    return 21;
                }

                var gameRepository = new GameRepository(factory!);
                var latestGame = gameRepository.GetById(game.GameId) ?? game;
                gameRepository.Update(latestGame with { LastPlayedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow });

                var cachedResponse = new PrepareLaunchResponse
                {
                    RequestId = request.RequestId,
                    GameId = request.GameId,
                    Ready = true,
                    Message = cacheResult.Status.ToString(),
                    LaunchExecutablePath = cacheResult.LaunchExecutablePath,
                    LaunchWorkingDirectory = cacheResult.LaunchWorkingDirectory
                };
                taskRepository?.UpdateState(taskId, TaskState.Completed);
                await Console.Out.WriteLineAsync(ProtocolJson.Serialize(cachedResponse));
                return 0;
            }

            var delayMs = RandomNumberGenerator.GetInt32(500, 1501);
            await Task.Delay(delayMs, cancellation.Token);
            var response = new PrepareLaunchResponse
            {
                RequestId = request.RequestId,
                GameId = request.GameId,
                Ready = true,
                Message = $"Ready after {delayMs} ms"
            };
            taskRepository?.UpdateState(taskId, TaskState.Completed);
            await Console.Out.WriteLineAsync(ProtocolJson.Serialize(response));
            return 0;
        }
        catch (OperationCanceledException)
        {
            taskRepository?.UpdateState(taskId, TaskState.Cancelled, "cancelled", "Agent request was cancelled");
            await WriteErrorAsync(request.RequestId, request.GameId, "cancelled", "Agent request was cancelled");
            return 50;
        }
        catch (Exception error)
        {
            taskRepository?.UpdateState(taskId, TaskState.Failed, "unexpected_error", error.Message);
            await WriteErrorAsync(request.RequestId, request.GameId, "unexpected_error", error.Message);
            return 21;
        }
    }

    private static (string Code, string Message)? EnsureFreshIndexForPrepareLaunch(
        SqliteConnectionFactory factory,
        GameRecord game,
        CancellationToken cancellationToken)
    {
        var files = new FileEntryRepository(factory).ListByGame(game.GameId);
        var requiresIndex = files.Count == 0;
        if (!requiresIndex && game.CacheStatus == CacheStatus.Ready && game.SourceRevision == game.CacheRevision)
        {
            var directories = new DirectoryFingerprintRepository(factory).ListByGame(game.GameId);
            var probe = new SourceQuickProbe().Probe(game, files, directories);
            requiresIndex = probe.Status is SourceQuickProbeStatus.Changed or SourceQuickProbeStatus.InsufficientBaseline;
            if (probe.Status == SourceQuickProbeStatus.Unavailable)
            {
                return null;
            }
        }

        if (!requiresIndex)
        {
            return null;
        }

        var manifestRoot = Environment.GetEnvironmentVariable("VGC_MANIFEST_ROOT") ??
            IndexingOptions.Default().ManifestRoot;
        var indexingOptions = IndexingOptions.Default() with { ManifestRoot = manifestRoot };
        var service = new IndexingService(
            new IndexingStore(factory),
            new FileSystemScanner(),
            new ManifestBuilder(),
            indexingOptions);
        var result = service.IndexGame(game.GameId, null, cancellationToken);
        return result.Status switch
        {
            ManifestStatus.Complete or ManifestStatus.CompleteWithWarnings => null,
            _ => ("index_failed", result.ErrorMessage ?? $"Indexing failed: {result.Status}")
        };
    }

    private static async Task<int> IndexGameAsync(string[] args)
    {
        var gameId = ReadOption(args, "--game-id");
        if (string.IsNullOrWhiteSpace(gameId))
        {
            await Console.Error.WriteLineAsync("Missing --game-id.");
            return 10;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        if (int.TryParse(ReadOption(args, "--cancel-after-ms"), out var cancelAfterMs) && cancelAfterMs >= 0)
        {
            cancellation.CancelAfter(cancelAfterMs);
        }

        var databasePath = ReadOption(args, "--db-path") ?? VgcSettingsStore.ResolveDatabasePath();
        var options = new DatabaseOptions(databasePath);
        var factory = new SqliteConnectionFactory(options);
        new MigrationRunner(factory).Initialize();
        RecoverStaleActiveTasks(factory);

        var manifestRoot = ReadOption(args, "--manifest-root") ??
            Environment.GetEnvironmentVariable("VGC_MANIFEST_ROOT") ??
            IndexingOptions.Default().ManifestRoot;
        var indexingOptions = IndexingOptions.Default() with { ManifestRoot = manifestRoot };
        var service = new IndexingService(
            new IndexingStore(factory),
            new FileSystemScanner(),
            new ManifestBuilder(),
            indexingOptions);

        var progress = new Progress<IndexingProgress>(value =>
        {
            var json = JsonSerializer.Serialize(new
            {
                event_name = "index_progress",
                phase = value.Phase,
                current_directory = value.CurrentDirectory,
                file_count = value.FileCount,
                directory_count = value.DirectoryCount,
                processed_bytes = value.ProcessedBytes,
                warning_count = value.WarningCount
            });
            Console.Error.WriteLine(json);
        });

        var result = service.IndexGame(gameId, progress, cancellation.Token);
        var resultJsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        resultJsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result, resultJsonOptions));
        return result.Status switch
        {
            ManifestStatus.Complete or ManifestStatus.CompleteWithWarnings => 0,
            ManifestStatus.Cancelled => 50,
            _ => 20
        };
    }

    private static async Task<int> PrepareCacheCliAsync(string[] args)
    {
        var gameId = ReadOption(args, "--game-id");
        if (string.IsNullOrWhiteSpace(gameId))
        {
            await Console.Error.WriteLineAsync("Missing --game-id.");
            return 10;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        if (int.TryParse(ReadOption(args, "--cancel-after-ms"), out var cancelAfterMs) && cancelAfterMs >= 0)
        {
            cancellation.CancelAfter(cancelAfterMs);
        }

        var databasePath = ReadOption(args, "--db-path") ?? VgcSettingsStore.ResolveDatabasePath();
        var options = new DatabaseOptions(databasePath);
        var factory = new SqliteConnectionFactory(options);
        new MigrationRunner(factory).Initialize();
        RecoverStaleActiveTasks(factory);

        var forceRefresh = args.Any(arg => arg.Equals("--force-refresh", StringComparison.OrdinalIgnoreCase));
        var verificationText = ReadOption(args, "--verification") ?? "Fast";
        if (!Enum.TryParse<VerificationPolicy>(verificationText, ignoreCase: false, out var verification))
        {
            await Console.Error.WriteLineAsync($"Invalid --verification: {verificationText}");
            return 10;
        }

        var minimumFreeSpace = long.TryParse(ReadOption(args, "--minimum-free-space-bytes"), out var parsedMinimum)
            ? parsedMinimum
            : ReadMinimumFreeSpace();
        var service = CreateCacheService(factory, minimumFreeSpace);
        var game = new GameRepository(factory).GetById(gameId);
        if (game is not null)
        {
            var indexError = EnsureFreshIndexForPrepareLaunch(factory, game, cancellation.Token);
            if (indexError is not null)
            {
                await Console.Error.WriteLineAsync(indexError.Value.Message);
                return 20;
            }
        }
        var result = await service.PrepareCacheAsync(
            new CacheRequest(Guid.NewGuid(), gameId, forceRefresh, verification),
            cancellation.Token);
        var resultJsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        resultJsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result, resultJsonOptions));
        return result.Status switch
        {
            CacheResultStatus.Success or CacheResultStatus.CacheAlreadyReady => 0,
            CacheResultStatus.Cancelled => 50,
            CacheResultStatus.GameNotFound or CacheResultStatus.IndexMissing => 10,
            _ => 20
        };
    }

    private static async Task<int> RuntimeStateCliAsync(string[] args)
    {
        var gameId = RequireOption(args, "--game-id");
        var stateText = RequireOption(args, "--state");
        if (!Enum.TryParse<CacheStatus>(stateText, ignoreCase: false, out var status) ||
            status is not (CacheStatus.Running or CacheStatus.Ready))
        {
            await Console.Error.WriteLineAsync($"Invalid --state: {stateText}");
            return 10;
        }

        var factory = OpenFactory(args);
        var repository = new GameRepository(factory);
        var game = repository.GetById(gameId);
        if (game is null)
        {
            await WriteJsonAsync(new { status = "game_not_found", game_id = gameId });
            return 10;
        }

        var now = DateTimeOffset.UtcNow;
        repository.Update(game with
        {
            CacheStatus = status,
            LastPlayedUtc = status == CacheStatus.Running ? now : game.LastPlayedUtc,
            UpdatedUtc = now
        });
        await WriteJsonAsync(new
        {
            status = "updated",
            game_id = game.GameId,
            cache_status = status.ToString(),
            process_id = ReadOption(args, "--process-id")
        });
        return 0;
    }

    private static async Task<int> LibraryDryRunCliAsync(string[] args)
    {
        var config = ReadLibraryConfig(args);
        var factory = OpenFactory(args);
        var result = new LibraryDiscoveryService(new GameRepository(factory)).Discover(config);
        await WriteJsonAsync(result);
        return 0;
    }

    private static async Task<int> LibraryImportCliAsync(string[] args)
    {
        var config = ReadLibraryConfig(args);
        var factory = OpenFactory(args);
        var games = new GameRepository(factory);
        var dryRun = new LibraryDiscoveryService(games).Discover(config);
        var importNeedsConfirmation = args.Any(arg => arg.Equals("--import-needs-confirmation", StringComparison.OrdinalIgnoreCase));
        var import = new LibraryImportService(games).Import(config, dryRun.Candidates, importNeedsConfirmation);
        await WriteJsonAsync(import);
        return import.ImportedGames.Count > 0 ? 0 : 20;
    }

    private static async Task<int> GameUpsertCliAsync(string[] args)
    {
        var gameId = RequireOption(args, "--game-id");
        var sourceRoot = RequireOption(args, "--source-root");
        var cacheRoot = RequireOption(args, "--cache-root");
        var executablePath = RequireOption(args, "--executable-path");
        var displayName = ReadOption(args, "--display-name") ?? Path.GetFileName(sourceRoot);
        var originalScriptJson = ReadOption(args, "--original-script-json");
        var launchModeText = ReadOption(args, "--launch-mode-override");
        var now = DateTimeOffset.UtcNow;
        var factory = OpenFactory(args);
        var games = new GameRepository(factory);
        var existing = games.GetById(gameId) ?? games.GetByVniteGameId(gameId);
        var argumentsJson = string.IsNullOrWhiteSpace(originalScriptJson)
            ? existing?.ArgumentsJson ?? "[]"
            : CreateOriginalScriptArgumentsJson(originalScriptJson);
        var workingDirectory = ResolveUpsertWorkingDirectory(existing, sourceRoot);
        var record = new GameRecord
        {
            GameId = existing?.GameId ?? gameId,
            VniteGameId = gameId,
            DisplayName = existing?.DisplayName ?? displayName,
            SourceRoot = sourceRoot,
            CacheRoot = existing?.CacheRoot is { Length: > 0 } ? existing.CacheRoot : cacheRoot,
            ExecutablePath = executablePath,
            ArgumentsJson = argumentsJson,
            WorkingDirectory = workingDirectory,
            LaunchPolicy = existing?.LaunchPolicy ?? LaunchPolicy.FastLaunch,
            CachePolicy = existing?.CachePolicy ?? CachePolicy.CacheBeforeLaunch,
            VerificationPolicy = existing?.VerificationPolicy ?? VerificationPolicy.Fast,
            SourceRevision = existing?.SourceRevision ?? 0,
            CacheRevision = existing?.CacheRevision ?? 0,
            LastFullScanUtc = existing?.LastFullScanUtc,
            LastIncrementalScanUtc = existing?.LastIncrementalScanUtc,
            LastPlayedUtc = existing?.LastPlayedUtc,
            CacheSizeBytes = existing?.CacheSizeBytes ?? 0,
            IsPinned = existing?.IsPinned ?? false,
            CacheStatus = existing?.CacheStatus ?? CacheStatus.NotCached,
            ContainsPossibleSaveData = existing?.ContainsPossibleSaveData ?? false,
            PendingManualSaveBackup = existing?.PendingManualSaveBackup ?? false,
            LastCacheRefreshUtc = existing?.LastCacheRefreshUtc,
            ManagementIntent = existing?.ManagementIntent ?? ManagementIntent.Managed,
            LaunchModeOverride = ParseLaunchModeOverride(launchModeText, existing?.LaunchModeOverride ?? GameLaunchModeOverride.Inherit),
            ExecutableRelativePath = TryMakeRelativePath(sourceRoot, executablePath),
            WorkingDirectoryRelativePath = TryMakeRelativePath(sourceRoot, workingDirectory),
            CreatedUtc = existing?.CreatedUtc ?? now,
            UpdatedUtc = now
        };
        games.Upsert(record);
        await WriteJsonAsync(new
        {
            game_id = record.GameId,
            source_root = record.SourceRoot,
            cache_root = record.CacheRoot,
            executable_path = record.ExecutablePath,
            executable_relative_path = record.ExecutableRelativePath,
            working_directory_relative_path = record.WorkingDirectoryRelativePath,
            management_intent = record.ManagementIntent.ToString(),
            launch_mode_override = record.LaunchModeOverride.ToString(),
            success = true
        });
        return 0;
    }

    private static async Task<int> GameListCliAsync(string[] args)
    {
        var factory = OpenFactory(args);
        var records = new GameRepository(factory)
            .List()
            .Select(game => new
            {
                game_id = game.GameId,
                vnite_game_id = game.VniteGameId,
                display_name = game.DisplayName,
                source_root = game.SourceRoot,
                cache_root = game.CacheRoot,
                executable_path = game.ExecutablePath
            })
            .ToList();
        await WriteJsonAsync(new
        {
            success = true,
            count = records.Count,
            games = records
        });
        return 0;
    }

    private static async Task<int> GameDeleteCliAsync(string[] args)
    {
        var gameId = RequireOption(args, "--game-id");
        var factory = OpenFactory(args);
        var repository = new GameRepository(factory);
        var record = repository.GetById(gameId) ?? repository.GetByVniteGameId(gameId);
        if (record is null)
        {
            await WriteJsonAsync(new { success = true, deleted = false, game_id = gameId, status = "not_found" });
            return 0;
        }

        repository.Delete(record.GameId);
        await WriteJsonAsync(new
        {
            success = true,
            deleted = true,
            game_id = record.GameId,
            vnite_game_id = record.VniteGameId
        });
        return 0;
    }

    private static GameLaunchModeOverride ParseLaunchModeOverride(string? value, GameLaunchModeOverride fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return Enum.TryParse<GameLaunchModeOverride>(value, ignoreCase: false, out var parsed) ? parsed : fallback;
    }

    private static string? TryMakeRelativePath(string root, string value)
    {
        try
        {
            var normalizedRoot = NormalizePath(root);
            var normalizedValue = Path.GetFullPath(value);
            var relative = Path.GetRelativePath(normalizedRoot, normalizedValue);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return null;
            return relative.Replace(Path.DirectorySeparatorChar, '/');
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveUpsertWorkingDirectory(GameRecord? existing, string sourceRoot)
    {
        if (existing is null || string.IsNullOrWhiteSpace(existing.WorkingDirectory))
        {
            return sourceRoot;
        }

        if (PathEquals(existing.SourceRoot, sourceRoot))
        {
            return existing.WorkingDirectory;
        }

        if (IsSameOrChildPath(existing.WorkingDirectory, existing.SourceRoot))
        {
            var relative = Path.GetRelativePath(existing.SourceRoot, existing.WorkingDirectory);
            if (relative == ".")
            {
                return sourceRoot;
            }

            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            {
                return Path.GetFullPath(Path.Combine(sourceRoot, relative));
            }
        }

        return sourceRoot;
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrChildPath(string value, string root)
    {
        var normalizedValue = NormalizePath(value);
        var normalizedRoot = NormalizePath(root);
        return normalizedValue.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedValue.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string CreateOriginalScriptArgumentsJson(string originalScriptJson)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(originalScriptJson));
        return JsonSerializer.Serialize(new[] { $"__VGC_ORIGINAL_SCRIPT_B64={encoded}" });
    }

    private static async Task<int> MToolValidateCliAsync(string[] args)
    {
        var path = ReadOption(args, "--mtool-path");
        var result = await new MToolValidator().ValidateAsync(path);
        await WriteJsonAsync(result);
        return result.Status == MToolCompatibilityStatus.ExecutableMissing ? 20 : 0;
    }

    private static async Task<int> CacheLruSuggestCliAsync(string[] args)
    {
        var factory = OpenFactory(args);
        var maxBytes = long.TryParse(ReadOption(args, "--max-total-cache-bytes"), out var parsedMax) ? parsedMax : (long?)null;
        var minFree = long.TryParse(ReadOption(args, "--minimum-free-space-bytes"), out var parsedFree) ? parsedFree : (long?)null;
        var service = CreateCacheManagementService(factory, ReadMinimumFreeSpace(args));
        service.CleanupExpiredQuarantine(ReadQuarantineRetentionDays(args));
        var result = service.PlanCleanup(new CacheCleanupOptions(maxBytes, minFree));
        await WriteJsonAsync(new { auto_apply = false, candidates = result.Candidates, result });
        return 0;
    }

    private static async Task<int> CacheLruApplyCliAsync(string[] args)
    {
        var factory = OpenFactory(args);
        var maxBytes = long.TryParse(ReadOption(args, "--max-total-cache-bytes"), out var parsedMax) ? parsedMax : (long?)null;
        var minFree = long.TryParse(ReadOption(args, "--minimum-free-space-bytes"), out var parsedFree) ? parsedFree : (long?)null;
        var maxItems = int.TryParse(ReadOption(args, "--max-items"), out var parsedMaxItems) ? parsedMaxItems : int.MaxValue;
        var service = CreateCacheManagementService(factory, ReadMinimumFreeSpace(args));
        var expired = service.CleanupExpiredQuarantine(ReadQuarantineRetentionDays(args));
        var result = service.ApplyLruCleanup(new CacheCleanupOptions(maxBytes, minFree, AutoApply: true), maxItems);
        await WriteJsonAsync(new { expired_quarantine = expired, result });
        return result.Status is "cleaned" or "partial" or "not_needed" ? 0 : 20;
    }

    private static async Task<int> CacheQuickRefreshCliAsync(string[] args)
    {
        var gameId = RequireOption(args, "--game-id");
        var factory = OpenFactory(args);
        var result = await CreateCacheManagementService(factory, ReadMinimumFreeSpace(args)).QuickRefreshAsync(gameId);
        await WriteJsonAsync(result);
        return result.Status is CacheResultStatus.Success or CacheResultStatus.CacheAlreadyReady ? 0 : 20;
    }

    private static async Task<int> CacheCleanRebuildCliAsync(string[] args)
    {
        var gameId = RequireOption(args, "--game-id");
        var factory = OpenFactory(args);
        var result = await CreateCacheManagementService(factory, ReadMinimumFreeSpace(args)).CleanRebuildAsync(gameId);
        await WriteJsonAsync(result);
        return result.Cleared ? 0 : 20;
    }

    private static async Task<int> CacheClearCliAsync(string[] args)
    {
        var gameId = RequireOption(args, "--game-id");
        var factory = OpenFactory(args);
        var result = CreateCacheManagementService(factory, ReadMinimumFreeSpace(args)).ClearCache(new CacheClearRequest(
            gameId,
            args.Any(arg => arg.Equals("--backup-saves", StringComparison.OrdinalIgnoreCase)),
            args.Any(arg => arg.Equals("--keep-user-data", StringComparison.OrdinalIgnoreCase)),
            args.Any(arg => arg.Equals("--force", StringComparison.OrdinalIgnoreCase)),
            ReadOption(args, "--reason") ?? "manual_clear"));
        await WriteJsonAsync(result);
        return result.Cleared || result.Status == "cache_missing" ? 0 : 20;
    }

    private static async Task<int> QuarantineRestoreCliAsync(string[] args)
    {
        var quarantineId = RequireOption(args, "--quarantine-id");
        var factory = OpenFactory(args);
        var result = CreateCacheManagementService(factory, ReadMinimumFreeSpace(args)).RestoreQuarantine(quarantineId);
        await WriteJsonAsync(result);
        return 0;
    }

    private static async Task<int> QuarantineCleanExpiredCliAsync(string[] args)
    {
        var factory = OpenFactory(args);
        var result = CreateCacheManagementService(factory, ReadMinimumFreeSpace(args)).CleanupExpiredQuarantine(ReadQuarantineRetentionDays(args));
        await WriteJsonAsync(result);
        return result.FailedCount == 0 ? 0 : 20;
    }

    private static async Task<int> SaveProfileAddCliAsync(string[] args)
    {
        var gameId = RequireOption(args, "--game-id");
        var displayName = RequireOption(args, "--display-name");
        var sourceTypeText = RequireOption(args, "--source-type");
        var pathTemplate = RequireOption(args, "--path-template");
        if (!Enum.TryParse<SaveProfileSourceType>(sourceTypeText, ignoreCase: false, out var sourceType))
        {
            await Console.Error.WriteLineAsync($"Invalid --source-type: {sourceTypeText}");
            return 10;
        }

        var factory = OpenFactory(args);
        var result = new SaveProfileService(factory).AddOrUpdate(new SaveProfileCreateRequest(
            gameId,
            displayName,
            sourceType,
            pathTemplate,
            ReadOption(args, "--backup-root"),
            args.Any(arg => arg.Equals("--primary", StringComparison.OrdinalIgnoreCase))));
        await WriteJsonAsync(result);
        return 0;
    }

    private static async Task<int> SaveProfileListCliAsync(string[] args)
    {
        var gameId = RequireOption(args, "--game-id");
        var factory = OpenFactory(args);
        await WriteJsonAsync(new SaveProfileService(factory).List(gameId));
        return 0;
    }

    private static async Task<int> SaveProfileValidateCliAsync(string[] args)
    {
        var saveProfileId = RequireOption(args, "--profile-id");
        var factory = OpenFactory(args);
        await WriteJsonAsync(new SaveProfileService(factory).Validate(saveProfileId));
        return 0;
    }

    private static async Task<int> SaveBackupCliAsync(string[] args)
    {
        var saveProfileId = RequireOption(args, "--profile-id");
        var factory = OpenFactory(args);
        await WriteJsonAsync(new SaveProfileService(factory).Backup(saveProfileId));
        return 0;
    }

    private static async Task<int> SaveExportCliAsync(string[] args)
    {
        var saveProfileId = RequireOption(args, "--profile-id");
        var outputPath = RequireOption(args, "--output");
        var factory = OpenFactory(args);
        await WriteJsonAsync(new SaveProfileService(factory).Export(saveProfileId, outputPath));
        return 0;
    }

    private static async Task<int> SaveImportCliAsync(string[] args)
    {
        var saveProfileId = RequireOption(args, "--profile-id");
        var inputPath = RequireOption(args, "--input");
        var factory = OpenFactory(args);
        await WriteJsonAsync(new SaveProfileService(factory).Import(saveProfileId, inputPath));
        return 0;
    }

    private static async Task<int> SaveRestoreCliAsync(string[] args)
    {
        var backupId = RequireOption(args, "--backup-id");
        var factory = OpenFactory(args);
        await WriteJsonAsync(new SaveProfileService(factory).Restore(backupId));
        return 0;
    }

    private static SqliteConnectionFactory OpenFactory(IReadOnlyList<string> args)
    {
        var databasePath = ReadOption(args, "--db-path") ?? VgcSettingsStore.ResolveDatabasePath();
        var options = new DatabaseOptions(databasePath);
        var factory = new SqliteConnectionFactory(options);
        new MigrationRunner(factory).Initialize();
        RecoverStaleActiveTasks(factory);
        return factory;
    }

    private static void RecoverStaleActiveTasks(SqliteConnectionFactory factory)
    {
        var staleBeforeUtc = DateTimeOffset.UtcNow - ReadStaleActiveTaskAge();
        var recovered = new TaskRepository(factory).MarkStaleActiveTasksInterrupted(staleBeforeUtc);
        if (recovered > 0)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                event_name = "stale_active_tasks_recovered",
                recovered_count = recovered,
                stale_before_utc = staleBeforeUtc
            }));
        }
    }

    private static TimeSpan ReadStaleActiveTaskAge()
    {
        return int.TryParse(Environment.GetEnvironmentVariable("VGC_STALE_ACTIVE_TASK_MINUTES"), out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : TimeSpan.FromHours(6);
    }

    private static LibraryImportConfig ReadLibraryConfig(IReadOnlyList<string> args)
    {
        var libraryRoot = ReadOption(args, "--library-root");
        var cacheRoot = ReadOption(args, "--cache-root");
        if (string.IsNullOrWhiteSpace(libraryRoot)) throw new ArgumentException("Missing --library-root.");
        if (string.IsNullOrWhiteSpace(cacheRoot)) throw new ArgumentException("Missing --cache-root.");
        var launchModeText = ReadOption(args, "--launch-mode") ?? "Direct";
        if (!Enum.TryParse<LaunchMode>(launchModeText, ignoreCase: false, out var launchMode))
        {
            throw new ArgumentException($"Invalid --launch-mode: {launchModeText}");
        }

        return new LibraryImportConfig(
            [libraryRoot],
            cacheRoot,
            launchMode,
            ReadOption(args, "--mtool-path"),
            int.TryParse(ReadOption(args, "--scan-depth"), out var scanDepth) ? scanDepth : 3);
    }

    private static async Task WriteJsonAsync<T>(T result)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result, options));
    }

    private static CacheService CreateCacheService(SqliteConnectionFactory factory, long minimumFreeSpaceBytes)
    {
        var root = Path.Combine(
            VgcSettingsStore.ResolveLogRoot(),
            "robocopy");
        return new CacheService(
            new CacheStore(factory),
            new CopyPlanner(),
            new RobocopyCommandBuilder(root),
            new RobocopyRunner(),
            new CacheVerifier(),
            minimumFreeSpaceBytes);
    }

    private static CacheManagementService CreateCacheManagementService(SqliteConnectionFactory factory, long minimumFreeSpaceBytes) =>
        new(factory, CreateCacheService(factory, minimumFreeSpaceBytes), VgcSettingsStore.LoadRequired().ProtectConfigFiles);

    private static long ReadMinimumFreeSpace()
    {
        var raw = Environment.GetEnvironmentVariable("VGC_MIN_FREE_BYTES");
        if (long.TryParse(raw, out var parsed)) return parsed;
        return VgcSettingsStore.ResolveMinimumFreeSpaceBytes() ?? CachePathSafety.DefaultMinimumFreeSpaceBytes;
    }

    private static long ReadMinimumFreeSpace(IReadOnlyList<string> args) =>
        long.TryParse(ReadOption(args, "--minimum-free-space-bytes"), out var parsed) ? parsed : ReadMinimumFreeSpace();

    private static int ReadQuarantineRetentionDays(IReadOnlyList<string> args)
    {
        if (int.TryParse(ReadOption(args, "--quarantine-retention-days"), out var parsed))
        {
            return Math.Max(0, parsed);
        }

        return Math.Max(0, VgcSettingsStore.LoadRequired().QuarantineRetentionDays ?? 30);
    }

    private static string? ReadOption(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        }
        return null;
    }

    private static string RequireOption(IReadOnlyList<string> args, string name) =>
        ReadOption(args, name) ?? throw new ArgumentException($"Missing {name}.");

    public static async Task WriteErrorAsync(Guid requestId, string gameId, string code, string message)
    {
        var response = new PrepareLaunchResponse
        {
            RequestId = requestId,
            GameId = gameId,
            Ready = false,
            Error = new ErrorResult
            {
                RequestId = requestId,
                GameId = gameId,
                Code = code,
                Message = message
            }
        };
        await Console.Out.WriteLineAsync(ProtocolJson.Serialize(response));
    }
}
