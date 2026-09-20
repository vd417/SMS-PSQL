using System.Data;
using System.Reflection;
using Dapper;

namespace Sms.Shared.Kernel.Data;

/// Base for all repositories. Postgres functions for writes/complex reads (call sites pass the
/// same schema-qualified name and args object used for the old SQL Server procs — the SQL Server
/// EXEC call is now a "SELECT * FROM fn(...)" using named notation, so it round-trips through
/// Postgres identifier case-folding without every call site needing to change);
/// QueryInlineAsync for simple single-table reads (parameterised only — never string-concat).
public abstract class BaseRepository(IDbConnectionFactory factory)
{
    protected IDbConnectionFactory Factory { get; } = factory;

    protected async Task<IReadOnlyList<T>> QueryProcAsync<T>(
        string proc, object? args = null, CancellationToken ct = default)
    {
        await using var conn = await Factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<T>(
            new CommandDefinition(FunctionCallSql(proc, args), args, commandType: CommandType.Text, cancellationToken: ct));
        return rows.AsList();
    }

    protected async Task<T?> QuerySingleProcAsync<T>(
        string proc, object? args = null, CancellationToken ct = default)
    {
        await using var conn = await Factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<T>(
            new CommandDefinition(FunctionCallSql(proc, args), args, commandType: CommandType.Text, cancellationToken: ct));
    }

    /// Returns the row count the function reports via `GET DIAGNOSTICS ... = ROW_COUNT; RETURN`
    /// (the Postgres equivalent of the old EXEC's @@ROWCOUNT-based return value) — every converted
    /// void/side-effect function must declare `RETURNS int` and return that count, never RETURNS void.
    protected async Task<int> ExecuteProcAsync(
        string proc, object? args = null, CancellationToken ct = default)
    {
        await using var conn = await Factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(FunctionCallSql(proc, args), args, commandType: CommandType.Text, cancellationToken: ct));
    }

    // Named notation (arg => @Arg) so Postgres resolves parameters by name, not position. The
    // argument label is quoted+lowercased rather than left bare: bare-unquoted would fold to the
    // same lowercase text for an ordinary name, but several SQL/JSON-standard words (e.g. "json",
    // "role") are reserved and can't appear unquoted in this position at all — quoting sidesteps
    // that unconditionally, and is a no-op for every non-keyword name. The converted function's
    // parameter must be declared with the matching quoted-lowercase name (or plain, if not a
    // keyword) to match.
    private static string FunctionCallSql(string proc, object? args)
    {
        if (args is null)
            return $"SELECT * FROM {proc}()";
        var argList = string.Join(", ",
            args.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => $"\"{p.Name.ToLowerInvariant()}\" => @{p.Name}"));
        return $"SELECT * FROM {proc}({argList})";
    }

    protected async Task<IReadOnlyList<T>> QueryInlineAsync<T>(
        string sql, object? args = null, CancellationToken ct = default)
    {
        await using var conn = await Factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<T>(
            new CommandDefinition(sql, args, commandType: CommandType.Text, cancellationToken: ct));
        return rows.AsList();
    }

    protected async Task<int> ExecuteInlineAsync(
        string sql, object? args = null, CancellationToken ct = default)
    {
        await using var conn = await Factory.OpenAsync(ct);
        return await conn.ExecuteAsync(
            new CommandDefinition(sql, args, commandType: CommandType.Text, cancellationToken: ct));
    }
}
