-- Optional per-class teaching-period count. No row means the class uses the full
-- school bell. 5, 6, or 8 means that class timetable stops after that period.
-- Timings still come from the school Periods schedule.
-- Idempotent: safe to apply by hand while the 0021 checksum blocks PgMigrator.

CREATE TABLE IF NOT EXISTS "dbo"."ClassPeriodOverrides" (
    "Id" uuid DEFAULT gen_random_uuid() NOT NULL,
    "TenantId" uuid NOT NULL,
    "ClassId" uuid NOT NULL,
    "PeriodCount" int NOT NULL,
    CONSTRAINT "PK_ClassPeriodOverrides" PRIMARY KEY ("Id"),
    CONSTRAINT "UQ_ClassPeriodOverrides_Class" UNIQUE ("TenantId", "ClassId")
);

ALTER TABLE "dbo"."ClassPeriodOverrides" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."ClassPeriodOverrides" FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS "ClassPeriodOverridesTenantPolicy_select" ON "dbo"."ClassPeriodOverrides";
DROP POLICY IF EXISTS "ClassPeriodOverridesTenantPolicy_update" ON "dbo"."ClassPeriodOverrides";
DROP POLICY IF EXISTS "ClassPeriodOverridesTenantPolicy_delete" ON "dbo"."ClassPeriodOverrides";
DROP POLICY IF EXISTS "ClassPeriodOverridesTenantPolicy_insert" ON "dbo"."ClassPeriodOverrides";

CREATE POLICY "ClassPeriodOverridesTenantPolicy_select" ON "dbo"."ClassPeriodOverrides"
    FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ClassPeriodOverridesTenantPolicy_update" ON "dbo"."ClassPeriodOverrides"
    FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id())
    WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ClassPeriodOverridesTenantPolicy_delete" ON "dbo"."ClassPeriodOverrides"
    FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ClassPeriodOverridesTenantPolicy_insert" ON "dbo"."ClassPeriodOverrides"
    FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

GRANT SELECT, INSERT, UPDATE, DELETE ON "dbo"."ClassPeriodOverrides" TO sms_app;
