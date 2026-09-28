-- 0005: dbo.PtmMeetings, the minimum storage behind GET /v1/ptm and PATCH /v1/ptm/{id}
-- (parent/student app PTM screen). Additive only: a new table, index, RLS policies and grants.
-- Rollback: DROP TABLE "dbo"."PtmMeetings";

CREATE TABLE "dbo"."PtmMeetings" (
    "Id" uuid DEFAULT gen_random_uuid() NOT NULL,
    "TenantId" uuid NOT NULL,
    "StudentId" uuid NOT NULL,
    "TeacherId" uuid,
    "Subject" varchar(120),
    "MeetingDate" date NOT NULL,
    "MeetingTime" time NOT NULL,
    "Mode" varchar(120) NOT NULL,
    "Status" varchar(20) DEFAULT 'pending' NOT NULL,
    "CreatedAt" timestamptz DEFAULT now() NOT NULL,
    CONSTRAINT "PK_PtmMeetings" PRIMARY KEY ("Id"),
    CONSTRAINT "CK_PtmMeetings_Status" CHECK ("Status" IN ('pending', 'confirmed'))
);

CREATE INDEX "IX_PtmMeetings_Tenant_Student_Date"
    ON "dbo"."PtmMeetings" ("TenantId", "StudentId", "MeetingDate");

ALTER TABLE "dbo"."PtmMeetings" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."PtmMeetings" FORCE ROW LEVEL SECURITY;
CREATE POLICY "PtmMeetingsTenantPolicy_select" ON "dbo"."PtmMeetings" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PtmMeetingsTenantPolicy_update" ON "dbo"."PtmMeetings" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PtmMeetingsTenantPolicy_delete" ON "dbo"."PtmMeetings" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PtmMeetingsTenantPolicy_insert" ON "dbo"."PtmMeetings" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

-- 99_app_role_grants.sql ran before this table existed.
GRANT SELECT, INSERT, UPDATE, DELETE ON "dbo"."PtmMeetings" TO sms_app;
