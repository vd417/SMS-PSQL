-- 0006: teacher bus push — add BusTravelingTeachers."StopId" (a traveling teacher's optional
-- boarding stop, used for the "~1 km away" approaching alert) and BusTeacherAlerts (per
-- trip/teacher/kind dedupe, the teacher mirror of BusParentAlerts). Tenant-scoped RLS, same
-- 4-policy pattern; granted to sms_app.

ALTER TABLE "dbo"."BusTravelingTeachers" ADD COLUMN "StopId" uuid NULL;

CREATE TABLE "dbo"."BusTeacherAlerts" (
    "Id" uuid DEFAULT gen_random_uuid() NOT NULL,
    "TenantId" uuid NOT NULL,
    "TripId" uuid NOT NULL,
    "TeacherUserId" uuid NOT NULL,
    "Kind" varchar(40) NOT NULL,
    "CreatedAt" timestamptz DEFAULT now() NOT NULL
);

ALTER TABLE "dbo"."BusTeacherAlerts" ADD CONSTRAINT "PK_BusTeacherAlerts" PRIMARY KEY ("Id");
CREATE UNIQUE INDEX "UX_BusTeacherAlerts_Trip_Teacher_Kind"
    ON "dbo"."BusTeacherAlerts" ("TenantId", "TripId", "TeacherUserId", "Kind");

ALTER TABLE "dbo"."BusTeacherAlerts" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."BusTeacherAlerts" FORCE ROW LEVEL SECURITY;
CREATE POLICY "BusTeacherAlertsTenantPolicy_select" ON "dbo"."BusTeacherAlerts" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BusTeacherAlertsTenantPolicy_update" ON "dbo"."BusTeacherAlerts" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BusTeacherAlertsTenantPolicy_delete" ON "dbo"."BusTeacherAlerts" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BusTeacherAlertsTenantPolicy_insert" ON "dbo"."BusTeacherAlerts" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

GRANT SELECT, INSERT, UPDATE, DELETE ON "dbo"."BusTeacherAlerts" TO sms_app;
