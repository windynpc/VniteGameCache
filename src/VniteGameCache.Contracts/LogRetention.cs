using System.Text;
using System.Text.RegularExpressions;

namespace VniteGameCache.Contracts;

public static class LogRetention
{
    public const long MaximumTotalBytes = 500L * 1024 * 1024;
    public const long MaximumFileBytes = 10L * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Regex JsonLogName = new(@"^\d{8}(?:\.plugin)?(?:\.[a-f0-9]+)?\.jsonl$", RegexOptions.IgnoreCase);

    public static string[] Roots(string logDirectory) =>
        [logDirectory, VgcSettingsStore.ResolveLogRoot(), Path.Combine(VgcSettingsStore.LocalAppDataRoot, "logs"),
         Path.Combine(VgcSettingsStore.LocalAppDataRoot, "plugin")];

    public static void Append(string filePath, string line, IEnumerable<string> roots,
        long maximumTotalBytes = MaximumTotalBytes, long maximumFileBytes = MaximumFileBytes)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                if (File.Exists(filePath) && new FileInfo(filePath).Length + Encoding.UTF8.GetByteCount(line) > maximumFileBytes)
                {
                    var archived = Path.Combine(Path.GetDirectoryName(filePath)!,
                        $"{Path.GetFileNameWithoutExtension(filePath)}.{Guid.NewGuid():N}.jsonl");
                    File.Move(filePath, archived);
                }
                File.AppendAllText(filePath, line, new UTF8Encoding(false));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Logging must not prevent a game from launching.
            }
            Trim(roots, maximumTotalBytes);
        }
    }

    public static void Trim(IEnumerable<string> roots, long maximumTotalBytes = MaximumTotalBytes)
    {
        lock (Gate)
        {
            var files = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) continue;
                    var options = new EnumerationOptions
                    {
                        RecurseSubdirectories = true, IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    };
                    foreach (var path in Directory.EnumerateFiles(root, "*", options))
                    {
                        var name = Path.GetFileName(path);
                        if (name == "vnite-game-cache.jsonl" || JsonLogName.IsMatch(name) ||
                            name.EndsWith(".robocopy.log", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith(".robocopy.log.progress.json", StringComparison.OrdinalIgnoreCase))
                        {
                            try { files[Path.GetFullPath(path)] = new FileInfo(path); }
                            catch (IOException) { }
                        }
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            var snapshots = new List<(FileInfo File, long Length, DateTime Modified)>();
            foreach (var file in files.Values)
            {
                try { snapshots.Add((file, file.Length, file.LastWriteTimeUtc)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            var total = snapshots.Sum(item => item.Length);
            foreach (var item in snapshots.OrderBy(item => item.Modified).ThenBy(item => item.File.FullName, StringComparer.Ordinal))
            {
                if (total <= maximumTotalBytes) break;
                try
                {
                    item.File.Delete();
                    total -= item.Length;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
