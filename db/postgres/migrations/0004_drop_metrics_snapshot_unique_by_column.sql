-- 0004: finish what 0003 intended, independent of the constraint's name.
--
-- 0003 dropped dbo."PlatformMetricsSnapshot"'s redundant UNIQUE ("Month") by the baseline's
-- exact name ("PlatformMetricsSnapshot_Month_key") with IF EXISTS. A database built before the
-- runner existed (the local sms_dev) carries the same constraint as platformmetricssnapshot_month_key,
-- so 0003 was a silent no-op there. This drops every UNIQUE constraint whose only column is "Month"
-- (never the primary key, which is contype 'p'), whatever it is called. On databases where 0003
-- already worked it finds nothing and does nothing.
-- Not destructive: no data changes; PK_PlatformMetricsSnapshot still enforces uniqueness.
-- Rollback, if ever needed:
--   ALTER TABLE "dbo"."PlatformMetricsSnapshot" ADD CONSTRAINT "PlatformMetricsSnapshot_Month_key" UNIQUE ("Month");

DO $$
DECLARE
    r record;
BEGIN
    FOR r IN
        SELECT con.conname
        FROM pg_constraint con
        JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attname = 'Month'
        WHERE con.conrelid = 'dbo."PlatformMetricsSnapshot"'::regclass
          AND con.contype = 'u'
          AND con.conkey = ARRAY[a.attnum]::int2[]
    LOOP
        EXECUTE format('ALTER TABLE "dbo"."PlatformMetricsSnapshot" DROP CONSTRAINT %I', r.conname);
    END LOOP;
END
$$;
