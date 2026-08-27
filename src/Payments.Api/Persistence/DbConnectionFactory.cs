using Npgsql;

namespace Payments.Api.Persistence;

/// <summary>
/// Hands out connections from Npgsql's pool. The data source is a singleton;
/// connections are short lived and must be disposed by the caller.
/// </summary>
public sealed class DbConnectionFactory : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public DbConnectionFactory(string connectionString)
        => _dataSource = NpgsqlDataSource.Create(connectionString);

    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct)
        => _dataSource.OpenConnectionAsync(ct);

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
