# PostgreSQL Forward-Migration Mechanism Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a small, Npgsql-only forward-migration runner (`init` / `migrate` / `status`). Ship the `client_delete` fix and the dead-function removal as migrations 0001/0002. Retire the SQL Server `MigrateCli`. Migrate the local `sms_dev` behind a verified backup. Prove RLS and migrations in a real compose stack in CI. Push only once everything, including the real GitHub Actions run, is green.

**Architecture:** `db/Sms.PgMigrator` is a console project with the logic in a library-style `PgMigrator` class. It applies the immutable `db/postgres/*.sql` baseline to an empty database (`init`), or only pending `db/postgres/migrations/NNNN_*.sql` files to an existing one (`migrate`). It tracks them in `dbo.schema_migrations` with checksums, under a Postgres advisory lock, one transaction per migration. The integration-test fixture builds every test database through the same runner, so tests see exactly what production sees.

**Tech Stack:** .NET 10, Npgsql 9.0.3 (already used by `Sms.Shared.Kernel`), xUnit 2.9 + FluentAssertions 8, Dapper (tests only), PostgreSQL 18, docker compose, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-09-26-postgres-forward-migrations-design.md` (approved 2026-09-26, with the user's additional requirements folded in below).

## Global Constraints

- `db/postgres/*.sql` (top-level baseline files) stay byte-for-byte unchanged. `git diff ebedefd -- db/postgres/*.sql` must be empty at the end.
- No new NuGet dependencies. The runner uses `Npgsql` 9.0.3 only.
- Migration file names match `^\d{4}_[a-z0-9_]+\.sql$`, and versions are ≥ 1. Any other `.sql` file in the folder, or a duplicate version, is a hard error.
- Tracking table: `dbo.schema_migrations (version int PK, name text, checksum text, applied_at timestamptz)`. The checksum is SHA-256 hex (lowercase) of the file with CRLF normalised to LF, because `core.autocrlf=true` here and CI checks out LF.
- Each migration runs in its own transaction, and its `INSERT INTO dbo.schema_migrations` is in that same transaction. Failure means rollback, not recorded, stop.
- Advisory lock: `pg_try_advisory_lock(PgMigrator.AdvisoryLockKey)`, polled every 500 ms up to the lock timeout (default 60 s). The key is a fixed `long` constant that must never change.
- A migration marked `-- sms:destructive` in its leading comment block needs `--backup-confirmed` for `migrate`. `init` targets an empty DB, so it has nothing to back up.
- Forward-only. The runner never executes rollback SQL.
- Tests only ever mutate `sms_test_*` / `sms_migtest_*` databases. `TestPostgresServer.EnsureDisposable` enforces this.
- The runner never runs against production. On this machine it runs only against disposable test DBs, a disposable `sms_audit_*` DB, and (Task 8, after a verified backup) `sms_dev`.
- The historical files `db/Sms.Migrations/M*.cs`, `MigrationRunner.cs` and `procs/**` are untouched. The only exception is `MigrateCli.cs`, which the user explicitly asked to retire (Task 6).
- The API never runs migrations. It connects as `sms_app`, and `DatabaseRoleGuard` refuses superuser/BYPASSRLS roles outside Development.
- Never print a password. Read secrets inside the same command that uses them.
- Commit per task. **Do not push until Task 10.**
- Test commands need `SMS_TEST_PG_PASSWORD=12345678` (local superuser `postgres`). psql and pg_dump are in `C:\Program Files\PostgreSQL\18\bin`.

## Review Focus

1. **A migration that contains its own `COMMIT;`/`BEGIN;`.** It would end the runner's transaction and get recorded even if a later statement failed. The loader must reject it. Test: Task 2 `Rejects_a_file_that_controls_its_own_transaction`.
2. **A file saved with CRLF on Windows and checked out with LF on Linux CI.** It must have the same checksum, or every CI/compose run would report "edited after applied". Test: Task 2 `Checksum_ignores_line_ending_style`.
3. **An operator running `migrate` with the API's `sms_app` connection string.** Expect a clear "connect as the schema owner" refusal with nothing recorded, not a raw permission error. Test: Task 3 `Migrate_as_the_app_role_is_refused_with_a_clear_message`.
4. **`status` on a baseline database that has never been migrated.** It should report everything as pending and write nothing (no tracking table created). Test: Task 3 `Status_is_read_only_and_reports_pending`.
5. **Immediately re-running after a failed migration.** The lock must have been released, so the re-run doesn't wait for the timeout. Test: Task 3 `Failed_migration_is_rolled_back_not_recorded_and_stops_the_run`, which re-runs with a 1 s lock timeout.

---

## File Structure

| File | Responsibility |
|---|---|
| `db/Sms.PgMigrator/Sms.PgMigrator.csproj` | Console project. Copies baseline and migrations into output under `baseline/` and `migrations/` (build and publish). |
| `db/Sms.PgMigrator/MigrationException.cs` | The one exception type for every refusal or failure. |
| `db/Sms.PgMigrator/MigrationFile.cs` | Loads and validates a migrations folder: name pattern, duplicates, checksum, destructive directive, transaction-control ban. Pure file I/O. |
| `db/Sms.PgMigrator/AdvisoryLock.cs` | Acquire (with timeout) and release of the session advisory lock. |
| `db/Sms.PgMigrator/PgMigrator.cs` | `InitAsync` / `MigrateAsync` / `StatusAsync`. |
| `db/Sms.PgMigrator/Cli.cs` | Argument parsing and `Main`. Exit codes 0 = ok, 1 = usage, 2 = refused/failed. |
| `db/Sms.PgMigrator/Dockerfile` | Image for the compose `migrate` service. |
| `db/postgres/migrations/0001_client_delete_fix.sql` | Parameter rename plus the 4 restored DELETEs. |
| `db/postgres/migrations/0002_drop_trip_ping_bulk_insert.sql` | Drops the dead worked example. |
| `tests/Sms.Tests.Integration/TestPostgresServer.cs` | Shared test-server connection strings, create/drop of disposable DBs, target guard. |
| `tests/Sms.Tests.Integration/PostgresFixture.cs` | Builds the per-run DB with `PgMigrator.InitAsync`. |
| `tests/Sms.Tests.Integration/Migrations/MigrationTestDatabase.cs` | One throwaway `sms_migtest_*` DB with small query helpers. |
| `tests/Sms.Tests.Integration/Migrations/PgMigratorTests.cs` | Runner behaviour against real Postgres. |
| `tests/Sms.Tests.Integration/Migrations/RealMigrationsTests.cs` | The real baseline plus 0001/0002. |
| `tests/Sms.Tests.Integration/Catre/ClientDeleteTests.cs` | Delete path end to end (already written). Extended with the 4-table orphan check. |
| `tests/Sms.Tests.Integration/Data/DatabaseRoleGuardTests.cs` | The API refuses to start as a superuser outside Development. |
| `tests/Sms.Tests.Unit/PgMigrator/MigrationFileTests.cs`, `CliTests.cs` | Pure logic. |
| `docs/runbooks/postgres-migrations.md` | Deploy, backup and restore procedure. |
| `docker-compose.yml`, `.github/workflows/ci.yml` | `migrate` service and `compose-smoke` job. |

Already done and uncommitted (from the approved bounded design): `docker-compose.yml` (API as `sms_app`), `appsettings.Development.json` (`Username=sms_app`), `src/Sms.Api/Extensions/DatabaseRoleGuard.cs` plus its call in `Program.cs`, removal of the `Sms.Migrations` reference and `using`s from `Sms.Api`, and `tests/.../Catre/ClientDeleteTests.cs`. The last one currently fails and gets committed in Task 5.

---

### Task 1: Shared test-server helper, guard test, commit the bounded API changes

**Files:**
- Create: `tests/Sms.Tests.Integration/TestPostgresServer.cs`
- Modify: `tests/Sms.Tests.Integration/PostgresFixture.cs` (use the helper; no behaviour change)
- Create: `tests/Sms.Tests.Integration/Data/DatabaseRoleGuardTests.cs`

**Interfaces:**
- Produces: `TestPostgresServer.ForDatabase(string db) : string` (owner/superuser CS), `TestPostgresServer.AppConnectionString(string db) : string` (`sms_app`), `TestPostgresServer.CreateDatabaseAsync(string db) : Task`, `TestPostgresServer.DropDatabaseAsync(string db) : Task`, `TestPostgresServer.EnsureDisposable(string db) : void`.

- [ ] **Step 1: Create `TestPostgresServer.cs`**

```csharp
using Npgsql;

namespace Sms.Tests.Integration;

/// Connection strings for the Postgres server integration tests run against, plus create/drop of
/// disposable databases. Every mutating entry point refuses any database that isn't a disposable
/// sms_test_* (PostgresFixture) or sms_migtest_* (runner tests) one, so no test can ever touch
/// sms_dev or any other shared database, whatever the environment variables point at.
public static class TestPostgresServer
{
    private static readonly string? OverrideCs = Environment.GetEnvironmentVariable("SMS_TEST_PG_CONNECTION");
    private static readonly string Host = Environment.GetEnvironmentVariable("SMS_TEST_PG_HOST") ?? "localhost";
    private static readonly string[] DisposablePrefixes = ["sms_test_", "sms_migtest_"];

    public static void EnsureDisposable(string db)
    {
        if (!DisposablePrefixes.Any(p => db.StartsWith(p, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"Refusing to target database '{db}': tests may only touch disposable sms_test_*/sms_migtest_* databases.");
    }

    /// Owner (superuser) connection to a disposable test database: schema DDL, CREATE ROLE, GRANT.
    public static string ForDatabase(string db)
    {
        EnsureDisposable(db);
        return Build(db, username: null, password: null);
    }

    /// The app-facing non-superuser role (db/postgres/00_app_role.sql), so RLS actually applies.
    public static string AppConnectionString(string db)
    {
        EnsureDisposable(db);
        return Build(db, "sms_app", "sms_app_dev_pw");
    }

    public static async Task CreateDatabaseAsync(string db)
    {
        EnsureDisposable(db);
        await using var admin = new NpgsqlConnection(Build("postgres", null, null));
        await admin.OpenAsync();
        await using var create = admin.CreateCommand();
        // Database names can't be parameterised; db is our own guid-based name (checked above).
        create.CommandText = $"CREATE DATABASE \"{db}\";";
        await create.ExecuteNonQueryAsync();
    }

    public static async Task DropDatabaseAsync(string db)
    {
        EnsureDisposable(db);
        await using var admin = new NpgsqlConnection(Build("postgres", null, null));
        await admin.OpenAsync();
        await using (var terminate = admin.CreateCommand())
        {
            terminate.CommandText =
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid();";
            terminate.Parameters.AddWithValue("db", db);
            await terminate.ExecuteNonQueryAsync();
        }
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{db}\";";
        await drop.ExecuteNonQueryAsync();
    }

    private static string Build(string db, string? username, string? password)
    {
        var b = !string.IsNullOrEmpty(OverrideCs)
            ? new NpgsqlConnectionStringBuilder(OverrideCs) { Database = db }
            : new NpgsqlConnectionStringBuilder
            {
                Host = Host, Database = db, Username = "postgres",
                Password = Environment.GetEnvironmentVariable("SMS_TEST_PG_PASSWORD"),
            };
        if (username is not null) { b.Username = username; b.Password = password; }
        return b.ConnectionString;
    }
}
```

- [ ] **Step 2: Switch `PostgresFixture` to the helper (same behaviour)**

Replace the body of `PostgresFixture` (keep the class doc comment and the `SqlCollection` definition) with:

```csharp
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly string _dbName = "sms_test_" + Guid.NewGuid().ToString("N");
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await TestPostgresServer.CreateDatabaseAsync(_dbName);

        var schemaDir = Path.Combine(AppContext.BaseDirectory, "postgres-schema");
        var files = Directory.GetFiles(schemaDir, "*.sql").OrderBy(f => f, StringComparer.Ordinal);

        // Schema application (CREATE ROLE, ENABLE/FORCE RLS, GRANT) needs superuser privilege.
        await using (var conn = new NpgsqlConnection(TestPostgresServer.ForDatabase(_dbName)))
        {
            await conn.OpenAsync();
            foreach (var file in files)
            {
                await using var batch = conn.CreateCommand();
                batch.CommandText = await File.ReadAllTextAsync(file);
                await batch.ExecuteNonQueryAsync();
            }
        }

        // The app itself (and therefore every test) connects as the non-superuser role from
        // here on, so RLS policies are genuinely exercised.
        ConnectionString = TestPostgresServer.AppConnectionString(_dbName);
    }

    public Task DisposeAsync() => TestPostgresServer.DropDatabaseAsync(_dbName);
}
```

- [ ] **Step 3: Write the guard tests**

`tests/Sms.Tests.Integration/Data/DatabaseRoleGuardTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Sms.Tests.Integration.Data;

