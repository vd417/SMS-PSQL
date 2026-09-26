# PostgreSQL forward-migration mechanism — design

Date: 2026-09-26 · Branch: `postgres-migration` · Status: awaiting review

## Why

`db/postgres/*.sql` is the real PostgreSQL baseline, but it can only build an *empty* database
(plain `CREATE TABLE`/`CREATE TYPE`, not idempotent). There is no way to change the schema of a
database that already exists. The only "production migration" entry point, `Sms.Migrations.MigrateCli`,
drives FluentMigrator's SQL Server processor and cannot connect to Postgres at all. Nothing runs
migrations at API startup (`Program.cs`), and that stays true.

Two fixes on this branch already need to reach existing databases, and they become the first
real migrations:

- **0001** — `dbo.client_delete(p_id uuid)` → `dbo.client_delete(Id uuid)`. `ClientRepository`
  calls it as `"id" => @Id`, so every school deletion fails today with "function does not exist".
  The fix also restores 4 DELETEs the port left out compared with the source proc
  (`db/Sms.Migrations/procs/catredel/Client_Delete.sql`): `UserAppSettings`,
  `PeriodAttendanceAudit`, `PeriodAttendanceRecords`, `Achievements`. Postgres can't rename a
  parameter with `CREATE OR REPLACE`, so this is `DROP FUNCTION` followed by `CREATE FUNCTION`.
  Result type `dbo.client_delete_result` is unchanged.
- **0002** — `DROP FUNCTION dbo.trip_ping_bulk_insert(...)`. It's a dead worked example: the real
  call resolves to `dbo.tripping_bulkinsert` (`14_transport_procs.sql`). It has no callers outside
  comments. Non-destructive for data.

The baseline files stay byte-for-byte unchanged (requirement 1). A fresh database is the baseline
plus every migration.

## Existing conventions this follows

- Npgsql only, no new NuGet packages. The runner takes a connection string like
  `NpgsqlConnectionFactory` does.
- Baseline files are applied in `StringComparer.Ordinal` filename order, one command per file,
  on an owner/superuser connection. This matches `PostgresFixture` today and docker's
  `/docker-entrypoint-initdb.d`.
- The app connects as the non-superuser `sms_app` (`00_app_role.sql`, `99_app_role_grants.sql`).
  DDL runs as the owner role, never as `sms_app`.
- SQL files are copied to build output with `<None Include=… CopyToOutputDirectory LinkBase>`,
  the same way the test project copies `postgres-schema` today.
- Function parameters use the original proc parameter name and are referenced as
  `fn_name.Param` (see `10_tenancy_procs.sql`).

## Components

### `db/postgres/migrations/NNNN_description.sql`

- The filename must match `^\d{4}_[a-z0-9_]+\.sql$`. The version is the integer prefix.
- Any other `.sql` file in the folder, or a duplicate version, is a hard error. A typo'd file
  can't be silently skipped.
- Optional header directives go in leading comment lines:
  - `-- sms:destructive` marks a migration that drops or rewrites existing data (see Safety).
- No rollback section is ever executed. A migration may *describe* a manual rollback in a
  comment, but the system is forward-only.
- Every new table or sequence must `GRANT` to `sms_app` in the same file. Functions get
  `EXECUTE` from `PUBLIC` by default, but an explicit grant is still written for consistency
  with `99_app_role_grants.sql`. The integration suite runs as `sms_app`, so a missing grant
  fails tests.

### `db/Sms.PgMigrator` (new, small console project; references Npgsql only)

- `PgMigrator` is a library class with all the logic, so tests can call it in-process.
- `Program.Main` is a thin CLI over it. It gets its connection string from `--connection`, or
  else from the `SMS_MIGRATOR_CONNECTION` env var. It deliberately doesn't read
  `ConnectionStrings__Sql`, because that is the API's `sms_app` string and DDL needs the owner
  role.
