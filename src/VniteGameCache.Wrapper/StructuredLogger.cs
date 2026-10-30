using VniteGameCache.Contracts;

namespace VniteGameCache.Wrapper;

public sealed class StructuredLogger(string? configuredLogDirectory)
{
    private readonly string _logDirectory = configuredLogDirectory
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VniteGameCache",
            "logs");

    public async Task WriteAsync(
        LaunchRequest request,
        string component,
        string eventName,
        int? processId,
        string? executablePath,
        string result,
        string? error)
    {
        Directory.CreateDirectory(_logDirectory);
        var entry = new Dictionary<string, object?>
        {
            ["request_id"] = request.RequestId,
            ["game_id"] = request.GameId,
            ["component"] = component,
            ["event"] = eventName,
            ["timestamp_utc"] = DateTimeOffset.UtcNow,
            ["process_id"] = processId,
            ["executable_path"] = executablePath,
            ["result"] = result,
            ["error"] = error
        };

        var path = Path.Combine(_logDirectory, $"{DateTimeOffset.UtcNow:yyyyMMdd}.jsonl");
        await File.AppendAllTextAsync(path, ProtocolJson.Serialize(entry) + Environment.NewLine);
    }
}
