-- Timetable draft/publish envelope, same shape as AcademicPeriodSchedules / ClassTestSchedules.
-- Slot rows in TimetableSlots stay the live routine. This table is the admin draft vs last
-- published snapshot so per-class status survives a reload (session memory does not).
-- Idempotent: safe to apply by hand while the 0021 checksum blocks PgMigrator.

CREATE TABLE IF NOT EXISTS "dbo"."TimetableSchedules" (
    "Id" uuid DEFAULT gen_random_uuid() NOT NULL,
    "TenantId" uuid NOT NULL,
    "DraftJson" text,
    "PublishedJson" text,
    "DraftSavedAt" timestamptz,
    "PublishedAt" timestamptz
);

CREATE UNIQUE INDEX IF NOT EXISTS "UQ_TimetableSchedules_Tenant"
    ON "dbo"."TimetableSchedules" ("TenantId");

ALTER TABLE "dbo"."TimetableSchedules" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."TimetableSchedules" FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS "TimetableSchedulesTenantPolicy_select" ON "dbo"."TimetableSchedules";
DROP POLICY IF EXISTS "TimetableSchedulesTenantPolicy_update" ON "dbo"."TimetableSchedules";
DROP POLICY IF EXISTS "TimetableSchedulesTenantPolicy_delete" ON "dbo"."TimetableSchedules";
DROP POLICY IF EXISTS "TimetableSchedulesTenantPolicy_insert" ON "dbo"."TimetableSchedules";

CREATE POLICY "TimetableSchedulesTenantPolicy_select" ON "dbo"."TimetableSchedules"
    FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TimetableSchedulesTenantPolicy_update" ON "dbo"."TimetableSchedules"
    FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id())
    WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TimetableSchedulesTenantPolicy_delete" ON "dbo"."TimetableSchedules"
    FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TimetableSchedulesTenantPolicy_insert" ON "dbo"."TimetableSchedules"
    FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

GRANT SELECT, INSERT, UPDATE, DELETE ON "dbo"."TimetableSchedules" TO sms_app;

CREATE OR REPLACE FUNCTION dbo.timetableschedule_get()
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "DraftJson" text, "PublishedJson" text, "DraftSavedAt" timestamptz, "PublishedAt" timestamptz)
LANGUAGE sql
AS $$
    SELECT "Id", "TenantId", "DraftJson", "PublishedJson", "DraftSavedAt", "PublishedAt"
    FROM "dbo"."TimetableSchedules" LIMIT 1;
$$;

CREATE OR REPLACE FUNCTION dbo.timetableschedule_upsert(
    TenantId uuid, DraftJson text DEFAULT NULL, PublishedJson text DEFAULT NULL,
    DraftSavedAt timestamptz DEFAULT NULL, PublishedAt timestamptz DEFAULT NULL
)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "DraftJson" text, "PublishedJson" text, "DraftSavedAt" timestamptz, "PublishedAt" timestamptz)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    INSERT INTO "dbo"."TimetableSchedules" ("TenantId", "DraftJson", "PublishedJson", "DraftSavedAt", "PublishedAt")
    VALUES (TenantId, DraftJson, PublishedJson, DraftSavedAt, PublishedAt)
    ON CONFLICT ("TenantId") DO UPDATE SET
        "DraftJson" = COALESCE(timetableschedule_upsert.DraftJson, "TimetableSchedules"."DraftJson"),
        "PublishedJson" = COALESCE(timetableschedule_upsert.PublishedJson, "TimetableSchedules"."PublishedJson"),
        "DraftSavedAt" = COALESCE(timetableschedule_upsert.DraftSavedAt, "TimetableSchedules"."DraftSavedAt"),
        "PublishedAt" = COALESCE(timetableschedule_upsert.PublishedAt, "TimetableSchedules"."PublishedAt");

    RETURN QUERY
    SELECT "Id", "TenantId", "DraftJson", "PublishedJson", "DraftSavedAt", "PublishedAt"
    FROM "dbo"."TimetableSchedules" WHERE "TenantId" = timetableschedule_upsert.TenantId LIMIT 1;
END;
$$;

GRANT EXECUTE ON FUNCTION dbo.timetableschedule_get() TO sms_app;
GRANT EXECUTE ON FUNCTION dbo.timetableschedule_upsert(uuid, text, text, timestamptz, timestamptz) TO sms_app;
