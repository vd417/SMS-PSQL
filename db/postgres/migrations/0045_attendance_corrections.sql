-- One row each time saved attendance is changed. The backup is the marks before that save.
-- The first two changes in a calendar month (Asia/Kolkata) are stored only.
-- The 3rd and later changes email the school owner. Rollback: DROP TABLE "dbo"."AttendanceCorrections";

CREATE TABLE IF NOT EXISTS "dbo"."AttendanceCorrections" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId" uuid NOT NULL,
    "Kind" varchar(16) NOT NULL,
    "ClassId" uuid NOT NULL,
    "Date" date NOT NULL,
    "Period" int,
    "Subject" varchar(120),
    "ActorId" uuid,
    "ActorName" varchar(200),
    "ActorRole" varchar(64),
    "BackupJson" text NOT NULL,
    "Detail" text NOT NULL,
    "At" timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS "IX_AttendanceCorrections_Month"
    ON "dbo"."AttendanceCorrections" ("TenantId", "At");
CREATE INDEX IF NOT EXISTS "IX_AttendanceCorrections_Slot"
    ON "dbo"."AttendanceCorrections" ("ClassId", "Date", "Period", "Subject", "At");

ALTER TABLE "dbo"."AttendanceCorrections" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."AttendanceCorrections" FORCE ROW LEVEL SECURITY;
CREATE POLICY "AttendanceCorrectionsTenantPolicy_select" ON "dbo"."AttendanceCorrections"
    FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AttendanceCorrectionsTenantPolicy_update" ON "dbo"."AttendanceCorrections"
    FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id())
    WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AttendanceCorrectionsTenantPolicy_delete" ON "dbo"."AttendanceCorrections"
    FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AttendanceCorrectionsTenantPolicy_insert" ON "dbo"."AttendanceCorrections"
    FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

GRANT SELECT, INSERT, UPDATE, DELETE ON "dbo"."AttendanceCorrections" TO sms_app;
