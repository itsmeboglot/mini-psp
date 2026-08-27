using Npgsql;

namespace Payments.Api.Persistence;

/// <summary>
/// Hands out connections from Npgsql's pool. The data source is a singleton;
/// connections are short lived and must be disposed by the caller.
/// </summary>
public sealed class DbConnectionFactory : IAsyncDisposable
{
    /// <summary>
    /// Npgsql defaults to 30 seconds, which for a payment API means one bad query
    /// can hold a pooled connection and a request for half a minute. At a hundred
    /// connections a handful of them take the whole pool down with them.
    /// </summary>
    private const int DefaultCommandTimeoutSeconds = 5;

    /// <summary>Npgsql defaults to 15 seconds to establish a connection.</summary>
    private const int DefaultConnectTimeoutSeconds = 3;

    private readonly NpgsqlDataSource _dataSource;

    public DbConnectionFactory(string connectionString)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);

        // Only defaulted, never overridden: an operator who put a timeout in
        // configuration meant it.
        if (!settings.ContainsKey("Command Timeout") && !settings.ContainsKey("CommandTimeout"))
        {
            settings.CommandTimeout = DefaultCommandTimeoutSeconds;
        }

        if (!settings.ContainsKey("Timeout"))
        {
            settings.Timeout = DefaultConnectTimeoutSeconds;
        }

        _dataSource = NpgsqlDataSource.Create(settings.ConnectionString);
    }

    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct)
        => _dataSource.OpenConnectionAsync(ct);

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
