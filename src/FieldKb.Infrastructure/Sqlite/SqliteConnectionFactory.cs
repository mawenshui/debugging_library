using Microsoft.Data.Sqlite;

namespace FieldKb.Infrastructure.Sqlite;

public sealed class SqliteConnectionFactory
{
    private readonly SqliteOptions _options;

    public SqliteConnectionFactory(SqliteOptions options)
    {
        _options = options;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 2
        }.ToString());

        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
