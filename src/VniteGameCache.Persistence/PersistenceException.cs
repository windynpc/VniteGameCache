namespace VniteGameCache.Persistence;

public sealed class PersistenceException : Exception
{
    public string Code { get; }

    public PersistenceException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }
}

public sealed class MigrationException : Exception
{
    public string Code { get; }

    public MigrationException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }
}
