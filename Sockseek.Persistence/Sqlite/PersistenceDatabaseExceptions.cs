using Microsoft.Data.Sqlite;

namespace Sockseek.Persistence.Sqlite;

public abstract class PersistenceDatabaseException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public sealed class PersistenceSchemaCompatibilityException(string message, Exception? innerException = null)
    : PersistenceDatabaseException(message, innerException);

public sealed class PersistenceDatabaseCorruptionException(string message, Exception? innerException = null)
    : PersistenceDatabaseException(message, innerException);

public sealed class PersistenceDatabaseUnavailableException(string message, Exception? innerException = null)
    : PersistenceDatabaseException(message, innerException);

internal static class PersistenceDatabaseErrors
{
    public static Exception Classify(Exception exception, string databasePath)
    {
        if (exception is PersistenceDatabaseException)
            return exception;

        var sqlite = Find<SqliteException>(exception);
        if (sqlite?.SqliteErrorCode is 11 or 26)
            return new PersistenceDatabaseCorruptionException(
                $"Persistence database '{databasePath}' is corrupt or is not a SQLite database.", exception);

        if (exception is IOException or UnauthorizedAccessException
            || Find<IOException>(exception) != null
            || Find<UnauthorizedAccessException>(exception) != null)
        {
            return new PersistenceDatabaseUnavailableException(
                $"Persistence database '{databasePath}' is unavailable: {exception.Message}", exception);
        }

        return exception;
    }

    private static T? Find<T>(Exception? exception) where T : Exception
    {
        while (exception != null)
        {
            if (exception is T match)
                return match;
            exception = exception.InnerException;
        }
        return null;
    }
}