- The baseline and migrations folders are copied into the project's output (`baseline/`,
  `migrations/`). Both can be overridden with `--baseline-dir` / `--migrations-dir`.

Commands, which make fresh vs. existing explicit (requirement 11):

| Command | Precondition (checked before any write) | Does |
|---|---|---|
| `init` | Target DB has **no** `dbo` schema. | Applies the baseline files in order, creates the tracking table, then applies every migration. |
| `migrate` | Target DB **has** the baseline (`dbo."Tenants"` exists). | Creates the tracking table if missing, then applies pending migrations. Never touches baseline files. |
| `status` | — | Read-only. Prints the target (host/db/user), and which migrations are applied and pending. |

`init` on a non-empty DB, or `migrate` on an empty one, exits non-zero without writing anything.
Every command first prints the target host, database, and current user (safety rule: verify the
target before mutating).

### Tracking table

```sql
CREATE TABLE IF NOT EXISTS dbo.schema_migrations (
    version     int PRIMARY KEY,
    name        text NOT NULL,
    checksum    text NOT NULL,          -- sha256 of the file, CRLF normalised to LF
    applied_at  timestamptz NOT NULL DEFAULT now()
);
```

It's created by the runner, not the baseline, so the baseline stays unchanged. RLS is not
enabled on it, and `sms_app` gets no grant: the app never reads it.

If an applied migration's checksum no longer matches its file, the run is a hard error. Nobody
can silently edit history.

## Data flow of one `migrate` run

1. Open one connection as the owner role. Print the target.
2. Take a session-level `pg_try_advisory_lock(key)` with a fixed bigint constant declared once in `PgMigrator`. Poll every 1s up to
   `--lock-timeout` (default 60s), then fail with a "another migration run holds the lock" error.
   This covers requirement 8: two deploy processes serialize, and the second one then sees
   everything applied and no-ops.
3. Check the precondition. Create the tracking table if needed. Load and validate all files
   (names, duplicates, checksums of applied ones).
4. For each pending migration in ascending numeric order:
   `BEGIN` → execute the file → `INSERT INTO dbo.schema_migrations` → `COMMIT`.
   On any error: `ROLLBACK`, stop, and exit non-zero. That migration isn't recorded and none of
   its partial DDL remains (Postgres DDL is transactional). Later migrations don't run.
   (Requirements 5–7.)
