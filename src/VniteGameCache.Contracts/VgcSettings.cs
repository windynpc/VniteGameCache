using System.Text.Json;
using System.Text.Json.Serialization;

namespace VniteGameCache.Contracts;

public sealed record VgcSettings
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("database_path")]
    public string? DatabasePath { get; init; }

    [JsonPropertyName("default_cache_root")]
    public string? DefaultCacheRoot { get; init; }

    [JsonPropertyName("default_save_backup_root")]
    public string? DefaultSaveBackupRoot { get; init; }

    [JsonPropertyName("minimum_free_space_bytes")]
    public long? MinimumFreeSpaceBytes { get; init; }

    [JsonPropertyName("cache_max_total_bytes")]
    public long? CacheMaxTotalBytes { get; init; }

    [JsonPropertyName("quarantine_retention_days")]
    public int? QuarantineRetentionDays { get; init; }

    [JsonPropertyName("protect_config_files")]
    public bool ProtectConfigFiles { get; init; }

    [JsonPropertyName("log_root")]
    public string? LogRoot { get; init; }

    [JsonPropertyName("commands_root")]
    public string? CommandsRoot { get; init; }

    [JsonPropertyName("default_launch_mode")]
    public string? DefaultLaunchMode { get; init; }

    [JsonPropertyName("mtool_executable_path")]
    public string? MToolExecutablePath { get; init; }

    [JsonPropertyName("updated_utc")]
    public DateTimeOffset? UpdatedUtc { get; init; }
}

public enum VgcSettingsHealthStatus
{
    Healthy,
    Missing,
    InvalidJson,
    Unreadable,
    UnsupportedSchema
}

public enum VgcLaunchMode
{
    Direct,
    TranslationPreferred,
    MTool
}

public sealed record VgcSettingsLoadResult(
    VgcSettings Settings,
    string SettingsPath,
    VgcSettingsHealthStatus Status,
    string? ErrorMessage)
{
    public bool IsHealthy => Status == VgcSettingsHealthStatus.Healthy;
}

public sealed class VgcSettingsException(string code, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public static class VgcSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string DefaultSettingsPath =>
        Path.Combine(LocalAppDataRoot, "config", "settings.json");

    public static string LocalAppDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VniteGameCache");

    public static VgcSettings Load() => LoadWithDiagnostics().Settings;

    public static VgcSettings LoadRequired()
    {
        var result = LoadWithDiagnostics();
        return result.Status switch
        {
            VgcSettingsHealthStatus.Healthy or VgcSettingsHealthStatus.Missing => result.Settings,
            _ => throw new VgcSettingsException(
                "settings_unhealthy",
                $"Shared settings are not usable ({result.Status}): {result.SettingsPath}. {result.ErrorMessage}".Trim())
        };
    }

    public static VgcSettingsLoadResult LoadWithDiagnostics()
    {
        var path = ResolveSettingsPath();

        if (!File.Exists(path))
        {
            return new VgcSettingsLoadResult(new VgcSettings(), path, VgcSettingsHealthStatus.Missing, "Settings file does not exist.");
        }

        try
        {
            var settings = JsonSerializer.Deserialize<VgcSettings>(File.ReadAllText(path), JsonOptions);
            if (settings is null)
            {
                return new VgcSettingsLoadResult(new VgcSettings(), path, VgcSettingsHealthStatus.InvalidJson, "Settings document is empty.");
            }

            if (settings.SchemaVersion != 1)
            {
                return new VgcSettingsLoadResult(settings, path, VgcSettingsHealthStatus.UnsupportedSchema, $"Unsupported settings schema: {settings.SchemaVersion}.");
            }

            return new VgcSettingsLoadResult(settings, path, VgcSettingsHealthStatus.Healthy, null);
        }
        catch (JsonException error)
        {
            return new VgcSettingsLoadResult(new VgcSettings(), path, VgcSettingsHealthStatus.InvalidJson, error.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new VgcSettingsLoadResult(new VgcSettings(), path, VgcSettingsHealthStatus.Unreadable, error.Message);
        }
    }

    public static string ResolveDatabasePath()
    {
        var envPath = Environment.GetEnvironmentVariable("VGC_DB_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return envPath;
        }

        var configuredPath = LoadRequired().DatabasePath;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        return Path.Combine(LocalAppDataRoot, "data", "vnite-game-cache.db");
    }

    public static string ResolveLogRoot()
    {
        var configured = Load().LogRoot;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(LocalAppDataRoot, "logs")
            : configured;
    }

    public static string ResolveCommandsRoot()
    {
        var configured = Load().CommandsRoot;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(LocalAppDataRoot, "commands")
            : configured;
    }

    public static string ResolveDefaultSaveBackupRoot(string gameId)
    {
        var configured = LoadRequired().DefaultSaveBackupRoot;
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(LocalAppDataRoot, "saves")
            : configured;
        return Path.Combine(root, gameId);
    }

    public static long? ResolveMinimumFreeSpaceBytes() => LoadRequired().MinimumFreeSpaceBytes;

    public static VgcLaunchMode ParseLaunchMode(string? value)
    {
        return value?.Trim() switch
        {
            nameof(VgcLaunchMode.TranslationPreferred) => VgcLaunchMode.TranslationPreferred,
            nameof(VgcLaunchMode.MTool) => VgcLaunchMode.MTool,
            _ => VgcLaunchMode.Direct
        };
    }

    private static string ResolveSettingsPath()
    {
        var path = Environment.GetEnvironmentVariable("VGC_SETTINGS_PATH");
        return string.IsNullOrWhiteSpace(path) ? DefaultSettingsPath : Path.GetFullPath(path);
    }
}
