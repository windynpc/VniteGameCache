using System.Diagnostics;

namespace VniteGameCache.LibraryImport;

public sealed class MToolValidator
{
    public async Task<MToolValidationResult> ValidateAsync(string? executablePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new MToolValidationResult(MToolCompatibilityStatus.NotConfigured, null, null, null, "MTool executable path is not configured.");
        }

        if (!File.Exists(executablePath))
        {
            return new MToolValidationResult(MToolCompatibilityStatus.ExecutableMissing, executablePath, null, null, "MTool executable path does not exist.");
        }

        foreach (var probe in new[] { "--help", "-h", "/?", "/help" })
        {
            var result = await ProbeAsync(executablePath, probe, cancellationToken);
            if (result.Output.Contains("usage", StringComparison.OrdinalIgnoreCase) ||
                result.Output.Contains("help", StringComparison.OrdinalIgnoreCase) ||
                result.Output.Contains("command", StringComparison.OrdinalIgnoreCase))
            {
                return new MToolValidationResult(MToolCompatibilityStatus.LaunchProtocolValidated, executablePath, probe, result.ExitCode, "Help output was detected. Automatic launch still requires a tested game-executable invocation before marking LaunchSucceeded.");
            }
        }

        return new MToolValidationResult(MToolCompatibilityStatus.LaunchProtocolUnknown, executablePath, null, null, "MTool executable exists, but no stable launch protocol was detected. Vnite Game Cache will not guess launch arguments.");
    }

    private static async Task<(int? ExitCode, string Output)> ProbeAsync(string executablePath, string argument, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, (await outputTask) + (await errorTask));
        }
        catch
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return (null, string.Empty);
        }
    }
}