5. Release the lock (it's also released automatically when the connection closes).

`init` is the same flow with the baseline applied first, inside the same lock. The baseline runs
as one transaction, so a failed `init` leaves an empty database behind, not a half-built one.

Migrations that can't run inside a transaction (e.g. `CREATE INDEX CONCURRENTLY`) aren't
supported yet. Such a file fails loudly rather than half-applying. This can be added if one is
ever needed.

## Safety

- **Destructive migrations.** The runner refuses to apply a file marked `-- sms:destructive`
  unless it's invoked with `--backup-confirmed`. The runner can't verify a backup itself, so
  the flag is the operator's explicit attestation.
- **Runbook.** `docs/runbooks/postgres-migrations.md` documents:
  1. `pg_dump -Fc` (or confirm a PITR point) before any destructive migration, and a restore test.
  2. Run `status`, then `migrate`, before rolling out the API.
  3. Recovery is always restore-from-backup, never automated rollback.
- **Tests never touch shared databases.** Migration tests create their own `sms_migtest_<guid>`
  databases. A helper refuses to run any mutation unless the target database name starts with
  `sms_migtest_` or `sms_test_`. Each database is dropped in `finally`/`DisposeAsync`, using the
  same `pg_terminate_backend` + `DROP DATABASE` as `PostgresFixture`. `sms_dev` and production are
  never test targets.
- **No automatic migrate at API startup.** The API keeps running as `sms_app`, which has no DDL
  rights. `DatabaseRoleGuard` (already implemented) makes the API refuse to start outside
  Development if it's connected as a role that bypasses RLS.

## Wiring

- **`PostgresFixture`** calls `PgMigrator` `init` instead of its own file loop. Every
  integration test then runs against baseline + migrations, exactly as production would, and
  `ClientDeleteTests` exercises migration 0001.
- **`docker-compose.yml`**:
  - `db` keeps initdb applying the baseline to a fresh volume.
  - A new one-shot `migrate` service (built from a small `db/Sms.PgMigrator/Dockerfile`,
    connecting as the `sms` bootstrap superuser) runs `migrate`.
  - `api` gets `depends_on: migrate: condition: service_completed_successfully` and connects as
    `sms_app` (already changed).
- **CI**:
  - The existing job is unchanged, since it already runs the migration tests through the
    integration project.
  - New `compose-smoke` job, because Docker isn't available on the dev machine. It runs
    `docker compose up -d --build` and waits for `/health/ready`. It then asserts via `psql` that
    the API's backend in `pg_stat_activity` runs as `sms_app` (non-superuser), and that
    `dbo.schema_migrations` lists 0001 and 0002. This is the real "RLS is enforced when the
    application runs" check.
- **Retire `MigrateCli`** (only after the above passes):
  - Delete `db/Sms.Migrations/MigrateCli.cs` and `MigrateCliResolveTests.cs`. Their
    arg-vs-env resolution is covered by new `PgMigrator` CLI tests.
  - Switch `Sms.Migrations.csproj` from `Exe` to a library, and drop the test project's reference
    to it.
  - All `M*.cs` files, `MigrationRunner.cs`, and `procs/**` stay untouched as historical
    reference. The project stays in the solution so it keeps compiling.
- The `Program.cs` startup comment gets updated to point at the runner and runbook.

## Tests (integration project, `Migrations/PgMigratorTests.cs`, own databases)

Synthetic migration folders (written to a temp dir per test) keep these independent of the real
migrations:

1. Fresh `init`: baseline objects exist and every real migration is recorded.
2. `migrate` on a baseline-only DB applies 0001 and records it.
3. Several migrations apply in numeric order (0002 depends on 0001's table; files are written
   out of order).
4. A re-run skips already-applied migrations (the migration inserts a row; the count stays 1).
5. A failing migration (valid DDL, then an error): not recorded, its DDL rolled back, later
   migrations not run, non-zero result.
6. Concurrency:
   - Two runners start together on one DB, with a migration that `pg_sleep`s and then inserts.
     It's applied exactly once and both runs succeed.
   - A runner facing an externally held lock times out with the lock error.
7. Existing DB: a marker row is inserted into a baseline table first. `migrate` applies a new
   migration, the marker survives, and no baseline file is re-executed.
8. Guards: `init` on a non-empty DB refuses; `migrate` on an empty DB refuses; a checksum
   mismatch refuses; a destructive migration without `--backup-confirmed` refuses; a bad
   filename or duplicate version refuses.
9. The real migrations: 0001 is covered end to end by `ClientDeleteTests` (404 / 409 with counts
   / 204 with the dependent rows gone, as `sms_app`); 0002 is checked by asserting
   `dbo.trip_ping_bulk_insert` no longer exists after `init`.

## Out of scope

- Rewriting the historical FluentMigrator files.
- Down migrations.
- Non-transactional migrations.
- Running migrations from the API process.
- Changing `sms_app`'s fixed dev password. Production must create `sms_app` with its own secret
  password; the runbook calls this out, and the baseline's `IF NOT EXISTS` leaves an existing
  role's password alone.

## Open question for review

Your local `sms_dev` database: should I run `migrate` against it once this is built (after
printing the target and taking a `pg_dump`)? Otherwise school deletion stays broken locally.
Your user-secrets connection string also still uses `postgres`, so you'll see the new
`DatabaseRoleGuard` error log in Development until you switch it to `sms_app`.
