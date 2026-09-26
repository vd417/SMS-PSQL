using Dapper;
using Npgsql;

namespace Sms.Tests.Integration.Migrations;

/// One throwaway sms_migtest_<guid> database. Runner tests only ever hand the runner this
/// connection string (TestPostgresServer refuses anything that isn't disposable), and the database
/// is dropped in DisposeAsync, even when the test fails.
public sealed class MigrationTestDatabase : IAsyncDisposable
{
    public string Name { get; } = "sms_migtest_" + Guid.NewGuid().ToString("N");
    public string ConnectionString => TestPostgresServer.ForDatabase(Name);

    public static async Task<MigrationTestDatabase> CreateAsync()
    {
        var db = new MigrationTestDatabase();
        await TestPostgresServer.CreateDatabaseAsync(db.Name);
        return db;
    }

    public async Task ExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.ExecuteAsync(sql);
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        return await conn.ExecuteScalarAsync<T>(sql) ?? throw new InvalidOperationException($"NULL from: {sql}");
    }

    public async Task<bool> ExistsAsync(string regclass)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        return await conn.ExecuteScalarAsync<bool>("SELECT to_regclass(@r) IS NOT NULL", new { r = regclass });
    }

    public async Task<int[]> AppliedVersionsAsync()
    {
        if (!await ExistsAsync("dbo.schema_migrations")) return [];
        await using var conn = new NpgsqlConnection(ConnectionString);
        return (await conn.QueryAsync<int>("SELECT version FROM dbo.schema_migrations ORDER BY version")).ToArray();
    }

    public ValueTask DisposeAsync() => new(TestPostgresServer.DropDatabaseAsync(Name));
}
