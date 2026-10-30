namespace VniteGameCache.ControlPanel;

internal static class DirectorySizeProbe
{
    internal static (long Bytes, long Files) Measure(string root, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return (0, 0);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"核算目录是链接，已停止：{root}");

        long bytes = 0;
        long files = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else
                {
                    bytes = checked(bytes + new FileInfo(path).Length);
                    files = checked(files + 1);
                }
            }
        }
        return (bytes, files);
    }
}
