namespace VniteGameCache.Contracts;

public sealed class LaunchArgumentException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}

public sealed record LaunchArgumentParseResult(string GameId, bool LegacyQuotedGameIdNormalized);

public static class LaunchArgumentCodec
{
    public static string Build(string gameId) => $"--game-id={NormalizeGameId(gameId)}";

    public static LaunchArgumentParseResult Parse(string arguments) => Parse(Tokenize(arguments));

    public static LaunchArgumentParseResult Parse(IReadOnlyList<string> arguments)
    {
        string? gameId = null;
        string? rawGameId = null;
        var legacyQuoted = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.IsNullOrWhiteSpace(argument)) continue;

            string? value = null;
            var key = NormalizeArgumentKey(argument);
            if (key.StartsWith("--game-id=", StringComparison.OrdinalIgnoreCase))
            {
                value = key["--game-id=".Length..];
            }
            else if (key.Equals("--game-id", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= arguments.Count)
                {
                    throw new LaunchArgumentException("MissingGameIdArgument", "Missing value for --game-id.");
                }

                index++;
                value = arguments[index];
            }
            else
            {
                throw new LaunchArgumentException("MalformedGameIdArgument", $"Unexpected launch argument: {argument}");
            }

            if (gameId is not null)
            {
                throw new LaunchArgumentException("DuplicateGameIdArgument", "Duplicate --game-id argument.");
            }

            rawGameId = value;
            legacyQuoted = IsOuterQuoted(value);
            gameId = NormalizeGameId(value);
        }

        if (gameId is null)
        {
            throw new LaunchArgumentException("MissingGameIdArgument", "Missing --game-id argument.");
        }

        return new LaunchArgumentParseResult(gameId, legacyQuoted && !string.Equals(rawGameId, gameId, StringComparison.Ordinal));
    }

    public static string NormalizeGameId(string? value)
    {
        var normalized = Unquote(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new LaunchArgumentException("MissingGameIdArgument", "Game ID is empty.");
        }

        if (!Guid.TryParse(normalized, out var guid))
        {
            throw new LaunchArgumentException("MalformedGameIdArgument", $"Malformed game ID: {value}");
        }

        return guid.ToString("D");
    }

    public static bool Validate(string arguments)
    {
        try
        {
            _ = Parse(arguments);
            return true;
        }
        catch (LaunchArgumentException)
        {
            return false;
        }
    }

    private static string Unquote(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length >= 2 && IsOuterQuoted(trimmed))
        {
            return trimmed[1..^1].Trim();
        }

        if (trimmed.StartsWith('\'') || trimmed.EndsWith('\'') || trimmed.StartsWith('"') || trimmed.EndsWith('"'))
        {
            throw new LaunchArgumentException("MalformedGameIdArgument", $"Unmatched quote in game ID: {value}");
        }

        return trimmed;
    }

    private static string NormalizeArgumentKey(string value)
    {
        var trimmed = value.Trim();
        return IsOuterQuoted(trimmed) ? trimmed[1..^1].Trim() : trimmed;
    }

    private static bool IsOuterQuoted(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length >= 2 &&
               ((trimmed[0] == '\'' && trimmed[^1] == '\'') ||
                (trimmed[0] == '"' && trimmed[^1] == '"'));
    }

    private static IReadOnlyList<string> Tokenize(string arguments)
    {
        var tokens = new List<string>();
        var current = new List<char>();
        var inDoubleQuotes = false;

        foreach (var character in arguments ?? string.Empty)
        {
            if (character == '"')
            {
                current.Add(character);
                inDoubleQuotes = !inDoubleQuotes;
                continue;
            }

            if (char.IsWhiteSpace(character) && !inDoubleQuotes)
            {
                Flush();
                continue;
            }

            current.Add(character);
        }

        if (inDoubleQuotes)
        {
            throw new LaunchArgumentException("MalformedGameIdArgument", "Unmatched quote in launch arguments.");
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (current.Count == 0) return;
            tokens.Add(new string(current.ToArray()));
            current.Clear();
        }
    }
}
