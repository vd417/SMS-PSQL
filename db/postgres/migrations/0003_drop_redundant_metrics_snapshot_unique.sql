-- 0003: drop dbo."PlatformMetricsSnapshot"'s redundant UNIQUE ("Month").
--
-- The baseline declares "Month" date NOT NULL UNIQUE inline (04_tables.sql) and then makes the
-- same column the primary key (05_constraints.sql), so Postgres carries two unique indexes on one
-- column. SQL Server has only PK_PlatformMetricsSnapshot (found by the SQL Server -> Postgres
-- schema comparison). The primary key still enforces uniqueness, and the only writer
-- (dbo.platformmetrics_upsertcurrentmonth's ON CONFLICT ("Month")) resolves against it.
-- Not destructive: no data changes. Rollback, if ever needed:
--   ALTER TABLE "dbo"."PlatformMetricsSnapshot" ADD CONSTRAINT "PlatformMetricsSnapshot_Month_key" UNIQUE ("Month");

ALTER TABLE "dbo"."PlatformMetricsSnapshot" DROP CONSTRAINT IF EXISTS "PlatformMetricsSnapshot_Month_key";
