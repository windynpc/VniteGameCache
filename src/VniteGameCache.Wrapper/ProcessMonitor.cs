using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VniteGameCache.Wrapper;

public sealed class ProcessMonitor
{
    public async Task<IReadOnlyList<int>> WaitForExitTreeAsync(
        Process initialProcess,
        IReadOnlyList<string> additionalProcessNames,
        string? expectedExecutableRoot,
        DateTimeOffset launchStartedUtc,
        TimeSpan discoveryWindow,
        TimeSpan stableExitDelay,
        CancellationToken cancellationToken,
        bool restrictDescendantsToExpectedRoot = false,
        IReadOnlyList<string>? additionalExpectedExecutableRoots = null)
    {
        var observed = new HashSet<int> { initialProcess.Id };
        var trackedNames = additionalProcessNames
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedExpectedRoots = new[] { expectedExecutableRoot }
            .Concat(additionalExpectedExecutableRoots ?? Array.Empty<string>())
            .Select(NormalizeRoot)
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => root!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var lastSeenAliveUtc = DateTimeOffset.UtcNow;
        var discoveryUntilUtc = launchStartedUtc.Add(discoveryWindow);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wasAliveBeforeDiscovery = observed.Any(IsProcessAlive);
            DiscoverChildrenAndNamedProcesses(
                observed,
                trackedNames,
                normalizedExpectedRoots,
                launchStartedUtc,
                discoveryUntilUtc,
                wasAliveBeforeDiscovery || DateTimeOffset.UtcNow - lastSeenAliveUtc < stableExitDelay,
                restrictDescendantsToExpectedRoot);

            var alive = observed.Any(IsProcessAlive);
            if (alive)
            {
                lastSeenAliveUtc = DateTimeOffset.UtcNow;
            }
            else if (DateTimeOffset.UtcNow - lastSeenAliveUtc >= stableExitDelay)
            {
                return observed.OrderBy(id => id).ToArray();
            }

            await Task.Delay(100, cancellationToken);
        }
    }

    private static void DiscoverChildrenAndNamedProcesses(
        HashSet<int> observed,
        HashSet<string> trackedNames,
        IReadOnlyList<string> expectedExecutableRoots,
        DateTimeOffset launchStartedUtc,
        DateTimeOffset discoveryUntilUtc,
        bool allowAfterDeadline,
        bool restrictDescendantsToExpectedRoot)
    {
        if (DateTimeOffset.UtcNow > discoveryUntilUtc && !allowAfterDeadline)
        {
            return;
        }

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var startedUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                    if (startedUtc < launchStartedUtc.AddSeconds(-1))
                    {
                        continue;
                    }

                    var parentId = TryGetParentProcessId(process);
                    if (parentId is not null && observed.Contains(parentId.Value))
                    {
                        if (!restrictDescendantsToExpectedRoot || IsProcessUnderExpectedRoots(process, expectedExecutableRoots))
                        {
                            observed.Add(process.Id);
                        }
                        continue;
                    }

                    if (trackedNames.Contains(process.ProcessName) && IsProcessUnderExpectedRoots(process, expectedExecutableRoots))
                    {
                        observed.Add(process.Id);
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (Win32Exception)
                {
                }
            }
        }
    }

    private static bool IsProcessUnderExpectedRoots(Process process, IReadOnlyList<string> expectedExecutableRoots)
    {
        if (expectedExecutableRoots.Count == 0)
        {
            return true;
        }

        try
        {
            var fileName = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(fileName)
                   && expectedExecutableRoots.Any(root => IsPathUnderRoot(fileName, root));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static string? NormalizeRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path);
        var normalizedRoot = NormalizeRoot(root);
        if (string.IsNullOrWhiteSpace(normalizedRoot))
        {
            return false;
        }

        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProcessAlive(int processId)
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
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static int? TryGetParentProcessId(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var info = new ProcessBasicInformation();
        var status = NtQueryInformationProcess(
            process.Handle,
            0,
            ref info,
            Marshal.SizeOf<ProcessBasicInformation>(),
            out _);

        return status == 0 ? (int)info.InheritedFromUniqueProcessId : null;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2A;
        public IntPtr Reserved2B;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }
}
