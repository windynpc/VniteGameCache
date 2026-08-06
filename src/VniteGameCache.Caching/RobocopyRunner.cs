using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VniteGameCache.Caching;

public interface IRobocopyRunner
{
    Task<RobocopyResult> RunAsync(
        RobocopyCommand command,
        CancellationToken cancellationToken,
        Action<RobocopyProgress>? progress = null);
}

public sealed class RobocopyRunner : IRobocopyRunner
{
    public async Task<RobocopyResult> RunAsync(
        RobocopyCommand command,
        CancellationToken cancellationToken,
        Action<RobocopyProgress>? progress = null)
    {
        var output = new List<string>();
        var outputLock = new object();
        var observedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? currentFile = null;
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "robocopy.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in command.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null) return;
            lock (outputLock)
            {
                output.Add(eventArgs.Data);
                var file = TryExtractSourceFile(eventArgs.Data, command.SourceRoot);
                if (file is not null)
                {
                    currentFile = file;
                    observedFiles.Add(file);
                }
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null) return;
            lock (outputLock) output.Add(eventArgs.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var monitorCancellation = new CancellationTokenSource();
        var monitorTask = progress is null
            ? Task.CompletedTask
            : MonitorIoProgressAsync(process, Snapshot, progress, monitorCancellation.Token);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            monitorCancellation.Cancel();
            await IgnoreCancellationAsync(monitorTask);
            return new RobocopyResult(process.ExitCode, Cancelled: false, SnapshotOutput());
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            monitorCancellation.Cancel();
            await IgnoreCancellationAsync(monitorTask);
            return new RobocopyResult(16, Cancelled: true, SnapshotOutput());
        }

        (int FilesObserved, string? CurrentFile) Snapshot()
        {
            lock (outputLock) return (observedFiles.Count, currentFile);
        }

        IReadOnlyList<string> SnapshotOutput()
        {
            lock (outputLock) return output.ToArray();
        }
    }

    private static async Task MonitorIoProgressAsync(
        Process process,
        Func<(int FilesObserved, string? CurrentFile)> snapshot,
        Action<RobocopyProgress> progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        ulong previousBytes = 0;
        var previousElapsed = TimeSpan.Zero;
        while (!cancellationToken.IsCancellationRequested)
        {
            long transferredBytes = 0;
            double bytesPerSecond = 0;
            try
            {
                if (OperatingSystem.IsWindows() && GetProcessIoCounters(process.Handle, out var counters))
                {
                    var currentBytes = counters.WriteTransferCount;
                    transferredBytes = currentBytes > long.MaxValue ? long.MaxValue : (long)currentBytes;
                    var elapsedDelta = stopwatch.Elapsed - previousElapsed;
                    if (elapsedDelta.TotalSeconds > 0 && currentBytes >= previousBytes)
                    {
                        bytesPerSecond = (currentBytes - previousBytes) / elapsedDelta.TotalSeconds;
                    }
                    previousBytes = currentBytes;
                    previousElapsed = stopwatch.Elapsed;
                }
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            var observed = snapshot();
            try
            {
                progress(new RobocopyProgress(transferredBytes, observed.FilesObserved, observed.CurrentFile, bytesPerSecond, stopwatch.Elapsed));
            }
            catch
            {
            }

            await Task.Delay(500, cancellationToken);
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string? TryExtractSourceFile(string line, string sourceRoot)
    {
        var index = line.IndexOf(sourceRoot, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var candidate = line[index..].Trim().Trim('"');
        return candidate.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out IoCounters ioCounters);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }
}

public static class RobocopyExitCodes
{
    public static bool IsSuccess(int exitCode) => exitCode is >= 0 and <= 7;
}
