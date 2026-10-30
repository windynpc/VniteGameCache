using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace VniteGameCache.LibraryImport;

public sealed record ArchivePreviewResult(
    string ArchivePath,
    bool Supported,
    long ExpandedBytes,
    int FileCount,
    IReadOnlyList<string> ExecutableCandidates,
    IReadOnlyList<string> Warnings);

public static class ArchivePreview
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar"
    };

    public static async Task<ArchivePreviewResult> ReadAsync(
        string archivePath,
        string sevenZipPath,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        if (!SupportedExtensions.Contains(Path.GetExtension(archivePath)) || !File.Exists(archivePath))
        {
            return new ArchivePreviewResult(archivePath, false, 0, 0, [], ["Archive file is missing or its extension is unsupported."]);
        }
        if (Regex.IsMatch(Path.GetFileName(archivePath), @"\.part\d+\.rar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return new ArchivePreviewResult(archivePath, false, 0, 0, [], ["Multi-volume RAR archives require manual handling."]);
        }
        if (!File.Exists(sevenZipPath))
        {
            return new ArchivePreviewResult(archivePath, false, 0, 0, [], ["7-Zip executable is unavailable."]);
        }

        var start = new ProcessStartInfo(sevenZipPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("l");
        start.ArgumentList.Add("-slt");
        start.ArgumentList.Add("-sccUTF-8");
        start.ArgumentList.Add(archivePath);

        using var process = Process.Start(start) ?? throw new IOException("Unable to start 7-Zip.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        var listing = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
        {
            return new ArchivePreviewResult(archivePath, false, 0, 0, [],
                [$"7-Zip listing failed (exit {process.ExitCode}): {error.Trim()}"]);
        }
        return ParseListing(archivePath, listing, warnings);
    }

    public static ArchivePreviewResult ParseListing(string archivePath, string listing)
    {
        return ParseListing(archivePath, listing, []);
    }

    private static ArchivePreviewResult ParseListing(string archivePath, string listing, List<string> warnings)
    {
        var entriesStart = listing.IndexOf("----------", StringComparison.Ordinal);
        if (entriesStart < 0)
        {
            return new ArchivePreviewResult(archivePath, false, 0, 0, [], ["7-Zip returned no archive entry table."]);
        }

        long expandedBytes = 0;
        var fileCount = 0;
        var executables = new List<string>();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unsafeEntry = false;
        var encrypted = false;
        var linkedEntry = false;

        void Commit()
        {
            if (!fields.TryGetValue("Path", out var member)) { fields.Clear(); return; }
            if (!IsSafeMemberPath(member)) unsafeEntry = true;
            if (fields.ContainsKey("Symbolic Link") || fields.ContainsKey("Hard Link") ||
                fields.GetValueOrDefault("Attributes")?.Contains('L') == true)
            {
                linkedEntry = true;
            }
            if (fields.GetValueOrDefault("Folder") == "+") { fields.Clear(); return; }
            fileCount++;
            if (fields.GetValueOrDefault("Encrypted") == "+") encrypted = true;
            if (long.TryParse(fields.GetValueOrDefault("Size"), out var size) && size >= 0)
            {
                expandedBytes = size > long.MaxValue - expandedBytes ? long.MaxValue : expandedBytes + size;
            }
            if (Path.GetExtension(member).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                executables.Add(member.Replace('\\', '/'));
            fields.Clear();
        }

        foreach (var line in listing[(entriesStart + 10)..].Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0) { Commit(); continue; }
            var split = trimmed.IndexOf(" = ", StringComparison.Ordinal);
            if (split > 0) fields[trimmed[..split]] = trimmed[(split + 3)..];
        }
        Commit();
        if (unsafeEntry) warnings.Add("Archive contains an unsafe member path.");
        if (encrypted) warnings.Add("Archive contains encrypted entries.");
        if (linkedEntry) warnings.Add("Archive contains linked entries.");
        if (fileCount == 0) warnings.Add("Archive contains no files.");
        if (executables.Count == 0) warnings.Add("No executable candidate found.");
        if (executables.Count > 1) warnings.Add("Multiple executable candidates require confirmation.");
        return new ArchivePreviewResult(archivePath, !unsafeEntry && !encrypted && !linkedEntry && fileCount > 0,
            expandedBytes, fileCount, executables, warnings);
    }

    private static bool IsSafeMemberPath(string member)
    {
        if (string.IsNullOrWhiteSpace(member) || Path.IsPathRooted(member) || member.Contains(':')) return false;
        var parts = member.Replace('\\', '/').Split('/');
        return parts.All(part => part.Length > 0 && part != "." && part != ".." &&
            !part.EndsWith(' ') && !part.EndsWith('.') &&
            !ReservedNames.Contains(part.Split('.')[0]) &&
            part.IndexOfAny(['<', '>', '"', '|', '?', '*', '\0']) < 0);
    }
}
