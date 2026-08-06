using System.Text.Json;
using System.Text.Json.Serialization;

namespace VniteGameCache.Contracts;

public static class Protocol
{
    public const int Version = 1;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public enum WrapperExitCode
{
    Success = 0,
    InvalidRequest = 10,
    AgentUnavailable = 20,
    AgentRejected = 21,
    ExecutableNotFound = 30,
    LaunchFailed = 31,
    ProcessMonitoringFailed = 40,
    Cancelled = 50,
    UnexpectedError = 99
}

public sealed record LaunchRequest
{
    public int ProtocolVersion { get; init; } = Protocol.Version;
    public required Guid RequestId { get; init; }
    public required string GameId { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string ExecutablePath { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public required string WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyList<string> AdditionalProcessNames { get; init; } = Array.Empty<string>();
    public bool RequireSourceExecutable { get; init; } = true;
}

public sealed record PrepareLaunchRequest
{
    public int ProtocolVersion { get; init; } = Protocol.Version;
    public required Guid RequestId { get; init; }
    public required string GameId { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string ExecutablePath { get; init; }
    public required string WorkingDirectory { get; init; }
}

public sealed record PrepareLaunchResponse
{
    public int ProtocolVersion { get; init; } = Protocol.Version;
    public required Guid RequestId { get; init; }
    public required string GameId { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public required bool Ready { get; init; }
    public string? Message { get; init; }
    public string? LaunchExecutablePath { get; init; }
    public string? LaunchWorkingDirectory { get; init; }
    public ErrorResult? Error { get; init; }
}

public sealed record LaunchResult
{
    public int ProtocolVersion { get; init; } = Protocol.Version;
    public required Guid RequestId { get; init; }
    public required string GameId { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public required bool Success { get; init; }
    public int? InitialProcessId { get; init; }
    public IReadOnlyList<int> ObservedProcessIds { get; init; } = Array.Empty<int>();
    public int ExitCode { get; init; }
    public string? Message { get; init; }
    public ErrorResult? Error { get; init; }
}

public sealed record ErrorResult
{
    public int ProtocolVersion { get; init; } = Protocol.Version;
    public required Guid RequestId { get; init; }
    public required string GameId { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string Code { get; init; }
    public required string Message { get; init; }
}

public static class ProtocolJson
{
    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, Protocol.JsonOptions);
    }

    public static T? Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, Protocol.JsonOptions);
    }
}

public static class LaunchRequestValidator
{
    public static string? Validate(LaunchRequest request)
    {
        if (request.ProtocolVersion != Protocol.Version)
        {
            return $"Unsupported protocol_version: {request.ProtocolVersion}";
        }

        if (request.RequestId == Guid.Empty)
        {
            return "request_id is required";
        }

        if (string.IsNullOrWhiteSpace(request.GameId))
        {
            return "game_id is required";
        }

        if (string.IsNullOrWhiteSpace(request.ExecutablePath))
        {
            return "executable_path is required";
        }

        if (string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            return "working_directory is required";
        }

        return null;
    }

    public static string? Validate(PrepareLaunchRequest request)
    {
        if (request.ProtocolVersion != Protocol.Version)
        {
            return $"Unsupported protocol_version: {request.ProtocolVersion}";
        }

        if (request.RequestId == Guid.Empty)
        {
            return "request_id is required";
        }

        if (string.IsNullOrWhiteSpace(request.GameId))
        {
            return "game_id is required";
        }

        if (string.IsNullOrWhiteSpace(request.ExecutablePath))
        {
            return "executable_path is required";
        }

        if (string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            return "working_directory is required";
        }

        return null;
    }
}
