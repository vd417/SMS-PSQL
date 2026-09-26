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
