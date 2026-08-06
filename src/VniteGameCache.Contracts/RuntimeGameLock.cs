using System.Security.Cryptography;
using System.Text;

namespace VniteGameCache.Contracts;

public static class RuntimeGameLock
{
    public static string NameFor(string gameId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(gameId))).ToLowerInvariant();
        return $@"Local\VniteGameCache.Game.{hash}";
    }

    public static Mutex? TryAcquire(string gameId)
    {
        var mutex = new Mutex(initiallyOwned: true, NameFor(gameId), out var createdNew);
        if (createdNew)
        {
            return mutex;
        }

        mutex.Dispose();
        return null;
    }

    public static bool IsHeld(string gameId)
    {
        try
        {
            using var mutex = Mutex.OpenExisting(NameFor(gameId));
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