/// DatabaseRoleGuard: outside Development the API must refuse to start when its connection role
/// bypasses RLS (superuser or BYPASSRLS), and start normally as sms_app.
[Collection("sql")]
public class DatabaseRoleGuardTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private static WebApplicationFactory<Program> App(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", connectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    [Fact]
    public void Api_refuses_to_start_outside_development_when_connected_as_a_superuser()
    {
        var db = new NpgsqlConnectionStringBuilder(fx.ConnectionString).Database!;
        using var app = App(TestPostgresServer.ForDatabase(db));

        var start = () => app.CreateClient();

        start.Should().Throw<Exception>()
            .Where(e => e.ToString().Contains("bypasses row-level security"));
    }

    [Fact]
    public async Task Api_starts_when_connected_as_sms_app()
    {
        await using var app = App(fx.ConnectionString);
        var res = await app.CreateClient().GetAsync("/health/ready");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
```

- [ ] **Step 4: Run the guard tests**

Run: `SMS_TEST_PG_PASSWORD=12345678 dotnet build tests/Sms.Tests.Integration && SMS_TEST_PG_PASSWORD=12345678 dotnet test tests/Sms.Tests.Integration --no-build --filter "FullyQualifiedName~DatabaseRoleGuardTests"`

Expected: 2 passed. `DatabaseRoleGuard` was implemented before this plan. To confirm the test is real, temporarily comment out `await DatabaseRoleGuard.RunAsync(app);` in `Program.cs`, rerun, and see `Api_refuses…` FAIL. Then restore the line.

- [ ] **Step 5: Run the Catre/Data suites to confirm the fixture refactor changed nothing**

Run: `SMS_TEST_PG_PASSWORD=12345678 dotnet test tests/Sms.Tests.Integration --no-build --filter "FullyQualifiedName~Catre|FullyQualifiedName~Data"`

Expected: all pass except the 3 `ClientDeleteTests`. Those still fail with 500, since that's the unfixed bug.

- [ ] **Step 6: Commit (ClientDeleteTests stays uncommitted until Task 5)**

```bash
git add docker-compose.yml src/Sms.Api/Program.cs src/Sms.Api/Sms.Api.csproj src/Sms.Api/appsettings.Development.json \
  src/Sms.Api/Extensions/ServiceCollectionExtensions.cs src/Sms.Api/Extensions/DatabaseRoleGuard.cs \
  tests/Sms.Tests.Integration/TestPostgresServer.cs tests/Sms.Tests.Integration/PostgresFixture.cs \
  tests/Sms.Tests.Integration/Data/DatabaseRoleGuardTests.cs
git commit -m "fix(security): never run the API as an RLS-bypassing Postgres role

- docker-compose: API connects as sms_app, not the sms bootstrap superuser
- appsettings.Development: default username sms_app (password still from user-secrets)
- DatabaseRoleGuard: refuse to start outside Development as superuser/BYPASSRLS, log an error in Development
- Sms.Api no longer references Sms.Migrations (drops FluentMigrator.SqlServer/SqlClient from the API)
- tests: shared TestPostgresServer helper that only ever targets disposable databases

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: `Sms.PgMigrator` project and `MigrationFile` loader

**Files:**
- Create: `db/Sms.PgMigrator/Sms.PgMigrator.csproj`, `db/Sms.PgMigrator/MigrationException.cs`, `db/Sms.PgMigrator/MigrationFile.cs`, `db/Sms.PgMigrator/Cli.cs` (stub `Main` only, finished in Task 4)
- Create: `db/postgres/migrations/.gitkeep`. The folder must exist for the csproj glob and the runner; `.gitkeep` isn't `.sql`, so the loader ignores it.
- Modify: `Sms.slnx` (add the project under `/db/`), `tests/Sms.Tests.Unit/Sms.Tests.Unit.csproj` (ProjectReference)
- Test: `tests/Sms.Tests.Unit/PgMigrator/MigrationFileTests.cs`

**Interfaces:**
- Produces: `MigrationFile(int Version, string Name, string Sql, string Checksum, bool Destructive)` with `string FileName`, `static IReadOnlyList<MigrationFile> LoadDirectory(string directory)` (sorted ascending by `Version`), `static string ComputeChecksum(string sql)`, `const string DestructiveDirective = "-- sms:destructive"`. Also `MigrationException(string message, Exception? inner = null)`.

- [ ] **Step 1: Create the project**

`db/Sms.PgMigrator/Sms.PgMigrator.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!-- Postgres schema runner: `init` (empty DB -> baseline + migrations), `migrate` (existing DB ->
         pending migrations), `status`. The entry point is Sms.PgMigrator.Cli (not a top-level Program)
         so the integration tests can reference this project alongside Sms.Api's Program. -->
    <OutputType>Exe</OutputType>
    <StartupObject>Sms.PgMigrator.Cli</StartupObject>
    <RootNamespace>Sms.PgMigrator</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Npgsql" Version="9.0.3" />
  </ItemGroup>

  <ItemGroup>
    <None Include="..\postgres\*.sql" LinkBase="baseline" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
    <None Include="..\postgres\migrations\*.sql" LinkBase="migrations" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

`db/Sms.PgMigrator/MigrationException.cs`:

```csharp
namespace Sms.PgMigrator;

/// Every refusal or failure the runner reports. The message says what happened, what state the
/// database was left in, and what the operator should do next.
public sealed class MigrationException(string message, Exception? inner = null) : Exception(message, inner);
```

`db/Sms.PgMigrator/Cli.cs` (stub, replaced in Task 4):

```csharp
namespace Sms.PgMigrator;

public static class Cli
{
    public static int Main(string[] args) => 1;
}
```

Add to `Sms.slnx` inside `<Folder Name="/db/">`: `<Project Path="db/Sms.PgMigrator/Sms.PgMigrator.csproj" />`

Add to `tests/Sms.Tests.Unit/Sms.Tests.Unit.csproj`'s ProjectReference ItemGroup: `<ProjectReference Include="..\..\db\Sms.PgMigrator\Sms.PgMigrator.csproj" />`

Create an empty `db/postgres/migrations/.gitkeep`.

- [ ] **Step 2: Write the failing tests**

`tests/Sms.Tests.Unit/PgMigrator/MigrationFileTests.cs`:

```csharp
using FluentAssertions;
using Sms.PgMigrator;

namespace Sms.Tests.Unit.PgMigrator;

public sealed class MigrationFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sms_migfile_").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Write(string name, string sql) => File.WriteAllText(Path.Combine(_dir, name), sql);

    [Fact]
    public void Loads_in_numeric_order_with_name_and_version()
    {
        Write("0010_later.sql", "SELECT 10;");
        Write("0002_second.sql", "SELECT 2;");
        Write("0001_first.sql", "SELECT 1;");
        File.WriteAllText(Path.Combine(_dir, "README.md"), "not sql, ignored");

        var files = MigrationFile.LoadDirectory(_dir);

        files.Select(f => f.Version).Should().Equal(1, 2, 10);
        files[0].Name.Should().Be("first");
        files[0].FileName.Should().Be("0001_first.sql");
        files[0].Sql.Should().Be("SELECT 1;");
    }

    [Theory]
    [InlineData("1_short.sql")]
    [InlineData("0001-dash.sql")]
    [InlineData("0001_Upper.sql")]
    [InlineData("0000_zero.sql")]
    [InlineData("fix.sql")]
    public void Rejects_a_sql_file_with_a_bad_name(string name)
    {
        Write(name, "SELECT 1;");
        var load = () => MigrationFile.LoadDirectory(_dir);
        load.Should().Throw<MigrationException>().WithMessage($"*{name}*");
    }

    [Fact]
    public void Rejects_duplicate_versions()
    {
        Write("0001_a.sql", "SELECT 1;");
        Write("0001_b.sql", "SELECT 1;");
        var load = () => MigrationFile.LoadDirectory(_dir);
        load.Should().Throw<MigrationException>().WithMessage("*Duplicate migration version 0001*");
    }

    [Fact]
    public void Missing_directory_is_an_error()
    {
        var load = () => MigrationFile.LoadDirectory(Path.Combine(_dir, "nope"));
        load.Should().Throw<MigrationException>().WithMessage("*not found*");
    }

    [Fact]
    public void Checksum_ignores_line_ending_style()
    {
        MigrationFile.ComputeChecksum("SELECT 1;\r\nSELECT 2;\r\n")
            .Should().Be(MigrationFile.ComputeChecksum("SELECT 1;\nSELECT 2;\n"));
        MigrationFile.ComputeChecksum("SELECT 1;").Should().MatchRegex("^[0-9a-f]{64}$");
        MigrationFile.ComputeChecksum("SELECT 1;").Should().NotBe(MigrationFile.ComputeChecksum("SELECT 2;"));
    }

    [Fact]
    public void Destructive_directive_is_read_from_the_leading_comment_block_only()
    {
        Write("0001_drop.sql", "-- 0001: drops a column\n-- sms:destructive\n\nALTER TABLE t DROP COLUMN c;");
        Write("0002_safe.sql", "SELECT 1;\n-- sms:destructive\n");

        var files = MigrationFile.LoadDirectory(_dir);

        files[0].Destructive.Should().BeTrue();
        files[1].Destructive.Should().BeFalse();
    }

    [Theory]
    [InlineData("CREATE TABLE t (id int);\nCOMMIT;")]
    [InlineData("BEGIN;\nCREATE TABLE t (id int);")]
    [InlineData("rollback;")]
    [InlineData("START TRANSACTION;")]
    [InlineData("COMMIT WORK;")]
    public void Rejects_a_file_that_controls_its_own_transaction(string sql)
    {
        Write("0001_tx.sql", sql);
        var load = () => MigrationFile.LoadDirectory(_dir);
        load.Should().Throw<MigrationException>().WithMessage("*transaction*");
    }

    [Fact]
    public void Plpgsql_block_begin_and_end_are_allowed()
    {
        Write("0001_fn.sql", "DO $$\nBEGIN\n  PERFORM 1;\nEND;\n$$;");
        MigrationFile.LoadDirectory(_dir).Should().ContainSingle();
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/Sms.Tests.Unit --filter "FullyQualifiedName~MigrationFileTests"`
Expected: build error `The type or namespace name 'MigrationFile' could not be found`.

- [ ] **Step 4: Implement `MigrationFile.cs`**

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Sms.PgMigrator;

/// One forward migration: db/postgres/migrations/NNNN_description.sql. Loading is strict on
/// purpose, so a misnamed, duplicated or self-committing file stops the run instead of being
/// silently skipped or half-applied.
public sealed partial record MigrationFile(int Version, string Name, string Sql, string Checksum, bool Destructive)
{
    /// Leading-comment directive marking a migration that drops or rewrites existing data;
    /// `migrate` then requires --backup-confirmed (docs/runbooks/postgres-migrations.md).
    public const string DestructiveDirective = "-- sms:destructive";

    public string FileName => $"{Version:D4}_{Name}.sql";

    [GeneratedRegex(@"^(\d{4})_([a-z0-9_]+)\.sql$")]
    private static partial Regex FileNamePattern();

    // The runner wraps each file in its own transaction together with its schema_migrations row.
    // A file that commits or opens a transaction itself would break that guarantee. PL/pgSQL
    // block BEGIN/END carry no trailing semicolon after BEGIN, and END is deliberately not
    // matched, so DO blocks and function bodies are unaffected.
    [GeneratedRegex(@"^\s*(BEGIN|COMMIT|ROLLBACK|START\s+TRANSACTION)(\s+(WORK|TRANSACTION))?\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex TransactionControl();

    public static IReadOnlyList<MigrationFile> LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            throw new MigrationException($"Migrations directory not found: {directory}");

        var files = new List<MigrationFile>();
        foreach (var path in Directory.GetFiles(directory, "*.sql"))
        {
            var fileName = Path.GetFileName(path);
            var match = FileNamePattern().Match(fileName);
            var version = match.Success ? int.Parse(match.Groups[1].Value) : 0;
            if (version < 1)
                throw new MigrationException(
                    $"'{fileName}' is not a valid migration name (expected NNNN_lowercase_description.sql, NNNN >= 0001). " +
                    "The runner never skips a .sql file silently; rename or remove it.");

            var sql = File.ReadAllText(path);
            if (TransactionControl().IsMatch(sql))
                throw new MigrationException(
                    $"'{fileName}' contains its own BEGIN/COMMIT/ROLLBACK. The runner already runs each migration in one " +
                    "transaction with its schema_migrations row; remove the transaction control statements.");

            files.Add(new MigrationFile(version, match.Groups[2].Value, sql, ComputeChecksum(sql), IsDestructive(sql)));
        }

        var duplicate = files.GroupBy(f => f.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new MigrationException(
                $"Duplicate migration version {duplicate.Key:D4}: {string.Join(", ", duplicate.Select(f => f.FileName))}");

        return files.OrderBy(f => f.Version).ToList();
    }

    /// SHA-256 of the file with CRLF normalised to LF, because Windows checkouts (core.autocrlf=true)
    /// and Linux CI/compose checkouts must agree.
    public static string ComputeChecksum(string sql) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n"))));

    private static bool IsDestructive(string sql)
    {
        foreach (var raw in sql.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (!line.StartsWith("--", StringComparison.Ordinal)) return false;
            if (line.Equals(DestructiveDirective, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/Sms.Tests.Unit --filter "FullyQualifiedName~MigrationFileTests"`
Expected: all pass (7 facts + theory rows).

- [ ] **Step 6: Commit**

```bash
git add db/Sms.PgMigrator db/postgres/migrations/.gitkeep Sms.slnx tests/Sms.Tests.Unit/Sms.Tests.Unit.csproj tests/Sms.Tests.Unit/PgMigrator/MigrationFileTests.cs
git commit -m "feat(db): Sms.PgMigrator project with strict migration-file loader

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `PgMigrator` engine (init / migrate / status, lock, per-migration transactions)

**Files:**
- Create: `db/Sms.PgMigrator/AdvisoryLock.cs`, `db/Sms.PgMigrator/PgMigrator.cs`
- Modify: `tests/Sms.Tests.Integration/Sms.Tests.Integration.csproj` (add `<ProjectReference Include="..\..\db\Sms.PgMigrator\Sms.PgMigrator.csproj" />`)
- Create: `tests/Sms.Tests.Integration/Migrations/MigrationTestDatabase.cs`, `tests/Sms.Tests.Integration/Migrations/PgMigratorTests.cs`

**Interfaces:**
- Consumes: `MigrationFile`, `MigrationException` (Task 2); `TestPostgresServer` (Task 1).
- Produces:
  - `MigratorOptions(string BaselineDirectory, string MigrationsDirectory, TimeSpan LockTimeout, bool BackupConfirmed)`
  - `AppliedMigration(int Version, string Name, string Checksum, DateTime AppliedAt)`
  - `MigrationStatus(bool HasBaseline, IReadOnlyList<AppliedMigration> Applied, IReadOnlyList<MigrationFile> Pending)`
  - `PgMigrator(string connectionString, MigratorOptions options, TextWriter log)` with:
    - `Task<IReadOnlyList<int>> InitAsync(CancellationToken ct = default)`
    - `Task<IReadOnlyList<int>> MigrateAsync(CancellationToken ct = default)`
    - `Task<MigrationStatus> StatusAsync(CancellationToken ct = default)`
    - `const long AdvisoryLockKey`
  - The return values are the versions applied by this call.

- [ ] **Step 1: Test database helper**

`tests/Sms.Tests.Integration/Migrations/MigrationTestDatabase.cs`:

```csharp
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
```

- [ ] **Step 2: Write the failing runner tests**

`tests/Sms.Tests.Integration/Migrations/PgMigratorTests.cs`:

```csharp
using FluentAssertions;
using Npgsql;
using Sms.PgMigrator;

namespace Sms.Tests.Integration.Migrations;

/// PgMigrator against real Postgres, with a tiny synthetic baseline and synthetic migrations so
/// each behaviour is isolated from the real schema. Each test gets its own sms_migtest_* databases.
/// In the "sql" collection so it never runs concurrently with PostgresFixture's cluster-wide
/// CREATE ROLE sms_app.
[Collection("sql")]
public sealed class PgMigratorTests : IAsyncLifetime
{
    private const string SyntheticBaseline =
        """
        CREATE SCHEMA dbo;
        CREATE TABLE dbo."Tenants" ("Id" uuid PRIMARY KEY, "Name" text NOT NULL);
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("sms_pgmigrator_").FullName;
    private readonly List<MigrationTestDatabase> _dbs = [];
    private string BaselineDir => Path.Combine(_root, "baseline");
    private string MigrationsDir => Path.Combine(_root, "migrations");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(BaselineDir);
        Directory.CreateDirectory(MigrationsDir);
        File.WriteAllText(Path.Combine(BaselineDir, "01_baseline.sql"), SyntheticBaseline);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var db in _dbs) await db.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private async Task<MigrationTestDatabase> NewDbAsync()
    {
        var db = await MigrationTestDatabase.CreateAsync();
        _dbs.Add(db);
        return db;
    }

    /// A database that already has the baseline but has never seen the runner, like sms_dev today.
    private async Task<MigrationTestDatabase> BaselineOnlyDbAsync()
    {
        var db = await NewDbAsync();
        await db.ExecAsync(SyntheticBaseline);
        return db;
    }

    private void Migration(string fileName, string sql) => File.WriteAllText(Path.Combine(MigrationsDir, fileName), sql);

    private PgMigrator Runner(string connectionString, bool backupConfirmed = false, int lockTimeoutSeconds = 30) =>
        new(connectionString,
            new MigratorOptions(BaselineDir, MigrationsDir, TimeSpan.FromSeconds(lockTimeoutSeconds), backupConfirmed),
            TextWriter.Null);

    [Fact]
    public async Task Init_applies_the_baseline_then_every_migration_and_records_them()
    {
        var db = await NewDbAsync();
        Migration("0001_widgets.sql", "CREATE TABLE dbo.widgets (id int PRIMARY KEY);");

        var applied = await Runner(db.ConnectionString).InitAsync();

        applied.Should().Equal(1);
        (await db.ExistsAsync("dbo.\"Tenants\"")).Should().BeTrue();
        (await db.ExistsAsync("dbo.widgets")).Should().BeTrue();
        (await db.AppliedVersionsAsync()).Should().Equal(1);
        (await db.ScalarAsync<string>("SELECT checksum FROM dbo.schema_migrations WHERE version = 1"))
            .Should().Be(MigrationFile.ComputeChecksum("CREATE TABLE dbo.widgets (id int PRIMARY KEY);"));
    }

    [Fact]
    public async Task Init_with_the_real_baseline_builds_the_full_schema()
    {
        var db = await NewDbAsync();
        var real = new PgMigrator(db.ConnectionString,
            new MigratorOptions(Path.Combine(AppContext.BaseDirectory, "baseline"), MigrationsDir, TimeSpan.FromSeconds(30), false),
            TextWriter.Null);

        (await real.InitAsync()).Should().BeEmpty();

        (await db.ScalarAsync<long>(
            "SELECT count(*) FROM pg_tables WHERE schemaname = 'dbo' AND tablename <> 'schema_migrations'"))
            .Should().BeGreaterThanOrEqualTo(121);
        (await db.ExistsAsync("dbo.schema_migrations")).Should().BeTrue();
    }

    [Fact]
    public async Task Migrate_on_an_existing_database_applies_pending_without_rerunning_the_baseline()
    {
        var db = await BaselineOnlyDbAsync();
        await db.ExecAsync("""INSERT INTO dbo."Tenants" VALUES ('00000000-0000-0000-0000-000000000001', 'existing school')""");
        Migration("0001_widgets.sql", "CREATE TABLE dbo.widgets (id int PRIMARY KEY);");

        // Re-running the baseline would fail on CREATE SCHEMA dbo, so success alone proves it didn't.
        var applied = await Runner(db.ConnectionString).MigrateAsync();

        applied.Should().Equal(1);
        (await db.ScalarAsync<string>("""SELECT "Name" FROM dbo."Tenants" """)).Should().Be("existing school");
        (await db.AppliedVersionsAsync()).Should().Equal(1);
    }

    [Fact]
    public async Task Migrations_apply_in_numeric_order()
    {
        var db = await BaselineOnlyDbAsync();
        Migration("0010_ten.sql", "INSERT INTO dbo.steps (n) VALUES (10);");
        Migration("0002_two.sql", "INSERT INTO dbo.steps (n) VALUES (2);");
        Migration("0001_table.sql", "CREATE TABLE dbo.steps (seq serial PRIMARY KEY, n int NOT NULL);");

        (await Runner(db.ConnectionString).MigrateAsync()).Should().Equal(1, 2, 10);

        (await db.ScalarAsync<string>("SELECT string_agg(n::text, ',' ORDER BY seq) FROM dbo.steps")).Should().Be("2,10");
    }

    [Fact]
    public async Task Already_applied_migrations_are_skipped()
    {
        var db = await BaselineOnlyDbAsync();
        Migration("0001_seed.sql", "CREATE TABLE dbo.once (id int); INSERT INTO dbo.once VALUES (1);");

        (await Runner(db.ConnectionString).MigrateAsync()).Should().Equal(1);
        (await Runner(db.ConnectionString).MigrateAsync()).Should().BeEmpty();

        (await db.ScalarAsync<long>("SELECT count(*) FROM dbo.once")).Should().Be(1);
    }

    [Fact]
    public async Task Failed_migration_is_rolled_back_not_recorded_and_stops_the_run()
    {
        var db = await BaselineOnlyDbAsync();
        Migration("0001_ok.sql", "CREATE TABLE dbo.a (id int);");
        Migration("0002_broken.sql", "CREATE TABLE dbo.half_done (id int);\nSELECT 1 / 0;");
        Migration("0003_after.sql", "CREATE TABLE dbo.c (id int);");

        var run = () => Runner(db.ConnectionString).MigrateAsync();

        (await run.Should().ThrowAsync<MigrationException>())
            .WithMessage("*0002_broken.sql*NOT recorded*");
        (await db.AppliedVersionsAsync()).Should().Equal(1);
        (await db.ExistsAsync("dbo.half_done")).Should().BeFalse("the failed migration's DDL must roll back");
        (await db.ExistsAsync("dbo.c")).Should().BeFalse("no migration may run after a failure");

        // Not recorded, so it may be fixed in place. The lock was released, so a 1 s timeout is enough.
        Migration("0002_broken.sql", "CREATE TABLE dbo.half_done (id int);");
        (await Runner(db.ConnectionString, lockTimeoutSeconds: 1).MigrateAsync()).Should().Equal(2, 3);
    }

    [Fact]
    public async Task Concurrent_runs_are_serialized_and_apply_each_migration_once()
    {
        var db = await BaselineOnlyDbAsync();
        Migration("0001_slow.sql", "SELECT pg_sleep(2); CREATE TABLE dbo.once (id int); INSERT INTO dbo.once VALUES (1);");

        var results = await Task.WhenAll(
            Runner(db.ConnectionString).MigrateAsync(),
            Runner(db.ConnectionString).MigrateAsync());

        results.Select(r => r.Count).Should().BeEquivalentTo([1, 0]);
        (await db.ScalarAsync<long>("SELECT count(*) FROM dbo.once")).Should().Be(1);
        (await db.AppliedVersionsAsync()).Should().Equal(1);
    }

    [Fact]
    public async Task A_run_gives_up_with_a_clear_error_when_the_lock_is_held()
    {
        var db = await BaselineOnlyDbAsync();
        Migration("0001_widgets.sql", "CREATE TABLE dbo.widgets (id int);");
        await using var holder = new NpgsqlConnection(db.ConnectionString);
        await holder.OpenAsync();
        await using (var take = new NpgsqlCommand($"SELECT pg_advisory_lock({PgMigrator.AdvisoryLockKey})", holder))
            await take.ExecuteNonQueryAsync();

        var run = () => Runner(db.ConnectionString, lockTimeoutSeconds: 2).MigrateAsync();

        await run.Should().ThrowAsync<MigrationException>().WithMessage("*migration lock*Nothing was applied*");
        (await db.ExistsAsync("dbo.widgets")).Should().BeFalse();
        (await db.ExistsAsync("dbo.schema_migrations")).Should().BeFalse();
    }

    [Fact]
    public async Task Init_refuses_a_database_that_already_has_the_baseline()
    {
        var db = await BaselineOnlyDbAsync();
        var run = () => Runner(db.ConnectionString).InitAsync();
        await run.Should().ThrowAsync<MigrationException>().WithMessage("*init refused*Use 'migrate'*");
        (await db.ExistsAsync("dbo.schema_migrations")).Should().BeFalse();
    }

    [Fact]
    public async Task Migrate_refuses_an_empty_database()
    {
        var db = await NewDbAsync();
        var run = () => Runner(db.ConnectionString).MigrateAsync();
        await run.Should().ThrowAsync<MigrationException>().WithMessage("*migrate refused*Use 'init'*");
        (await db.ScalarAsync<bool>("SELECT to_regnamespace('dbo') IS NULL")).Should().BeTrue();
    }

    [Fact]
    public async Task Editing_an_applied_migration_is_refused()
    {
        var db = await BaselineOnlyDbAsync();
        Migration("0001_widgets.sql", "CREATE TABLE dbo.widgets (id int);");
        await Runner(db.ConnectionString).MigrateAsync();
        Migration("0001_widgets.sql", "CREATE TABLE dbo.widgets (id bigint);");

        var run = () => Runner(db.ConnectionString).MigrateAsync();

        await run.Should().ThrowAsync<MigrationException>().WithMessage("*0001_widgets.sql*checksum*");
    }

    [Fact]
    public async Task Deleting_an_applied_migration_file_is_refused()
    {
        var db = await BaselineOnlyDbAsync();
        Migration("0001_widgets.sql", "CREATE TABLE dbo.widgets (id int);");
        await Runner(db.ConnectionString).MigrateAsync();
        File.Delete(Path.Combine(MigrationsDir, "0001_widgets.sql"));

        var run = () => Runner(db.ConnectionString).MigrateAsync();

        await run.Should().ThrowAsync<MigrationException>().WithMessage("*0001_widgets*file is missing*");
    }

    [Fact]
    public async Task Destructive_migration_requires_backup_confirmation_and_nothing_applies_without_it()
    {
        var db = await BaselineOnlyDbAsync();
        Migration("0001_add.sql", "CREATE TABLE dbo.x (id int, legacy text);");
        Migration("0002_drop.sql", "-- 0002: drop legacy column\n-- sms:destructive\nALTER TABLE dbo.x DROP COLUMN legacy;");

        var run = () => Runner(db.ConnectionString).MigrateAsync();

        await run.Should().ThrowAsync<MigrationException>().WithMessage("*0002_drop.sql*--backup-confirmed*Nothing was applied*");
        (await db.AppliedVersionsAsync()).Should().BeEmpty();
        (await db.ExistsAsync("dbo.x")).Should().BeFalse();

        (await Runner(db.ConnectionString, backupConfirmed: true).MigrateAsync()).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Status_is_read_only_and_reports_pending()
    {
        var db = await BaselineOnlyDbAsync();
        Migration("0001_widgets.sql", "CREATE TABLE dbo.widgets (id int);");

        var status = await Runner(db.ConnectionString).StatusAsync();

        status.HasBaseline.Should().BeTrue();
        status.Applied.Should().BeEmpty();
        status.Pending.Select(p => p.Version).Should().Equal(1);
        (await db.ExistsAsync("dbo.schema_migrations")).Should().BeFalse("status must not write");
    }

    [Fact]
    public async Task Migrate_as_the_app_role_is_refused_with_a_clear_message()
    {
        var db = await BaselineOnlyDbAsync();
        Migration("0001_widgets.sql", "CREATE TABLE dbo.widgets (id int);");

        var run = () => Runner(TestPostgresServer.AppConnectionString(db.Name)).MigrateAsync();

        await run.Should().ThrowAsync<MigrationException>().WithMessage("*schema owner*");
        (await db.ExistsAsync("dbo.widgets")).Should().BeFalse();
    }
}
```

Add `<ProjectReference Include="..\..\db\Sms.PgMigrator\Sms.PgMigrator.csproj" />` to `tests/Sms.Tests.Integration/Sms.Tests.Integration.csproj`.

Note: `Migrate_as_the_app_role…` needs `sms_app` to be able to `CONNECT` to the migtest DB. That's the PUBLIC default. The role exists cluster-wide once PostgresFixture has run, and it always has because this class is in the `sql` collection.

- [ ] **Step 3: Run to verify failure**

Run: `SMS_TEST_PG_PASSWORD=12345678 dotnet build tests/Sms.Tests.Integration`
Expected: build error `The type or namespace name 'MigratorOptions' could not be found`.

- [ ] **Step 4: Implement `AdvisoryLock.cs`**

```csharp
using Npgsql;

namespace Sms.PgMigrator;

/// Session-level advisory lock serialising migration runs against one database (advisory locks
/// are per database). Released explicitly on dispose; Postgres also drops it if the connection dies.
internal sealed class AdvisoryLock : IAsyncDisposable
{
    private readonly NpgsqlConnection _conn;
    private readonly long _key;

    private AdvisoryLock(NpgsqlConnection conn, long key) { _conn = conn; _key = key; }

    public static async Task<AdvisoryLock> AcquireAsync(
        NpgsqlConnection conn, long key, TimeSpan timeout, TextWriter log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var announced = false;
        while (true)
        {
            await using (var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@k)", conn))
            {
                cmd.Parameters.AddWithValue("k", key);
                if ((bool)(await cmd.ExecuteScalarAsync(ct))!)
                    return new AdvisoryLock(conn, key);
            }
            if (DateTime.UtcNow >= deadline)
                throw new MigrationException(
                    $"Another run holds the migration lock on this database; gave up after {timeout.TotalSeconds:0}s. Nothing was applied.");
            if (!announced)
            {
                log.WriteLine("Another migration run is in progress; waiting for its lock...");
                announced = true;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn.State != System.Data.ConnectionState.Open) return;
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", _conn);
            cmd.Parameters.AddWithValue("k", _key);
            await cmd.ExecuteScalarAsync();
        }
        catch (NpgsqlException)
        {
            // Connection is going away; Postgres releases session locks with it.
        }
    }
}
```

- [ ] **Step 5: Implement `PgMigrator.cs`**

```csharp
using Npgsql;

namespace Sms.PgMigrator;

public sealed record MigratorOptions(string BaselineDirectory, string MigrationsDirectory, TimeSpan LockTimeout, bool BackupConfirmed);

public sealed record AppliedMigration(int Version, string Name, string Checksum, DateTime AppliedAt);

public sealed record MigrationStatus(bool HasBaseline, IReadOnlyList<AppliedMigration> Applied, IReadOnlyList<MigrationFile> Pending);

/// Forward-only Postgres schema runner.
///   init    - empty database: the immutable db/postgres/*.sql baseline (one transaction), then every migration.
///   migrate - database that already has the baseline: pending db/postgres/migrations/*.sql only.
///   status  - read-only report.
/// Each migration runs in its own transaction together with its dbo.schema_migrations row, so a
/// failure leaves it unrecorded with none of its DDL applied. Runs against one database are
/// serialised with a session advisory lock. Connect as the schema owner, never as sms_app.
public sealed class PgMigrator(string connectionString, MigratorOptions options, TextWriter log)
{
    /// Fixed forever: every runner version must agree on it or two deploys could run at once.
    public const long AdvisoryLockKey = 7_365_202_600_001;

    private const string TrackingTableDdl =
        """
        CREATE TABLE IF NOT EXISTS dbo.schema_migrations (
            version     int PRIMARY KEY,
            name        text NOT NULL,
            checksum    text NOT NULL,
            applied_at  timestamptz NOT NULL DEFAULT now()
        );
        """;

    public async Task<IReadOnlyList<int>> InitAsync(CancellationToken ct = default)
    {
        var migrations = MigrationFile.LoadDirectory(options.MigrationsDirectory);
        var baseline = LoadBaseline();
        await using var conn = await OpenAsync(ct);
        await using var _ = await AdvisoryLock.AcquireAsync(conn, AdvisoryLockKey, options.LockTimeout, log, ct);

        if (await ScalarAsync<bool>(conn, "SELECT to_regnamespace('dbo') IS NOT NULL", ct))
            throw new MigrationException(
                "init refused: the target already has a dbo schema. Use 'migrate' for an existing database. Nothing was changed.");

        log.WriteLine($"Fresh database: applying {baseline.Count} baseline file(s) in one transaction.");
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            foreach (var (name, sql) in baseline)
            {
                try
                {
                    await ExecAsync(conn, tx, sql, ct);
                }
                catch (PostgresException ex)
                {
                    throw new MigrationException(
                        $"init failed in baseline file {name}; the whole baseline was rolled back and the database is still empty. {ex.MessageText}", ex);
                }
                log.WriteLine($"  baseline {name}");
            }
            await ExecAsync(conn, tx, TrackingTableDdl, ct);
            await tx.CommitAsync(ct);
        }

        return await ApplyPendingAsync(conn, migrations, requireBackupForDestructive: false, ct);
    }

    public async Task<IReadOnlyList<int>> MigrateAsync(CancellationToken ct = default)
    {
        var migrations = MigrationFile.LoadDirectory(options.MigrationsDirectory);
        await using var conn = await OpenAsync(ct);
        await using var _ = await AdvisoryLock.AcquireAsync(conn, AdvisoryLockKey, options.LockTimeout, log, ct);

        if (!await HasBaselineAsync(conn, ct))
            throw new MigrationException(
                "migrate refused: the target has no baseline (dbo.\"Tenants\" does not exist). Use 'init' for an empty database. Nothing was changed.");

        try
        {
            await ExecAsync(conn, null, TrackingTableDdl, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            throw new MigrationException(
                "Could not create dbo.schema_migrations: this role lacks DDL rights. Connect as the schema owner " +
                $"(never the API's sms_app role). Nothing was applied. {ex.MessageText}", ex);
        }

        return await ApplyPendingAsync(conn, migrations, requireBackupForDestructive: true, ct);
    }

    public async Task<MigrationStatus> StatusAsync(CancellationToken ct = default)
    {
        var migrations = MigrationFile.LoadDirectory(options.MigrationsDirectory);
        await using var conn = await OpenAsync(ct);
        var hasBaseline = await HasBaselineAsync(conn, ct);
        var applied = await ReadAppliedAsync(conn, ct);
        VerifyHistory(migrations, applied);
        var pending = migrations.Where(m => applied.All(a => a.Version != m.Version)).ToList();

        log.WriteLine(hasBaseline ? "Baseline: present." : "Baseline: MISSING (empty database, use init).");
        foreach (var a in applied) log.WriteLine($"  applied  {a.Version:D4}_{a.Name}  {a.AppliedAt:u}");
        foreach (var p in pending) log.WriteLine($"  pending  {p.FileName}{(p.Destructive ? "  [destructive]" : "")}");
        if (pending.Count == 0) log.WriteLine("No pending migrations.");
        return new MigrationStatus(hasBaseline, applied, pending);
    }

    private async Task<IReadOnlyList<int>> ApplyPendingAsync(
        NpgsqlConnection conn, IReadOnlyList<MigrationFile> migrations, bool requireBackupForDestructive, CancellationToken ct)
    {
        var applied = await ReadAppliedAsync(conn, ct);
        VerifyHistory(migrations, applied);
        var pending = migrations.Where(m => applied.All(a => a.Version != m.Version)).ToList();

        if (requireBackupForDestructive && !options.BackupConfirmed && pending.FirstOrDefault(m => m.Destructive) is { } destructive)
            throw new MigrationException(
                $"{destructive.FileName} is marked '{MigrationFile.DestructiveDirective}'. Take and verify a backup " +
                "(docs/runbooks/postgres-migrations.md), then re-run with --backup-confirmed. Nothing was applied.");

        if (pending.Count == 0)
        {
            log.WriteLine("No pending migrations.");
            return [];
        }

        var done = new List<int>();
        foreach (var m in pending)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                await ExecAsync(conn, tx, m.Sql, ct);
                await using var record = new NpgsqlCommand(
                    "INSERT INTO dbo.schema_migrations (version, name, checksum) VALUES (@v, @n, @c)", conn, tx);
                record.Parameters.AddWithValue("v", m.Version);
                record.Parameters.AddWithValue("n", m.Name);
                record.Parameters.AddWithValue("c", m.Checksum);
                await record.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                try { await tx.RollbackAsync(CancellationToken.None); } catch (Exception) { /* connection lost: server rolls back */ }
                throw new MigrationException(
                    $"{m.FileName} failed and was rolled back; it is NOT recorded as applied and no later migration ran. " +
                    (ex is PostgresException pg ? pg.MessageText : ex.Message), ex);
            }
            log.WriteLine($"Applied {m.FileName}");
            done.Add(m.Version);
        }
        return done;
    }

    private static void VerifyHistory(IReadOnlyList<MigrationFile> files, IReadOnlyList<AppliedMigration> applied)
    {
        foreach (var a in applied)
        {
            var file = files.FirstOrDefault(f => f.Version == a.Version)
                ?? throw new MigrationException(
                    $"Migration {a.Version:D4}_{a.Name} is recorded as applied but its file is missing. Applied migrations must never be deleted.");
            if (file.Checksum != a.Checksum)
                throw new MigrationException(
                    $"{file.FileName} was edited after it was applied (checksum mismatch). Never edit an applied migration; add a new one.");
        }
    }

    private IReadOnlyList<(string Name, string Sql)> LoadBaseline()
    {
        if (!Directory.Exists(options.BaselineDirectory))
            throw new MigrationException($"Baseline directory not found: {options.BaselineDirectory}");
        var files = Directory.GetFiles(options.BaselineDirectory, "*.sql")
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)))
            .ToList();
        if (files.Count == 0)
            throw new MigrationException($"Baseline directory has no .sql files: {options.BaselineDirectory}");
        return files;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        // No pooling: the advisory lock is tied to this one physical session.
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT current_database(), current_user::text, current_setting('server_version')", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        log.WriteLine($"Target: host={builder.Host} port={builder.Port} database={r.GetString(0)} user={r.GetString(1)} server=PostgreSQL {r.GetString(2)}");
        return conn;
    }

    // Existence checks read pg_catalog directly (readable by every role), not to_regclass, so a
    // role without USAGE on dbo gets the clear privilege error below instead of "no baseline".
    private static Task<bool> TableExistsAsync(NpgsqlConnection conn, string table, CancellationToken ct) =>
        ScalarAsync<bool>(conn,
            $"SELECT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'dbo' AND c.relname = '{table}')",
            ct);

    private static Task<bool> HasBaselineAsync(NpgsqlConnection conn, CancellationToken ct) =>
        TableExistsAsync(conn, "Tenants", ct);

    private static async Task<IReadOnlyList<AppliedMigration>> ReadAppliedAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "schema_migrations", ct))
            return [];
        var rows = new List<AppliedMigration>();
        await using var cmd = new NpgsqlCommand(
            "SELECT version, name, checksum, applied_at FROM dbo.schema_migrations ORDER BY version", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new AppliedMigration(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetDateTime(3)));
        return rows;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        // No timeout: schema changes on real data can legitimately run long.
        await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

- [ ] **Step 6: Run the runner tests**

Run: `SMS_TEST_PG_PASSWORD=12345678 dotnet build tests/Sms.Tests.Integration && SMS_TEST_PG_PASSWORD=12345678 dotnet test tests/Sms.Tests.Integration --no-build --filter "FullyQualifiedName~PgMigratorTests"`
Expected: 15 passed.

- If `Init_with_the_real_baseline…` fails because `bin/.../baseline` is missing, the content items didn't flow from the project reference. Fix it by adding `<None Include="..\..\db\postgres\*.sql" LinkBase="baseline" CopyToOutputDirectory="PreserveNewest" />` and `<None Include="..\..\db\postgres\migrations\*.sql" LinkBase="migrations" CopyToOutputDirectory="PreserveNewest" />` to the integration csproj.
- If it fails inside a baseline file (the whole baseline runs in one transaction here, unlike the old fixture's autocommit per file), don't edit the baseline. Report the file and error; that's a design issue to raise with the user.

- [ ] **Step 7: Confirm no stray test databases remain**

Run (PowerShell): `$env:PGPASSWORD='12345678'; & "C:\Program Files\PostgreSQL\18\bin\psql.exe" -h localhost -U postgres -d postgres -tAc "SELECT count(*) FROM pg_database WHERE datname LIKE 'sms_migtest_%'"`
Expected: `0`

- [ ] **Step 8: Commit**

```bash
git add db/Sms.PgMigrator tests/Sms.Tests.Integration/Sms.Tests.Integration.csproj tests/Sms.Tests.Integration/Migrations/MigrationTestDatabase.cs tests/Sms.Tests.Integration/Migrations/PgMigratorTests.cs
git commit -m "feat(db): PgMigrator init/migrate/status with advisory lock and per-migration transactions

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: CLI

**Files:**
- Modify: `db/Sms.PgMigrator/Cli.cs` (replace the stub)
- Test: `tests/Sms.Tests.Unit/PgMigrator/CliTests.cs`

**Interfaces:**
- Consumes: `PgMigrator`, `MigratorOptions`, `MigrationException` (Task 3).
- Produces: `CliOptions(string Command, string ConnectionString, string BaselineDirectory, string MigrationsDirectory, TimeSpan LockTimeout, bool BackupConfirmed)`; `Cli.Parse(string[] args, string? envConnection, string baseDirectory, out string? error) : CliOptions?`; `Cli.ConnectionEnvVar = "SMS_MIGRATOR_CONNECTION"`; `Cli.Main(string[]) : Task<int>`.

- [ ] **Step 1: Write the failing tests**

`tests/Sms.Tests.Unit/PgMigrator/CliTests.cs`:

```csharp
using FluentAssertions;
using Sms.PgMigrator;

namespace Sms.Tests.Unit.PgMigrator;

public class CliTests
{
    private const string Base = "/app";

    [Fact]
    public void Connection_argument_wins_over_the_environment()
    {
        var o = Cli.Parse(["migrate", "--connection", "Host=arg"], "Host=env", Base, out _);
        o!.ConnectionString.Should().Be("Host=arg");
    }

    [Fact]
    public void Falls_back_to_the_environment_variable()
    {
        var o = Cli.Parse(["status"], "Host=env", Base, out _);
        o!.ConnectionString.Should().Be("Host=env");
        o.Command.Should().Be("status");
    }

    [Fact]
    public void No_connection_anywhere_is_an_error()
    {
        Cli.Parse(["migrate"], null, Base, out var error).Should().BeNull();
        error.Should().Contain(Cli.ConnectionEnvVar);
    }

    [Fact]
    public void Defaults_directories_next_to_the_executable_and_a_60s_lock_timeout()
    {
        var o = Cli.Parse(["init"], "Host=env", Base, out _)!;
        o.BaselineDirectory.Should().Be(Path.Combine(Base, "baseline"));
        o.MigrationsDirectory.Should().Be(Path.Combine(Base, "migrations"));
        o.LockTimeout.Should().Be(TimeSpan.FromSeconds(60));
        o.BackupConfirmed.Should().BeFalse();
    }

    [Fact]
    public void Parses_every_option()
    {
        var o = Cli.Parse(
            ["migrate", "--baseline-dir", "b", "--migrations-dir", "m", "--lock-timeout-seconds", "5", "--backup-confirmed"],
            "Host=env", Base, out _)!;
        o.BaselineDirectory.Should().Be("b");
        o.MigrationsDirectory.Should().Be("m");
        o.LockTimeout.Should().Be(TimeSpan.FromSeconds(5));
        o.BackupConfirmed.Should().BeTrue();
    }

    [Theory]
    [InlineData(new string[0])]
    [InlineData(new[] { "upgrade" })]
    [InlineData(new[] { "migrate", "--nope", "x" })]
    [InlineData(new[] { "migrate", "--connection" })]
    [InlineData(new[] { "migrate", "--lock-timeout-seconds", "0" })]
    [InlineData(new[] { "migrate", "--lock-timeout-seconds", "abc" })]
    public void Rejects_bad_arguments(string[] args)
    {
        Cli.Parse(args, "Host=env", Base, out var error).Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Sms.Tests.Unit --filter "FullyQualifiedName~CliTests"`
Expected: build error `'Cli' does not contain a definition for 'Parse'`.

- [ ] **Step 3: Implement `Cli.cs`**

```csharp
using Npgsql;

namespace Sms.PgMigrator;

public sealed record CliOptions(
    string Command, string ConnectionString, string BaselineDirectory, string MigrationsDirectory,
    TimeSpan LockTimeout, bool BackupConfirmed);

/// Entry point. Exit codes: 0 success, 1 usage error, 2 refused or failed (the message says what
/// state the database is in). See docs/runbooks/postgres-migrations.md.
public static class Cli
{
    /// Deliberately not ConnectionStrings__Sql: that is the API's non-superuser sms_app string, and
    /// migrations need the schema owner.
    public const string ConnectionEnvVar = "SMS_MIGRATOR_CONNECTION";

    private const string Usage =
        """
        Usage: Sms.PgMigrator <init|migrate|status> [--connection "<npgsql connection string>"]
                              [--baseline-dir <dir>] [--migrations-dir <dir>]
                              [--lock-timeout-seconds <n>] [--backup-confirmed]
          init     empty database    -> baseline (db/postgres/*.sql) + every migration
          migrate  existing database -> pending migrations (db/postgres/migrations/*.sql) only
          status   read-only: applied and pending migrations
        The connection falls back to SMS_MIGRATOR_CONNECTION and must be the schema owner role,
        never the API's sms_app role.
        """;

    public static async Task<int> Main(string[] args)
    {
        var o = Parse(args, Environment.GetEnvironmentVariable(ConnectionEnvVar), AppContext.BaseDirectory, out var error);
        if (o is null)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(Usage);
            return 1;
        }

        var migrator = new PgMigrator(o.ConnectionString,
            new MigratorOptions(o.BaselineDirectory, o.MigrationsDirectory, o.LockTimeout, o.BackupConfirmed), Console.Out);
        try
        {
            switch (o.Command)
            {
                case "init": await migrator.InitAsync(); break;
                case "migrate": await migrator.MigrateAsync(); break;
                default: await migrator.StatusAsync(); break;
            }
            return 0;
        }
        catch (MigrationException ex)
        {
            Console.Error.WriteLine($"FAILED: {ex.Message}");
            return 2;
        }
        catch (NpgsqlException ex)
        {
            Console.Error.WriteLine($"FAILED: database error, nothing further was attempted: {ex.Message}");
            return 2;
        }
    }

    public static CliOptions? Parse(string[] args, string? envConnection, string baseDirectory, out string? error)
    {
        error = null;
        if (args.Length == 0 || args[0] is not ("init" or "migrate" or "status"))
        {
            error = "The first argument must be init, migrate or status.";
            return null;
        }

        string? connection = null;
        var baseline = Path.Combine(baseDirectory, "baseline");
        var migrations = Path.Combine(baseDirectory, "migrations");
        var lockSeconds = 60;
        var backupConfirmed = false;

        for (var i = 1; i < args.Length; i++)
        {
            var flag = args[i];
            if (flag == "--backup-confirmed")
            {
                backupConfirmed = true;
                continue;
            }
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Option '{flag}' needs a value.";
                return null;
            }
            var value = args[++i];
            switch (flag)
            {
                case "--connection": connection = value; break;
                case "--baseline-dir": baseline = value; break;
                case "--migrations-dir": migrations = value; break;
                case "--lock-timeout-seconds" when int.TryParse(value, out var s) && s > 0: lockSeconds = s; break;
                default:
                    error = $"Unknown option '{flag}' or invalid value '{value}'.";
                    return null;
            }
        }

        connection = string.IsNullOrWhiteSpace(connection) ? envConnection : connection;
        if (string.IsNullOrWhiteSpace(connection))
        {
            error = $"No connection string: pass --connection or set {ConnectionEnvVar}.";
            return null;
        }

        return new CliOptions(args[0], connection, baseline, migrations, TimeSpan.FromSeconds(lockSeconds), backupConfirmed);
    }
}
```

- [ ] **Step 4: Run tests and a CLI smoke check**

Run: `dotnet test tests/Sms.Tests.Unit --filter "FullyQualifiedName~CliTests|FullyQualifiedName~MigrationFileTests"`
Expected: all pass.

Run: `dotnet run --project db/Sms.PgMigrator -- upgrade; echo "exit=$?"`
Expected: the error plus usage printed, and `exit=1`.

- [ ] **Step 5: Commit**

```bash
git add db/Sms.PgMigrator/Cli.cs tests/Sms.Tests.Unit/PgMigrator/CliTests.cs
git commit -m "feat(db): PgMigrator command line (init/migrate/status)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Migrations 0001/0002, fixture on the runner, delete-path tests

**Files:**
- Create: `db/postgres/migrations/0001_client_delete_fix.sql`, `db/postgres/migrations/0002_drop_trip_ping_bulk_insert.sql`
- Modify: `tests/Sms.Tests.Integration/PostgresFixture.cs` (build via `PgMigrator.InitAsync`), `tests/Sms.Tests.Integration/Sms.Tests.Integration.csproj` (remove the `postgres-schema` item), `tests/Sms.Tests.Integration/Catre/ClientDeleteTests.cs` (add the orphan check), `src/Sms.Modules.Transport/TransportModule.cs:100-103` (comment only)
- Create: `tests/Sms.Tests.Integration/Migrations/RealMigrationsTests.cs`

**Interfaces:**
- Consumes: `PgMigrator`, `MigratorOptions` (Task 3); `TestPostgresServer` (Task 1); `MigrationTestDatabase` (Task 3).

- [ ] **Step 1: Extend `ClientDeleteTests` with the four restored dependents**

Add to the class:

```csharp
    // Tables the baseline port of Client_Delete forgot to clear (compared with the source proc,
    // db/Sms.Migrations/procs/catredel/Client_Delete.sql). Migration 0001 restores them.
    private static readonly string[] RestoredDependents =
        ["UserAppSettings", "PeriodAttendanceRecords", "PeriodAttendanceAudit", "Achievements"];

    private async Task SeedRestoredDependentsAsync(Guid tenantId)
    {
        var ctx = new TenantContext();
        ctx.Set(tenantId, Guid.NewGuid(), false);
        await using var conn = await new NpgsqlConnectionFactory(fx.ConnectionString, ctx).OpenAsync();
        await conn.ExecuteAsync(
            """
            INSERT INTO "dbo"."UserAppSettings" ("UserId", "TenantId") VALUES (@u, @t);
            INSERT INTO "dbo"."PeriodAttendanceRecords" ("Id", "TenantId", "ClassId", "StudentId", "Date", "Period", "Subject", "Status")
                VALUES (@r, @t, @c, @s, DATE '2026-09-01', 1, 'Maths', 'present');
            INSERT INTO "dbo"."PeriodAttendanceAudit" ("TenantId", "RecordId", "ClassId", "StudentId", "Date", "Period", "Subject", "ToStatus")
                VALUES (@t, @r, @c, @s, DATE '2026-09-01', 1, 'Maths', 'present');
            INSERT INTO "dbo"."Achievements" ("TenantId", "StudentId", "Title", "AwardedOn")
                VALUES (@t, @s, 'Chess', DATE '2026-09-01');
            """,
            new { t = tenantId, u = Guid.NewGuid(), r = Guid.NewGuid(), c = Guid.NewGuid(), s = Guid.NewGuid() });
    }
```

Then, in `Deleting_an_empty_client_removes_it_and_its_dependent_rows`, insert after the Subscriptions precondition assertion:

```csharp
        var otherSchool = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, otherSchool);
        await SeedRestoredDependentsAsync(id);
        await SeedRestoredDependentsAsync(otherSchool);
```

And after the existing post-delete assertions:

```csharp
        foreach (var table in RestoredDependents)
        {
            (await CountAsync($"""SELECT count(*) FROM "dbo"."{table}" WHERE "TenantId" = @tenantId""", id))
                .Should().Be(0, $"{table} rows of the deleted school must not be orphaned");
            (await CountAsync($"""SELECT count(*) FROM "dbo"."{table}" WHERE "TenantId" = @tenantId""", otherSchool))
                .Should().Be(1, $"{table} rows of other schools must survive");
        }
```

`CountAsync` runs with platform context (existing helper), so RLS doesn't hide the rows being counted.

- [ ] **Step 2: Write the real-migration tests**

`tests/Sms.Tests.Integration/Migrations/RealMigrationsTests.cs`:

```csharp
using FluentAssertions;
using Sms.PgMigrator;

namespace Sms.Tests.Integration.Migrations;

/// The real baseline and the real db/postgres/migrations, both as a fresh database (init) and as
/// an existing baseline-only database like sms_dev (migrate).
[Collection("sql")]
public sealed class RealMigrationsTests : IAsyncLifetime
{
    private static readonly string Baseline = Path.Combine(AppContext.BaseDirectory, "baseline");
    private static readonly string Migrations = Path.Combine(AppContext.BaseDirectory, "migrations");
    private readonly string _empty = Directory.CreateTempSubdirectory("sms_nomigrations_").FullName;
    private readonly List<MigrationTestDatabase> _dbs = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var db in _dbs) await db.DisposeAsync();
        Directory.Delete(_empty, recursive: true);
    }

    private async Task<MigrationTestDatabase> NewDbAsync()
    {
        var db = await MigrationTestDatabase.CreateAsync();
        _dbs.Add(db);
        return db;
    }

    private static PgMigrator Runner(MigrationTestDatabase db, string migrationsDir) =>
        new(db.ConnectionString, new MigratorOptions(Baseline, migrationsDir, TimeSpan.FromSeconds(60), false), TextWriter.Null);

    private static async Task AssertMigratedStateAsync(MigrationTestDatabase db)
    {
        (await db.ScalarAsync<string>("SELECT pg_get_function_identity_arguments('dbo.client_delete'::regproc)"))
            .Should().Be("id uuid", "ClientRepository binds the argument as \"id\" => @Id");
        (await db.ScalarAsync<bool>("SELECT has_function_privilege('sms_app', 'dbo.client_delete(uuid)', 'EXECUTE')"))
            .Should().BeTrue();
        (await db.ScalarAsync<bool>("SELECT to_regprocedure('dbo.trip_ping_bulk_insert(uuid,uuid,jsonb)') IS NULL"))
            .Should().BeTrue("0002 drops the dead worked example");
        (await db.ScalarAsync<bool>("SELECT to_regprocedure('dbo.tripping_bulkinsert(uuid,uuid,text)') IS NOT NULL"))
            .Should().BeTrue("the live TripPing_BulkInsert function must be untouched");
    }

    [Fact]
    public async Task Init_applies_the_baseline_and_records_every_real_migration()
    {
        var db = await NewDbAsync();

        var applied = await Runner(db, Migrations).InitAsync();

        applied.Should().Equal(MigrationFile.LoadDirectory(Migrations).Select(m => m.Version));
        applied.Should().StartWith([1, 2]);
        (await db.AppliedVersionsAsync()).Should().Equal(applied);
        await AssertMigratedStateAsync(db);
    }

    [Fact]
    public async Task Migrate_brings_a_baseline_only_database_up_to_date()
    {
        var db = await NewDbAsync();
        (await Runner(db, _empty).InitAsync()).Should().BeEmpty();
        (await db.ScalarAsync<string>("SELECT pg_get_function_identity_arguments('dbo.client_delete'::regproc)"))
            .Should().Be("p_id uuid", "precondition: the unfixed baseline signature");

        var applied = await Runner(db, Migrations).MigrateAsync();

        applied.Should().StartWith([1, 2]);
        await AssertMigratedStateAsync(db);
    }
}
```

Before writing it, check `tripping_bulkinsert`'s real signature: `grep -n "FUNCTION dbo.tripping_bulkinsert" -A4 db/postgres/14_transport_procs.sql`. Adjust the `to_regprocedure` argument list to match.

- [ ] **Step 3: Run to verify failure** (the fixture still uses the old file loop at this point)

Run: `SMS_TEST_PG_PASSWORD=12345678 dotnet build tests/Sms.Tests.Integration && SMS_TEST_PG_PASSWORD=12345678 dotnet test tests/Sms.Tests.Integration --no-build --filter "FullyQualifiedName~ClientDeleteTests|FullyQualifiedName~RealMigrationsTests"`

Expected:
- the 3 `ClientDeleteTests` FAIL with a 500;
- both `RealMigrationsTests` FAIL, because there are no real migrations yet (the `migrations` output folder is missing or empty).

- [ ] **Step 4: Write migration 0001**

`db/postgres/migrations/0001_client_delete_fix.sql`:

```sql
-- 0001: dbo.client_delete (school deletion, DELETE /v1/clients/{id} and DELETE /v1/me/schools/{id}).
--
-- 1. Parameter name. ClientRepository.DeleteEmptyAsync calls this with named notation
--    ("id" => @Id, see BaseRepository.FunctionCallSql), but the baseline declared it as p_id, so
--    every school deletion failed with "function does not exist". The name now follows the
--    project convention: the original proc's parameter name (@Id), referenced as client_delete.Id.
-- 2. Data integrity. The baseline port omitted four DELETEs present in the source proc
--    (db/Sms.Migrations/procs/catredel/Client_Delete.sql): UserAppSettings,
--    PeriodAttendanceAudit, PeriodAttendanceRecords and Achievements, which would have left that
--    school's rows orphaned. They're restored here in the source's order.
--
-- A parameter can't be renamed with CREATE OR REPLACE, hence DROP + CREATE. dbo.client_delete_result
-- is unchanged. Not destructive: this only replaces a function definition. Rollback, if ever
-- needed, is restoring the previous definition from db/postgres/08_sample_procedure_conversions.sql.

DROP FUNCTION IF EXISTS dbo.client_delete(uuid);

CREATE FUNCTION dbo.client_delete(Id uuid)
RETURNS dbo.client_delete_result
LANGUAGE plpgsql
AS $$
DECLARE
    v_students int;
    v_teachers int;
    v_staff int;
    v_result dbo.client_delete_result;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM "dbo"."Tenants" WHERE "Id" = client_delete.Id) THEN
        v_result := (false, 'not_found', 0, 0, 0);
        RETURN v_result;
    END IF;

    SELECT COUNT(*) INTO v_students FROM "dbo"."Students" WHERE "TenantId" = client_delete.Id;
    SELECT COUNT(*) INTO v_teachers FROM "dbo"."Teachers" WHERE "TenantId" = client_delete.Id;
    SELECT COUNT(*) INTO v_staff FROM "dbo"."Staff" WHERE "TenantId" = client_delete.Id;

    IF v_students > 0 OR v_teachers > 0 OR v_staff > 0 THEN
        v_result := (false, 'has_people', v_students, v_teachers, v_staff);
        RETURN v_result;
    END IF;

    -- Runs inside the caller's transaction: any error below rolls back every DELETE.
    DELETE FROM "dbo"."PlanUpgradeRequests" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Invoices" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Subscriptions" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."OnboardingItems" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."AuditLog" WHERE "TenantId" = client_delete.Id;

    DELETE FROM "dbo"."RefreshTokens" rt
        USING "dbo"."Users" u
        WHERE u."Id" = rt."UserId" AND u."TenantId" = client_delete.Id;
    DELETE FROM "dbo"."UserRoles" ur
        USING "dbo"."Users" u
        WHERE u."Id" = ur."UserId" AND u."TenantId" = client_delete.Id;
    DELETE FROM "dbo"."UserAppSettings" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Users" WHERE "TenantId" = client_delete.Id;

    DELETE FROM "dbo"."AttendanceRecords" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."PeriodAttendanceAudit" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."PeriodAttendanceRecords" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."ExamPapers" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Exams" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Homework" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Achievements" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Assignments" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."FeePayments" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."FeeInvoices" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Payslips" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."LeaveRequests" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."TimetableSlots" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Subjects" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Classes" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Grades" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."CalendarEvents" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Announcements" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Notifications" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."ChatMessages" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."ChatThreads" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Complaints" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Tickets" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."CheckIns" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Boardings" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."TripPings" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Trips" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."BusAssignments" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."BusStops" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Buses" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."LibraryBooks" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."SchoolLocations" WHERE "TenantId" = client_delete.Id;

    DELETE FROM "dbo"."Tenants" WHERE "Id" = client_delete.Id;

    v_result := (true, 'deleted', 0, 0, 0);
    RETURN v_result;
END;
$$;

GRANT EXECUTE ON FUNCTION dbo.client_delete(uuid) TO sms_app;
```

Before saving, diff the DELETE list against the source proc (`grep -n "DELETE" db/Sms.Migrations/procs/catredel/Client_Delete.sql`). Every source table must appear in the same order, and no others.

- [ ] **Step 5: Write migration 0002**

`db/postgres/migrations/0002_drop_trip_ping_bulk_insert.sql`:

```sql
-- 0002: drop dbo.trip_ping_bulk_insert, the worked TVP example from
-- db/postgres/08_sample_procedure_conversions.sql. Nothing calls it: TransportModule calls
-- "dbo.TripPing_BulkInsert", which folds to dbo.tripping_bulkinsert (14_transport_procs.sql).
-- Not destructive: it removes an unused function and touches no data.

DROP FUNCTION IF EXISTS dbo.trip_ping_bulk_insert(uuid, uuid, jsonb);
```

Before saving, confirm there are no callers: `git grep -n -i "trip_ping_bulk_insert" -- src tests db/postgres`. The only hits allowed are comments in `08_sample_procedure_conversions.sql` and `TransportModule.cs`.

In `src/Sms.Modules.Transport/TransportModule.cs` around lines 100–103, change the comment that points at the worked example so it says the example was dropped by migration 0002. Comment only, no code change.

Remove `db/postgres/migrations/.gitkeep` (the folder now has real files).

- [ ] **Step 6: Switch the fixture to the runner and drop the old copy item**

In `PostgresFixture.InitializeAsync`, replace everything between `CreateDatabaseAsync` and `ConnectionString = …` with:

```csharp
        // The same path production takes for a brand-new database: the immutable baseline, then
        // every forward migration, through the real runner, as the superuser (CREATE ROLE, RLS
        // and GRANT need it). Tests therefore always run against baseline + migrations.
        var migrator = new PgMigrator(
            TestPostgresServer.ForDatabase(_dbName),
            new MigratorOptions(
                Path.Combine(AppContext.BaseDirectory, "baseline"),
                Path.Combine(AppContext.BaseDirectory, "migrations"),
                TimeSpan.FromMinutes(1),
                BackupConfirmed: false),
            TextWriter.Null);
        await migrator.InitAsync();
```

Add `using Sms.PgMigrator;`. Remove `using Npgsql;` if it's now unused (warnings are errors). Update the class doc comment to say it builds via `PgMigrator` init.

In `Sms.Tests.Integration.csproj`, delete the ItemGroup containing `LinkBase="postgres-schema"` (and its comment).

- [ ] **Step 7: Run the targeted tests**

Run: `SMS_TEST_PG_PASSWORD=12345678 dotnet build tests/Sms.Tests.Integration && SMS_TEST_PG_PASSWORD=12345678 dotnet test tests/Sms.Tests.Integration --no-build --filter "FullyQualifiedName~ClientDeleteTests|FullyQualifiedName~RealMigrationsTests|FullyQualifiedName~PgMigratorTests|FullyQualifiedName~DatabaseRoleGuardTests"`

Expected: all pass (3 + 2 + 15 + 2).

If a `ClientDeleteTests` seed insert violates one of the 3 check constraints, run `grep -n "CHECK" db/postgres/05_constraints.sql` and use a permitted value. Don't touch the constraint.

- [ ] **Step 8: Run the full integration suite**

Run: `SMS_TEST_PG_PASSWORD=12345678 dotnet test tests/Sms.Tests.Integration --no-build`

Expected: 0 failed, 0 skipped. The total is 768 + 3 + 2 + 15 + 2 = 790, minus the 4 `MigrateCliResolveTests` once Task 6 lands. Right now it's 790.

- [ ] **Step 9: Commit**

```bash
git add db/postgres/migrations tests/Sms.Tests.Integration src/Sms.Modules.Transport/TransportModule.cs
git commit -m "fix(tenancy): client_delete parameter mismatch and orphaned rows, as migrations 0001/0002

0001 renames dbo.client_delete's parameter to Id (ClientRepository binds \"id\" => @Id; every
school deletion failed with 'function does not exist') and restores the four DELETEs the port
dropped relative to the source proc: UserAppSettings, PeriodAttendanceAudit,
PeriodAttendanceRecords, Achievements. 0002 drops the unused dbo.trip_ping_bulk_insert example.
The baseline stays unchanged. PostgresFixture now builds every test database through PgMigrator
init (baseline + migrations), exactly like a fresh production database.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Retire the SQL Server `MigrateCli`, runbook, startup comment

**Files:**
- Delete: `db/Sms.Migrations/MigrateCli.cs`, `tests/Sms.Tests.Integration/Migrations/MigrateCliResolveTests.cs`
- Modify: `db/Sms.Migrations/Sms.Migrations.csproj` (library, not Exe), `tests/Sms.Tests.Integration/Sms.Tests.Integration.csproj` (drop the `Sms.Migrations` reference), `src/Sms.Api/Program.cs:19-24` (comment)
- Create: `docs/runbooks/postgres-migrations.md`

- [ ] **Step 1: Confirm nothing else uses `Sms.Migrations` from the tests**

Run: `git grep -n "Sms.Migrations\|MigrateCli\|MigrationRunner" -- tests src`
Expected: only `MigrateCliResolveTests.cs` and the csproj reference. Stop and reassess if anything else appears.

- [ ] **Step 2: Remove and retarget**

- Delete the two files.
- In `db/Sms.Migrations/Sms.Migrations.csproj`, remove `<OutputType>Exe</OutputType>`, `<StartupObject>…</StartupObject>` and the comment above them. Replace the comment with: `<!-- Historical reference only: the retired SQL Server FluentMigrator history (M0001..M0210 and procs/). Nothing runs it; Postgres schema changes go through db/Sms.PgMigrator. -->`
- Remove the `<ProjectReference Include="..\..\db\Sms.Migrations\Sms.Migrations.csproj" />` line from the integration test csproj.
- In `Program.cs`, replace the 6-line comment inside `if (app.Environment.IsDevelopment())` with:

```csharp
    // Schema: db/postgres/*.sql is the immutable baseline for a brand-new database and
    // db/postgres/migrations/NNNN_*.sql are forward-only changes. Both are applied by db/Sms.PgMigrator
    // (init / migrate) before the API rolls out, never by the API itself, which connects as the
    // non-superuser sms_app role (see DatabaseRoleGuard). db/Sms.Migrations (FluentMigrator, SQL
    // Server) is historical reference only. See docs/runbooks/postgres-migrations.md.
```

- [ ] **Step 3: Write the runbook**

`docs/runbooks/postgres-migrations.md`:

````markdown
# PostgreSQL schema migrations — runbook

## What runs where

- The **baseline** (`db/postgres/*.sql`) is immutable. It's applied only to a brand-new, empty database.
- **Forward migrations** (`db/postgres/migrations/NNNN_description.sql`) are applied in numeric order,
  and each is recorded in `dbo.schema_migrations` with a SHA-256 checksum.
- **Runner:** `db/Sms.PgMigrator`. It is run by the deploy pipeline (or docker compose's `migrate` service)
  **before** the new API version starts. The API never migrates, and it connects as `sms_app`,
  which has no DDL rights.
- The runner connects as the **schema owner** role, passed via `--connection` or `SMS_MIGRATOR_CONNECTION`.
  Never use the API's `ConnectionStrings__Sql` (`sms_app`).

| Situation | Command |
|---|---|
| Brand-new empty database | `init` (baseline + every migration) |
| Existing database (has `dbo."Tenants"`) | `migrate` (pending migrations only; the baseline is never re-run) |
| See what would run | `status` (read-only) |

```bash
dotnet run --project db/Sms.PgMigrator -c Release -- status
dotnet run --project db/Sms.PgMigrator -c Release -- migrate
# or, from a publish: dotnet Sms.PgMigrator.dll migrate
```

Exit codes: `0` ok, `1` usage error, `2` refused/failed (the message says what state the DB is in).

## Guarantees

- One transaction per migration, including its `schema_migrations` row. A failed migration is
  rolled back, **not** recorded, and later migrations don't run. Fix the file and re-run.
- Runs against the same database are serialised by a Postgres advisory lock. A second deploy waits
  (default 60 s, `--lock-timeout-seconds`), then no-ops because everything is applied.
- Editing or deleting an applied migration is refused (checksum/missing-file check). Add a new
  migration instead.
- Forward-only: there are no down migrations. Recovery from a bad *applied* migration is either a
  new corrective migration or a restore from backup.

## Writing a migration

1. Take the next number: `0003_short_description.sql` (lowercase, digits and underscores only).
2. No `BEGIN`/`COMMIT`/`ROLLBACK` in the file; the runner wraps it. `CREATE INDEX CONCURRENTLY`
   and other non-transactional statements aren't supported.
3. `GRANT` any new table or sequence to `sms_app` (and `EXECUTE` on new functions) in the same file,
   and enable/force RLS plus the 4 policies on any new tenant-scoped table, matching
   `db/postgres/07_rls_policies.sql`. The integration tests run as `sms_app`, so a missing grant fails them.
4. If it drops or rewrites existing data (drop column/table, destructive `UPDATE`, type narrowing),
   put `-- sms:destructive` in the leading comment block. Describe the manual recovery in a comment.

## Deploying

1. `status` against the target. Check the printed host, database and user are the ones you meant.
2. **If any pending migration is destructive:**
   1. `pg_dump -Fc -h <host> -U <owner> -d <db> -f <db>_pre_<version>_<timestamp>.dump` (or confirm a
      point-in-time-recovery restore point on managed Postgres).
   2. Verify the dump: `pg_restore --list <file>` succeeds and lists the expected tables. Better still,
      restore it into a scratch database and compare table counts.
   3. Run `migrate --backup-confirmed`. Without the flag the runner refuses and applies nothing.
3. Otherwise, run `migrate`.
4. Roll out the API.
5. On failure: nothing from the failing migration was applied. Read the error, fix forward, re-run.
   Restore the backup only if an *already committed* migration damaged data:
   `pg_restore --clean --if-exists -d <db> <file>`, with the API stopped.

## Production role setup (once per environment)

`db/postgres/00_app_role.sql` creates `sms_app` with a fixed **development** password only if the
role doesn't exist. In any shared or production environment, create `sms_app` first
(`CREATE ROLE sms_app LOGIN PASSWORD '<secret>' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;`)
so the baseline leaves it alone, and give the API that password via its secret store. The API
refuses to start outside Development if its role is a superuser or has BYPASSRLS.

## Tests

Runner tests create their own `sms_migtest_*` databases and drop them afterwards. The test helper
refuses any other database name. Never point tests or `SMS_TEST_PG_*` variables at a shared or
production server.
````

- [ ] **Step 4: Build and run the affected tests**

Run:
```
dotnet build Sms.slnx
SMS_TEST_PG_PASSWORD=12345678 dotnet test tests/Sms.Tests.Integration --no-build --filter "FullyQualifiedName~Migrations"
dotnet test tests/Sms.Tests.Unit --no-build
```
Expected: 0 errors and 0 warnings; the migration tests pass; the unit tests pass.

- [ ] **Step 5: Commit**

```bash
git add -A db/Sms.Migrations/MigrateCli.cs db/Sms.Migrations/Sms.Migrations.csproj tests/Sms.Tests.Integration docs/runbooks/postgres-migrations.md src/Sms.Api/Program.cs
git commit -m "chore(db): retire the SQL Server MigrateCli in favour of Sms.PgMigrator; add migration runbook

Historical FluentMigrator migrations (M*.cs, MigrationRunner.cs, procs/) are kept untouched as reference.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Compose `migrate` service and CI `compose-smoke` job

**Files:**
- Create: `db/Sms.PgMigrator/Dockerfile`
- Modify: `docker-compose.yml`, `.github/workflows/ci.yml`

- [ ] **Step 1: Migrator image**

`db/Sms.PgMigrator/Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish db/Sms.PgMigrator/Sms.PgMigrator.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Sms.PgMigrator.dll"]
```

- [ ] **Step 2: Compose**

In `docker-compose.yml`:

1. Add a healthcheck to `db`. It uses TCP (`-h 127.0.0.1`) on purpose: during first-boot initdb the image's temporary server listens on the unix socket only, so this reports healthy only after the baseline scripts finish.

```yaml
    healthcheck:
      test: ["CMD", "pg_isready", "-h", "127.0.0.1", "-U", "sms", "-d", "sms_dev"]
      interval: 5s
      timeout: 5s
      retries: 30
```

2. Add the `migrate` service between `db` and `api`:

```yaml
  # One-shot: applies pending db/postgres/migrations on top of the baseline initdb created, as
  # the schema owner (the sms bootstrap superuser). The API only starts after it succeeds.
  migrate:
    build: { context: ., dockerfile: db/Sms.PgMigrator/Dockerfile }
    depends_on:
      db: { condition: service_healthy }
    environment:
      SMS_MIGRATOR_CONNECTION: "Host=db;Port=5432;Database=sms_dev;Username=sms;Password=Local_Dev_Pass123!"
    command: ["migrate"]
```

3. Replace `api`'s `depends_on: [db]` with:

```yaml
    depends_on:
      db: { condition: service_healthy }
      migrate: { condition: service_completed_successfully }
```

- [ ] **Step 3: CI job**

Append to `.github/workflows/ci.yml` under `jobs:` (sibling of `build-test`):

```yaml
  compose-smoke:
    # Real stack: initdb baseline -> Sms.PgMigrator migrate -> API as sms_app. Proves migrations
    # run, the API starts, it connects as a non-superuser, and RLS actually filters rows for that role.
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Start the stack
        run: docker compose up -d --build
      - name: Wait for the API to be ready
        run: |
          for i in $(seq 1 60); do
            if curl -fsS http://localhost:5080/health/ready; then echo; exit 0; fi
            sleep 5
          done
          docker compose ps -a; docker compose logs; exit 1
      - name: Migrator exited 0 and applied 0001 and 0002
        run: |
          docker compose logs migrate
          test "$(docker inspect -f '{{.State.ExitCode}}' "$(docker compose ps -a -q migrate)")" = "0"
          applied=$(docker compose exec -T db psql -U sms -d sms_dev -tAc \
            "SELECT string_agg(version::text, ',' ORDER BY version) FROM dbo.schema_migrations")
          echo "applied: $applied"
          case ",$applied," in *,1,*) ;; *) echo "0001 missing"; exit 1;; esac
          case ",$applied," in *,2,*) ;; *) echo "0002 missing"; exit 1;; esac
          test "$(docker compose exec -T db psql -U sms -d sms_dev -tAc \
            "SELECT pg_get_function_identity_arguments('dbo.client_delete'::regproc)")" = "id uuid"
      - name: API connects as sms_app (not superuser, no BYPASSRLS) and the guard is quiet
        run: |
          roles=$(docker compose exec -T db psql -U sms -d sms_dev -tAc \
            "SELECT DISTINCT a.usename || '|' || r.rolsuper || '|' || r.rolbypassrls
               FROM pg_stat_activity a JOIN pg_roles r ON r.rolname = a.usename
              WHERE a.datname = 'sms_dev' AND a.client_addr IS NOT NULL")
          echo "API sessions: $roles"
          test "$roles" = "sms_app|false|false"
          ! docker compose logs api | grep -q "bypasses row-level security"
      - name: RLS filters rows for the app role
        run: |
          A=11111111-1111-1111-1111-111111111111; B=22222222-2222-2222-2222-222222222222
          docker compose exec -T db psql -v ON_ERROR_STOP=1 -U sms -d sms_dev -c "
            INSERT INTO dbo.\"Tenants\" (\"Id\",\"Name\",\"Slug\",\"Status\",\"Tier\") VALUES
              ('$A','CI A','ci-a','active','silver'), ('$B','CI B','ci-b','active','silver');
            INSERT INTO dbo.\"Students\" (\"Id\",\"TenantId\",\"AdmissionNo\",\"Name\") VALUES
              (gen_random_uuid(),'$A','A-1','a'), (gen_random_uuid(),'$B','B-1','b');"
          as_app() { docker compose exec -T db psql -v ON_ERROR_STOP=1 -U sms_app -d sms_dev -tA "$@" | tail -n 1; }
          test "$(docker compose exec -T db psql -U sms -d sms_dev -tAc 'SELECT count(*) FROM dbo."Students"')" = "2"
          test "$(as_app -c 'SELECT count(*) FROM dbo."Students"')" = "0"
          test "$(as_app -c "SELECT set_config('app.tenant_id','$A',false)" -c 'SELECT count(*) FROM dbo."Students"')" = "1"
          test "$(as_app -c "SELECT set_config('app.tenant_id','$A',false)" -c "SELECT count(*) FROM dbo.\"Students\" WHERE \"TenantId\" = '$B'")" = "0"
          echo "RLS enforced for sms_app: superuser sees 2, no tenant sees 0, tenant A sees only its 1"
      - name: Logs on failure
        if: failure()
        run: docker compose logs
```

- [ ] **Step 4: Lint locally (Docker isn't available here, so structure only)**

Run: `python -c "import yaml,sys; [yaml.safe_load(open(f)) for f in ('docker-compose.yml','.github/workflows/ci.yml')]; print('yaml ok')"`
Expected: `yaml ok`. If PyYAML is missing, run `pip install pyyaml` first, or use `dotnet tool`-free `git diff` review. The real execution check is the CI run in Task 10.

- [ ] **Step 5: Commit**

```bash
git add db/Sms.PgMigrator/Dockerfile docker-compose.yml .github/workflows/ci.yml
git commit -m "ci: compose migrate service and a compose-smoke job proving migrations, sms_app and RLS

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Local `sms_dev` — verify the target, back up, migrate, switch user-secrets

**This mutates the user's local database. Every STOP condition below is absolute.** Windows PowerShell 5.1 strips embedded double quotes from native-command arguments, so any query below that contains a double-quoted identifier (`dbo."Tenants"`) must be written to a `.sql` file in the scratchpad and run with `psql -f`. Don't pass it via `-c`. Commands are PowerShell. Secrets are read from the user-secrets file inside the same command and never printed. Helper file: create `C:\Users\user\AppData\Local\Temp\claude\D--convert-SMS-backend-sms-api\4f329060-27cd-4458-b438-4204a7797443\scratchpad\devdb.ps1`:

```powershell
# Dot-source me. Loads the Sms.Api user-secrets connection string into PG* env vars for this
# process only, without echoing the password.
$secretsPath = Join-Path $env:APPDATA 'Microsoft\UserSecrets\94866f7e-51ed-4001-a3fd-5ad34ed927fe\secrets.json'
$secrets = Get-Content $secretsPath -Raw | ConvertFrom-Json
$cs = $secrets.'ConnectionStrings:Sql'
if (-not $cs) { $cs = $secrets.ConnectionStrings.Sql }
$parts = @{}
foreach ($kv in $cs.Split(';')) { if ($kv -match '^\s*([^=]+?)\s*=\s*(.*)$') { $parts[$Matches[1].Trim().ToLower()] = $Matches[2] } }
$env:PGHOST = $parts['host']; $env:PGPORT = $(if ($parts['port']) { $parts['port'] } else { '5432' })
$env:PGDATABASE = $parts['database']; $env:PGUSER = $parts['username']; $env:PGPASSWORD = $parts['password']
$DevCs = $cs
$PgBin = 'C:\Program Files\PostgreSQL\18\bin'
"secret target: host=$env:PGHOST port=$env:PGPORT database=$env:PGDATABASE username=$env:PGUSER"
```

- [ ] **Step 1: Identify the target (read-only)**

```powershell
. "$env:TEMP\claude\D--convert-SMS-backend-sms-api\4f329060-27cd-4458-b438-4204a7797443\scratchpad\devdb.ps1"
& "$PgBin\psql.exe" -tA -F ' | ' -c "SELECT current_database(), current_user, coalesce(inet_server_addr()::text,'local-socket'), current_setting('data_directory'), (SELECT count(*) FROM pg_tables WHERE schemaname='dbo'), EXISTS (SELECT 1 FROM pg_tables WHERE schemaname='dbo' AND tablename='Tenants'), to_regclass('dbo.schema_migrations') IS NOT NULL, pg_get_function_identity_arguments('dbo.client_delete'::regproc)"
```

Proceed only if ALL of the following hold. **Otherwise STOP and report, changing nothing.**
- host is `localhost`, `127.0.0.1` or `::1`;
- the database is `sms_dev`;
- the server address is `127.0.0.1`, `::1` or `local-socket`;
- `data_directory` is under `C:\Program Files\PostgreSQL\18\data`, i.e. this machine's service;
- the `dbo` table count is ≥ 121, `Tenants` exists, and `schema_migrations` does not exist yet.

Record `client_delete`'s current arguments (expected `p_id uuid`).

- [ ] **Step 2: Back up**

```powershell
. "…\scratchpad\devdb.ps1"
New-Item -ItemType Directory -Force 'D:\convert\db-backups' | Out-Null
$ts = Get-Date -Format 'yyyyMMdd_HHmmss'
$dump = "D:\convert\db-backups\sms_dev_pre_pgmigrator_$ts.dump"
& "$PgBin\pg_dump.exe" -Fc -d sms_dev -f $dump
"pg_dump exit=$LASTEXITCODE size=$((Get-Item $dump).Length)"
```

**STOP** if the exit code isn't 0 or the size is 0.

- [ ] **Step 3: Prove the dump is usable (restore into a scratch DB, compare, drop)**

```powershell
. "…\scratchpad\devdb.ps1"
$dump = (Get-ChildItem 'D:\convert\db-backups\sms_dev_pre_pgmigrator_*.dump' | Sort-Object LastWriteTime | Select-Object -Last 1).FullName
& "$PgBin\pg_restore.exe" --list $dump | Out-Null; "list exit=$LASTEXITCODE"
$check = "sms_restorecheck_$(Get-Date -Format yyyyMMddHHmmss)"
& "$PgBin\createdb.exe" $check
& "$PgBin\pg_restore.exe" --no-owner -d $check $dump; "restore exit=$LASTEXITCODE"
$q = "SELECT (SELECT count(*) FROM pg_tables WHERE schemaname='dbo') || '/' || (SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='dbo') || '/' || (SELECT count(*) FROM dbo.""Tenants"")"
"sms_dev  tables/functions/tenants: " + (& "$PgBin\psql.exe" -d sms_dev -tAc $q)
"restored tables/functions/tenants: " + (& "$PgBin\psql.exe" -d $check -tAc $q)
& "$PgBin\dropdb.exe" $check; "dropped $check exit=$LASTEXITCODE"
```

**STOP (don't migrate)** if the list/restore exit codes aren't 0, or the two count triples differ. `pg_restore` may exit 1 with warnings only for pre-existing roles/extensions. If so, read every warning: proceed only if all of them are "already exists" for roles/extensions and the triples match.

- [ ] **Step 4: Status, then migrate**

```powershell
. "…\scratchpad\devdb.ps1"
$env:SMS_MIGRATOR_CONNECTION = $DevCs
dotnet run --project db/Sms.PgMigrator -c Release -- status; "status exit=$LASTEXITCODE"
```

Expected: target `database=sms_dev user=postgres`, `Baseline: present`, pending 0001 and 0002, neither destructive. **STOP** if the target differs.

```powershell
. "…\scratchpad\devdb.ps1"
$env:SMS_MIGRATOR_CONNECTION = $DevCs
dotnet run --project db/Sms.PgMigrator -c Release -- migrate; "migrate exit=$LASTEXITCODE"
```

Expected: `Applied 0001_client_delete_fix.sql`, `Applied 0002_drop_trip_ping_bulk_insert.sql`, exit 0.

If it exits 2, the failing migration was rolled back and nothing was recorded. Report the error. Don't restore; there's nothing to restore, since the backup is only needed if a committed migration damaged data.

- [ ] **Step 5: Verify the post-migration state**

```powershell
. "…\scratchpad\devdb.ps1"
& "$PgBin\psql.exe" -d sms_dev -tA -c "SELECT version, name, checksum, applied_at FROM dbo.schema_migrations ORDER BY version" -c "SELECT pg_get_function_identity_arguments('dbo.client_delete'::regproc)" -c "SELECT to_regprocedure('dbo.trip_ping_bulk_insert(uuid,uuid,jsonb)') IS NULL" -c "SELECT has_function_privilege('sms_app','dbo.client_delete(uuid)','EXECUTE')" -c "SELECT count(*) FILTER (WHERE NOT (has_table_privilege('sms_app', format('%I.%I', schemaname, tablename), 'SELECT') AND has_table_privilege('sms_app', format('%I.%I', schemaname, tablename), 'INSERT') AND has_table_privilege('sms_app', format('%I.%I', schemaname, tablename), 'UPDATE') AND has_table_privilege('sms_app', format('%I.%I', schemaname, tablename), 'DELETE'))) FROM pg_tables WHERE schemaname='dbo' AND tablename <> 'schema_migrations'" -c "SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname='sms_app'"
```

Expected:
- rows 1 and 2, with checksums equal to `MigrationFile.ComputeChecksum` of each file;
- `id uuid`;
- `t`;
- `t`;
- `0`, meaning `sms_app` has DML on every table;
- `f|f`.

If the privilege count is > 0, `sms_dev` predates `99_app_role_grants.sql`. Apply that file's idempotent GRANTs (`psql -d sms_dev -f db/postgres/99_app_role_grants.sql`; the backup from Step 2 covers this), re-check, and report that this was done.

- [ ] **Step 6: Switch user-secrets to `sms_app` and verify without printing the password**

```powershell
dotnet user-secrets set "ConnectionStrings:Sql" "Host=localhost;Port=5432;Database=sms_dev;Username=sms_app;Password=sms_app_dev_pw" --project src/Sms.Api | Out-Null; "set exit=$LASTEXITCODE"
. "…\scratchpad\devdb.ps1"
& "$PgBin\psql.exe" -tA -F ' | ' -c "SELECT current_user, current_database(), r.rolsuper, r.rolbypassrls FROM pg_roles r WHERE r.rolname = current_user"
```

`sms_app_dev_pw` is the fixed, non-secret local/test password already committed in `db/postgres/00_app_role.sql`. The `postgres` password never appears.

Expected: the loader prints `username=sms_app database=sms_dev`, and psql prints `sms_app | sms_dev | f | f`.

- [ ] **Step 7: Start the API locally against `sms_dev` as `sms_app`**

Run (bash, background with a timeout): `timeout 60 dotnet run --project src/Sms.Api -c Release > "$SCRATCH/api_local.log" 2>&1; grep -E "Now listening|bypasses row-level security|DatabaseRoleGuard|Unhandled" "$SCRATCH/api_local.log"`. Here `$SCRATCH` is the scratchpad path.

Expected: `Now listening`, and no guard or unhandled lines.

- [ ] **Step 8: Rerun `ClientDeleteTests`, the RLS/security tests, then everything**

```
SMS_TEST_PG_PASSWORD=12345678 dotnet test tests/Sms.Tests.Integration -c Release --filter "FullyQualifiedName~ClientDeleteTests"
SMS_TEST_PG_PASSWORD=12345678 dotnet test tests/Sms.Tests.Integration -c Release --no-build --filter "FullyQualifiedName~Rls|FullyQualifiedName~Security|FullyQualifiedName~Isolation|FullyQualifiedName~SessionContext|FullyQualifiedName~DatabaseRoleGuard|FullyQualifiedName~Authz"
SMS_TEST_PG_PASSWORD=12345678 dotnet test tests/Sms.Tests.Integration -c Release --no-build
```

Expected: all green, 0 skipped. These run on disposable `sms_test_*` databases; `sms_dev` itself is verified by Steps 5–7.

No commit: this task changes only local state. Record the backup path, the two applied versions and the verification outputs for the final report.

---

### Task 9: Schema audit against SQL Server (actual comparison, no assumptions)

**Files:**
- Create (scratchpad, not the repo): `…\scratchpad\schema_audit.py`

The comparison runs against:
- the live SQL Server `Sms` on `DESKTOP-TJL4SG6` (Windows auth, read-only `SELECT`s against `sys.*`);
- a fresh `sms_audit_<ts>` Postgres DB built by `Sms.PgMigrator init`, i.e. baseline + migrations, then dropped;
- `sms_dev`, read-only.

- [ ] **Step 1: Build the audit database**

```powershell
$env:PGPASSWORD='12345678'; $PgBin='C:\Program Files\PostgreSQL\18\bin'
$audit = "sms_audit_$(Get-Date -Format yyyyMMddHHmmss)"; $audit
& "$PgBin\createdb.exe" -h localhost -U postgres $audit
$env:SMS_MIGRATOR_CONNECTION = "Host=localhost;Database=$audit;Username=postgres;Password=12345678"
dotnet run --project db/Sms.PgMigrator -c Release -- init; "init exit=$LASTEXITCODE"
```

Expected: target `database=sms_audit_…`, all baseline files, then applied 0001 and 0002, exit 0.

- [ ] **Step 2: Write `schema_audit.py`**

```python
"""Actual SQL Server (Sms) vs PostgreSQL schema comparison. Usage: python schema_audit.py <pg_db>
Needs PGPASSWORD for postgres. Read-only on both sides."""
import collections, csv, subprocess, sys

SQLCMD = r"C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\sqlcmd.exe"
PSQL = r"C:\Program Files\PostgreSQL\18\bin\psql.exe"
PG_DB = sys.argv[1]
SCHEMAS = "('dbo','rls')"

def mssql(q):
    out = subprocess.run([SQLCMD, "-S", "DESKTOP-TJL4SG6", "-d", "Sms", "-E", "-h", "-1", "-W", "-s", "|",
                          "-Q", "SET NOCOUNT ON; " + q], capture_output=True, text=True, check=True).stdout
    return [tuple(x.strip() for x in l.split("|")) for l in out.splitlines() if l.strip()]

def pg(q):
    out = subprocess.run([PSQL, "-h", "localhost", "-U", "postgres", "-d", PG_DB, "-At", "-F", "|",
                          "-v", "ON_ERROR_STOP=1", "-c", q], capture_output=True, text=True, check=True).stdout
    return [tuple(l.split("|")) for l in out.splitlines() if l.strip()]

MS_T = "sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id"
PG_T = f"pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind IN ('r','p') AND n.nspname IN {SCHEMAS} AND c.relname <> 'schema_migrations'"
results = []

def compare(label, ms_rows, pg_rows, key=lambda r: r):
    ms, pgs = collections.Counter(map(key, ms_rows)), collections.Counter(map(key, pg_rows))
    matched = sum((ms & pgs).values())
    missing, extra = sorted((ms - pgs).elements()), sorted((pgs - ms).elements())
    results.append((label, matched, sum(ms.values()), sum(pgs.values())))
    print(f"\n== {label}: {matched}/{sum(ms.values())} SQL Server objects matched (Postgres has {sum(pgs.values())})")
    for m in missing[:50]: print("   MISSING in PG:", m)
    for e in extra[:50]: print("   EXTRA in PG:  ", e)

# Tables
compare("Tables", mssql(f"SELECT s.name + '.' + t.name FROM {MS_T} WHERE t.is_ms_shipped = 0"),
        pg(f"SELECT n.nspname || '.' || c.relname FROM {PG_T}"))

# Columns (+ nullability, identity) keyed by table.column
ms_cols = mssql(f"""SELECT s.name + '.' + t.name, c.name, CASE c.is_nullable WHEN 1 THEN 'Y' ELSE 'N' END,
    CASE c.is_identity WHEN 1 THEN 'Y' ELSE 'N' END, ty.name
    FROM sys.columns c JOIN {MS_T} ON t.object_id = c.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id
    WHERE t.is_ms_shipped = 0""")
pg_cols = pg(f"""SELECT n.nspname || '.' || c.relname, a.attname, CASE WHEN a.attnotnull THEN 'N' ELSE 'Y' END,
    CASE WHEN a.attidentity <> '' OR coalesce(pg_get_expr(d.adbin, d.adrelid), '') LIKE 'nextval(%' THEN 'Y' ELSE 'N' END,
    format_type(a.atttypid, a.atttypmod)
    FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
    LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
    WHERE c.relkind IN ('r','p') AND n.nspname IN {SCHEMAS} AND c.relname <> 'schema_migrations' AND a.attnum > 0 AND NOT a.attisdropped""")
compare("Columns", ms_cols, pg_cols, key=lambda r: (r[0], r[1]))
pg_by = {(r[0], r[1]): r for r in pg_cols}
both = [(m, pg_by[(m[0], m[1])]) for m in ms_cols if (m[0], m[1]) in pg_by]
null_bad = [(m[0], m[1], m[2], p[2]) for m, p in both if m[2] != p[2]]
results.append(("Nullability", len(both) - len(null_bad), len(both), len(both)))
print(f"\n== Nullability: {len(both) - len(null_bad)}/{len(both)} matched columns agree")
for b in null_bad: print("   DIFF (table, column, mssql, pg):", b)
compare("Identity columns", [m[:2] for m in ms_cols if m[3] == "Y"], [p[:2] for p in pg_cols if p[3] == "Y"])
print("\n== Type mapping (mssql -> pg: count), for review")
for (a, b), n in sorted(collections.Counter((m[4], p[4]) for m, p in both).items()): print(f"   {a:>18} -> {b:<32} {n}")

# Primary keys / unique constraints: table + ordered columns
def ms_keys(flag):
    return mssql(f"""SELECT s.name + '.' + t.name, STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
        FROM sys.indexes i JOIN {MS_T} ON t.object_id = i.object_id
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.{flag} = 1 AND t.is_ms_shipped = 0 GROUP BY s.name, t.name, i.name""")
def pg_keys(contype):
    return pg(f"""SELECT n.nspname || '.' || c.relname, string_agg(a.attname, ',' ORDER BY k.ord)
        FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid JOIN pg_namespace n ON n.oid = c.relnamespace
        CROSS JOIN LATERAL unnest(con.conkey) WITH ORDINALITY k(attnum, ord)
        JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = k.attnum
        WHERE con.contype = '{contype}' AND n.nspname IN {SCHEMAS} AND c.relname <> 'schema_migrations' GROUP BY con.oid, 1""")
compare("Primary keys", ms_keys("is_primary_key"), pg_keys("p"))
compare("Unique constraints", ms_keys("is_unique_constraint"), pg_keys("u"))

# Foreign keys
compare("Foreign keys", mssql(f"""SELECT ps.name + '.' + pt.name,
        STRING_AGG(pc.name, ',') WITHIN GROUP (ORDER BY fkc.constraint_column_id), rs.name + '.' + rt.name,
        STRING_AGG(rc.name, ',') WITHIN GROUP (ORDER BY fkc.constraint_column_id)
    FROM sys.foreign_keys fk JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
    JOIN sys.tables pt ON pt.object_id = fk.parent_object_id JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
    JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
    JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
    JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
    GROUP BY fk.object_id, ps.name, pt.name, rs.name, rt.name"""),
    pg(f"""SELECT n.nspname || '.' || c.relname, string_agg(a.attname, ',' ORDER BY k.ord),
        rn.nspname || '.' || rc.relname, string_agg(ra.attname, ',' ORDER BY k.ord)
    FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid JOIN pg_namespace n ON n.oid = c.relnamespace
    JOIN pg_class rc ON rc.oid = con.confrelid JOIN pg_namespace rn ON rn.oid = rc.relnamespace
    CROSS JOIN LATERAL unnest(con.conkey, con.confkey) WITH ORDINALITY k(attnum, refattnum, ord)
    JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = k.attnum
    JOIN pg_attribute ra ON ra.attrelid = rc.oid AND ra.attnum = k.refattnum
    WHERE con.contype = 'f' GROUP BY con.oid, n.nspname, c.relname, rn.nspname, rc.relname"""))

# Indexes (not backing PK/UQ constraints): table + unique flag + ordered key columns
compare("Indexes", mssql(f"""SELECT s.name + '.' + t.name, CASE i.is_unique WHEN 1 THEN 'U' ELSE '-' END,
        STRING_AGG(CASE WHEN ic.is_included_column = 0 THEN c.name END, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
    FROM sys.indexes i JOIN {MS_T} ON t.object_id = i.object_id
    JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE i.is_primary_key = 0 AND i.is_unique_constraint = 0 AND i.type > 0 AND t.is_ms_shipped = 0
    GROUP BY s.name, t.name, i.name, i.is_unique"""),
    pg(f"""SELECT n.nspname || '.' || t.relname, CASE WHEN x.indisunique THEN 'U' ELSE '-' END,
        (SELECT string_agg(a.attname, ',' ORDER BY k.ord) FROM unnest(x.indkey::int2[]) WITH ORDINALITY k(attnum, ord)
           JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum WHERE k.ord <= x.indnkeyatts)
    FROM pg_index x JOIN pg_class t ON t.oid = x.indrelid JOIN pg_namespace n ON n.oid = t.relnamespace
    WHERE n.nspname IN {SCHEMAS} AND t.relname <> 'schema_migrations'
      AND NOT EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid = x.indexrelid AND con.contype IN ('p','u'))"""))

# Procedures/functions by case-folded name (the project's naming convention), explained via the inventory CSV
ms_fn = [(r[0].lower(),) for r in mssql("SELECT o.name FROM sys.objects o WHERE o.type IN ('P','FN','IF','TF') AND o.is_ms_shipped = 0")]
pg_fn = [(r[0].lower(),) for r in pg(f"SELECT DISTINCT p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname IN {SCHEMAS}")]
pg_fn_set = set(pg_fn)
status = {row["ObjectName"].lower(): row["ConversionStatus"] for row in csv.DictReader(open(r"D:\convert\sqlserver-object-inventory.csv", encoding="utf-8-sig"))}
missing_fn = sorted({n for (n,) in ms_fn} - {n for (n,) in pg_fn_set})
results.append(("Procedures/functions", len(ms_fn) - len(missing_fn), len(ms_fn), len(pg_fn)))
print(f"\n== Procedures/functions: {len(ms_fn) - len(missing_fn)}/{len(ms_fn)} SQL Server routines have a Postgres function of the same folded name (Postgres has {len(pg_fn)} functions)")
for n in missing_fn: print(f"   NO PG FUNCTION: {n}  (inventory: {status.get(n, 'not in inventory')})")

# RLS: protected tables and policies
ms_rls = mssql("SELECT DISTINCT OBJECT_SCHEMA_NAME(sp.target_object_id) + '.' + OBJECT_NAME(sp.target_object_id) FROM sys.security_predicates sp")
pg_rls = pg(f"SELECT n.nspname || '.' || c.relname FROM {PG_T} AND c.relrowsecurity AND c.relforcerowsecurity")
compare("RLS protected tables (ENABLE + FORCE)", ms_rls, pg_rls)
pol = pg("SELECT schemaname || '.' || tablename, string_agg(cmd, ',' ORDER BY cmd) FROM pg_policies WHERE schemaname = 'dbo' GROUP BY 1")
full = [p for p in pol if p[1] == "DELETE,INSERT,SELECT,UPDATE"]
results.append(("RLS policies (4 per table)", len(full), len(pg_rls), len(pol)))
print(f"\n== Policies: {len(full)}/{len(pg_rls)} RLS tables have SELECT/INSERT/UPDATE/DELETE policies ({sum(len(p[1].split(',')) for p in pol)} policies total)")
for p in pol:
    if p[1] != "DELETE,INSERT,SELECT,UPDATE": print("   INCOMPLETE:", p)

# Grants for sms_app
g = pg(f"""SELECT
  (SELECT count(*) FROM pg_tables WHERE schemaname = 'dbo' AND tablename <> 'schema_migrations'),
  (SELECT count(*) FROM pg_tables WHERE schemaname = 'dbo' AND tablename <> 'schema_migrations'
     AND has_table_privilege('sms_app', format('%I.%I', schemaname, tablename), 'SELECT')
     AND has_table_privilege('sms_app', format('%I.%I', schemaname, tablename), 'INSERT')
     AND has_table_privilege('sms_app', format('%I.%I', schemaname, tablename), 'UPDATE')
     AND has_table_privilege('sms_app', format('%I.%I', schemaname, tablename), 'DELETE')),
  (SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname IN {SCHEMAS}),
  (SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname IN {SCHEMAS} AND has_function_privilege('sms_app', p.oid, 'EXECUTE')),
  (SELECT rolsuper::text || '/' || rolbypassrls::text FROM pg_roles WHERE rolname = 'sms_app'),
  (SELECT has_table_privilege('sms_app', 'dbo.schema_migrations', 'SELECT'))""")[0]
results.append(("sms_app table DML grants", int(g[1]), int(g[0]), int(g[0])))
results.append(("sms_app function EXECUTE grants", int(g[3]), int(g[2]), int(g[2])))
print(f"\n== Grants: tables {g[1]}/{g[0]} full DML, functions {g[3]}/{g[2]} EXECUTE, sms_app super/bypassrls={g[4]}, sms_app reads schema_migrations={g[5]}")

print("\n==== SUMMARY (matched / SQL Server total / Postgres total)")
for label, m, a, b in results: print(f"  {label:<40} {m}/{a}   (pg {b})")
```

- [ ] **Step 3: Run it against the audit DB and against `sms_dev`**

```powershell
$env:PGPASSWORD='12345678'
python "…\scratchpad\schema_audit.py" sms_audit_<ts> | Tee-Object "…\scratchpad\audit_fresh.txt"
python "…\scratchpad\schema_audit.py" sms_dev | Tee-Object "…\scratchpad\audit_sms_dev.txt"
```

Expected: tables 121/121, columns 1,133/1,133, nullability agrees on every matched column, identity/PK/UQ/FK/index counts match, and RLS 84/84 with 84 × 4 policies. The functions line lists every unmatched SQL Server routine with its inventory status; each must be `SKIPPED` with a reason, or it's a gap to investigate. Grants are complete, and `sms_app` is `false/false` with no `schema_migrations` access.

**Every MISSING/EXTRA/DIFF line is investigated before moving on.**
- Real gaps (e.g. a missing index or FK) get a new forward migration `0003_…` with a test, following Task 5's pattern. Never edit the baseline.
- Explainable differences (e.g. a SQL Server-only `_Migration_*` helper table, or the `tripping_bulkinsert` fold) go into the final report with the reason.

- [ ] **Step 4: Baseline immutability and drop the audit DB**

```powershell
git diff --stat ebedefd -- "db/postgres/*.sql"; "baseline diff lines: $((git diff ebedefd -- 'db/postgres/*.sql' | Measure-Object -Line).Lines)"
$env:PGPASSWORD='12345678'; & "C:\Program Files\PostgreSQL\18\bin\dropdb.exe" -h localhost -U postgres sms_audit_<ts>; "dropped exit=$LASTEXITCODE"
```

Expected: `baseline diff lines: 0` and `dropped exit=0`. Note that the git pathspec `db/postgres/*.sql` also matches `migrations/*.sql`, because git `*` crosses `/`. If migrations show up, use `git diff ebedefd -- $(git ls-tree --name-only ebedefd db/postgres/ | grep '\.sql$')` instead.

---

### Task 10: Full verification, SQL Server dependency scan, push, real CI

- [ ] **Step 1: SQL Server dependency scan**

```bash
git grep -n -I -i -E "Microsoft\.Data\.SqlClient|\bSqlConnection\b|\bSqlCommand\b|\[dbo\]|UNIQUEIDENTIFIER|NEWID\(|NEWSEQUENTIALID|GETDATE\(|GETUTCDATE|SCOPE_IDENTITY|\bSELECT\s+TOP\b|\bTOP\s*\(|ISNULL\(|OUTER APPLY|CROSS APPLY|AddSqlServer|FluentMigrator|Trusted_Connection|TrustServerCertificate|Server=|sqlcmd|mssql" \
  -- . ':!db/Sms.Migrations' ':!docs' > "$SCRATCH/sqlserver_scan.txt"; wc -l "$SCRATCH/sqlserver_scan.txt"
git grep -c -I -i -E "SqlClient|FluentMigrator|AddSqlServer|UNIQUEIDENTIFIER|NEWID\(|GETUTCDATE" -- db/Sms.Migrations | tail -3
dotnet list src/Sms.Api package --include-transitive | grep -i -E "SqlClient|FluentMigrator" || echo "Sms.Api: no SQL Server packages"
```

Classify every hit in `sqlserver_scan.txt` as one of:
- (a) a comment or doc describing history, which is fine;
- (b) a test asserting SQL Server syntax is gone, which is fine;
- (c) live code or config. **Anything in (c) gets fixed**, with a test, and committed as `fix(…)`.

`db/Sms.Migrations` hits are reported separately as category D (historical, not built into the API, not runtime).

- [ ] **Step 2: Full local verification (Release, like CI)**

```bash
dotnet build Sms.slnx -c Release 2>&1 | tail -3
dotnet test tests/Sms.Tests.Unit -c Release --no-build 2>&1 | tail -3
SMS_TEST_PG_PASSWORD=12345678 dotnet test tests/Sms.Tests.Integration -c Release --no-build 2>&1 | tail -3
```

Expected: `0 Warning(s) 0 Error(s)`. Unit tests: 422 + the new MigrationFile/Cli tests, 0 failed, 0 skipped. Integration tests: 0 failed, 0 skipped.

Any failure goes through superpowers:systematic-debugging, is fixed at the root cause, and gets rerun. Never skip, delete or weaken a test.

- [ ] **Step 3: Push**

```bash
git status --short   # must be clean
git log --oneline origin/postgres-migration..HEAD
git push origin postgres-migration
```

- [ ] **Step 4: Watch the real CI run for the pushed HEAD**

```bash
sha=$(git rev-parse HEAD)
run=$(gh run list --branch postgres-migration --commit "$sha" --limit 1 --json databaseId -q '.[0].databaseId'); echo "run $run"
gh run watch "$run" --exit-status; echo "exit=$?"
gh run view "$run" --json conclusion,jobs -q '{conclusion: .conclusion, jobs: [.jobs[] | {name, conclusion}]}'
```

If `run` is empty, wait for the push event to register with `gh run list --branch postgres-migration --limit 3`, then re-query. Only a run whose `headSha` equals the local HEAD counts.

On failure: `gh run view "$run" --log-failed`, then investigate, fix, rerun locally, commit, push, and watch the new run. Repeat until both `build-test` and `compose-smoke` show `success`.

- [ ] **Step 5: Update memory**

Edit `C:\Users\user\.claude\projects\D--convert-SMS-backend-sms-api\memory\postgres_migration_status.md`. Add a dated "Update 3" section covering:
- that the PgMigrator mechanism exists (commands, runbook path);
- 0001/0002;
- `sms_dev` migrated (backup path);
- user-secrets now `sms_app`;
- the `compose-smoke` CI job;
- the final CI run id.

Remove the now-false "Deliberately deferred" bullets: RLS inert, and the migration-history strategy open. Keep the `MEMORY.md` pointer line accurate.

- [ ] **Step 6: Final report**

This happens only after Step 4 is green. It includes:
- the requested scorecard (Migration status, Production readiness, Tables, Columns, Functions/procedures, Indexes, Foreign keys, RLS, Unit tests, Integration tests, Build, CI, SQL Server runtime dependencies, Known blockers), with every number taken from Task 9's audit output and Step 2/Step 4 output;
- commit hash, branch, push result, and the GitHub Actions run id, URL and conclusion per job;
- sections A. PostgreSQL baseline migration, B. New forward-migration mechanism, C. Existing application/runtime migration dependencies, D. SQL Server historical code, E. Tests, F. CI.

It says NOT READY if any blocker remains.
