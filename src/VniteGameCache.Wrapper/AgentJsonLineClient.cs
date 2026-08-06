using System.Diagnostics;
using VniteGameCache.Contracts;

namespace VniteGameCache.Wrapper;

public sealed class AgentJsonLineClient(string? agentPath, string? dotnetPath, string? databasePath, StructuredLogger logger)
{
    public async Task UpdateRuntimeStateAsync(
        LaunchRequest launchRequest,
        string state,
        int? processId,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolvedAgentPath = ResolveAgentPath(agentPath);
            var args = new List<string> { "runtime-state", "--game-id", launchRequest.GameId, "--state", state };
            if (processId is not null)
            {
                args.Add("--process-id");
                args.Add(processId.Value.ToString());
            }

            var startInfo = CreateStartInfo(resolvedAgentPath, dotnetPath, args);
            if (!string.IsNullOrWhiteSpace(databasePath))
            {
                startInfo.Environment["VGC_DB_PATH"] = databasePath;
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Agent");
            await process.WaitForExitAsync(cancellationToken);
            await logger.WriteAsync(launchRequest, "wrapper", "runtime_state_update", processId, resolvedAgentPath, process.ExitCode == 0 ? "success" : "failed", state);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await logger.WriteAsync(launchRequest, "wrapper", "runtime_state_update_failed", processId, null, "failed", error.Message);
        }
    }

    public async Task<PrepareLaunchResponse> PrepareAsync(
        LaunchRequest launchRequest,
        CancellationToken cancellationToken)
    {
        var resolvedAgentPath = ResolveAgentPath(agentPath);
        var startInfo = CreateStartInfo(resolvedAgentPath, dotnetPath);
        if (!string.IsNullOrWhiteSpace(databasePath))
        {
            startInfo.Environment["VGC_DB_PATH"] = databasePath;
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Agent");

        var prepareRequest = new PrepareLaunchRequest
        {
            RequestId = launchRequest.RequestId,
            GameId = launchRequest.GameId,
            ExecutablePath = launchRequest.ExecutablePath,
            WorkingDirectory = launchRequest.WorkingDirectory
        };

        await logger.WriteAsync(launchRequest, "wrapper", "agent_started", process.Id, resolvedAgentPath, "started", null);
        await process.StandardInput.WriteLineAsync(ProtocolJson.Serialize(prepareRequest));
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();

        var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(line))
        {
            throw new InvalidOperationException($"Agent produced no response. ExitCode={process.ExitCode}");
        }

        var response = ProtocolJson.Deserialize<PrepareLaunchResponse>(line)
                       ?? throw new InvalidOperationException("Agent response could not be parsed");

        if (process.ExitCode != 0 && response.Ready)
        {
            throw new InvalidOperationException($"Agent exited with {process.ExitCode} after Ready response");
        }

        return response;
    }

    private static string ResolveAgentPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var envPath = Environment.GetEnvironmentVariable("VGC_AGENT_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return Path.GetFullPath(envPath);
        }

        var baseDirectory = AppContext.BaseDirectory;
        var exePath = Path.Combine(baseDirectory, "VniteGameCache.Agent.exe");
        if (File.Exists(exePath))
        {
            return exePath;
        }

        var dllPath = Path.Combine(baseDirectory, "VniteGameCache.Agent.dll");
        if (File.Exists(dllPath))
        {
            return dllPath;
        }

        throw new FileNotFoundException("Agent path was not configured and no colocated Agent binary was found");
    }

    private static ProcessStartInfo CreateStartInfo(string resolvedAgentPath, string? configuredDotnetPath, IReadOnlyList<string>? extraArgs = null)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        if (Path.GetExtension(resolvedAgentPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = configuredDotnetPath
                                 ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
                                 ?? "dotnet";
            startInfo.ArgumentList.Add(resolvedAgentPath);
        }
        else
        {
            startInfo.FileName = resolvedAgentPath;
        }

        if (extraArgs is not null)
        {
            foreach (var arg in extraArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }
        }

        return startInfo;
    }
}
