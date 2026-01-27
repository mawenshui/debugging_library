using Microsoft.Data.Sqlite;

namespace FieldKb.Client.Wpf;

public static class DbBusyDetector
{
    public static bool IsBusy(Exception ex)
    {
        return TryFindBusySqliteException(ex, out _);
    }

    public static bool TryFindBusySqliteException(Exception ex, out SqliteException? sqliteException)
    {
        sqliteException = null;
        var current = ex;
        while (true)
        {
            if (current is SqliteException se)
            {
                sqliteException = se;
                if (se.SqliteErrorCode == 5 || se.SqliteErrorCode == 6)
                {
                    return true;
                }

                var msg = se.Message ?? string.Empty;
                if (msg.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("database is busy", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("SQLITE_BUSY", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("SQLITE_LOCKED", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                sqliteException = null;
            }

            if (current.InnerException is null)
            {
                break;
            }

            current = current.InnerException;
        }

        return false;
    }
}

