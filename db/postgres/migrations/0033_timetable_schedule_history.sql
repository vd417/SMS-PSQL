-- Last 5 timetable draft/publish snapshots per school.
-- The live envelope (TimetableSchedules) still holds only the current draft and
-- the current published copy. This table is the short history behind them.
-- Idempotent: safe to apply by hand while the 0021 checksum blocks PgMigrator.

CREATE TABLE IF NOT EXISTS "dbo"."TimetableScheduleHistory" (
    "Id" uuid DEFAULT gen_random_uuid() NOT NULL,
    "TenantId" uuid NOT NULL,
    "Kind" varchar(16) NOT NULL,
    "SnapshotJson" text,
    "SavedAt" timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS "IX_TimetableScheduleHistory_Tenant_SavedAt"
    ON "dbo"."TimetableScheduleHistory" ("TenantId", "SavedAt" DESC);

ALTER TABLE "dbo"."TimetableScheduleHistory" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."TimetableScheduleHistory" FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS "TimetableScheduleHistoryTenantPolicy_select" ON "dbo"."TimetableScheduleHistory";
DROP POLICY IF EXISTS "TimetableScheduleHistoryTenantPolicy_update" ON "dbo"."TimetableScheduleHistory";
DROP POLICY IF EXISTS "TimetableScheduleHistoryTenantPolicy_delete" ON "dbo"."TimetableScheduleHistory";
DROP POLICY IF EXISTS "TimetableScheduleHistoryTenantPolicy_insert" ON "dbo"."TimetableScheduleHistory";

CREATE POLICY "TimetableScheduleHistoryTenantPolicy_select" ON "dbo"."TimetableScheduleHistory"
    FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TimetableScheduleHistoryTenantPolicy_update" ON "dbo"."TimetableScheduleHistory"
    FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id())
    WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TimetableScheduleHistoryTenantPolicy_delete" ON "dbo"."TimetableScheduleHistory"
    FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TimetableScheduleHistoryTenantPolicy_insert" ON "dbo"."TimetableScheduleHistory"
    FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

GRANT SELECT, INSERT, UPDATE, DELETE ON "dbo"."TimetableScheduleHistory" TO sms_app;

CREATE OR REPLACE FUNCTION dbo.timetableschedulehistory_add(
    TenantId uuid, Kind varchar(16), SnapshotJson text, SavedAt timestamptz
)
RETURNS int
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    INSERT INTO "dbo"."TimetableScheduleHistory" ("TenantId", "Kind", "SnapshotJson", "SavedAt")
    VALUES (TenantId, Kind, SnapshotJson, COALESCE(SavedAt, now()));

    DELETE FROM "dbo"."TimetableScheduleHistory"
    WHERE "Id" IN (
        SELECT "Id" FROM "dbo"."TimetableScheduleHistory"
        WHERE "TenantId" = timetableschedulehistory_add.TenantId
        ORDER BY "SavedAt" DESC, "Id" DESC
        OFFSET 5
    );

    RETURN 1;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.timetableschedulehistory_list()
RETURNS TABLE ("Id" uuid, "Kind" varchar(16), "SnapshotJson" text, "SavedAt" timestamptz)
LANGUAGE sql
AS $$
    SELECT "Id", "Kind", "SnapshotJson", "SavedAt"
    FROM "dbo"."TimetableScheduleHistory"
    ORDER BY "SavedAt" DESC, "Id" DESC
    LIMIT 5;
$$;

GRANT EXECUTE ON FUNCTION dbo.timetableschedulehistory_add(uuid, varchar, text, timestamptz) TO sms_app;
GRANT EXECUTE ON FUNCTION dbo.timetableschedulehistory_list() TO sms_app;
