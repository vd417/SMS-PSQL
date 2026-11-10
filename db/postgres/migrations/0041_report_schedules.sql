-- Saved school-report mail schedules. The report rows themselves stay in the existing
-- student, staff, fee, attendance, and transport tables. This only stores when to send,
-- who receives the file, and the last run.

CREATE TABLE "dbo"."ReportSchedules" (
    "Id" uuid DEFAULT gen_random_uuid() NOT NULL,
    "TenantId" uuid NOT NULL,
    "ReportKey" varchar(40) NOT NULL,
    "Title" varchar(200) NOT NULL,
    "Frequency" varchar(10) NOT NULL,
    "SendTime" time NOT NULL,
    "DayOfWeek" smallint,
    "DayOfMonth" smallint,
    "UtcOffsetMinutes" integer DEFAULT 0 NOT NULL,
    "Format" varchar(8) DEFAULT 'xlsx' NOT NULL,
    "Recipients" text NOT NULL,
    "Subject" varchar(200),
    "Message" text,
    "FiltersJson" text DEFAULT '{}' NOT NULL,
    "Status" varchar(10) DEFAULT 'active' NOT NULL,
    "NextRunAt" timestamptz NOT NULL,
    "LastRunAt" timestamptz,
    "CreatedBy" uuid,
    "CreatedAt" timestamptz DEFAULT now() NOT NULL,
    "UpdatedAt" timestamptz DEFAULT now() NOT NULL,
    CONSTRAINT "PK_ReportSchedules" PRIMARY KEY ("Id")
);

CREATE INDEX "IX_ReportSchedules_Due" ON "dbo"."ReportSchedules" ("NextRunAt") WHERE "Status" = 'active';

CREATE TABLE "dbo"."ReportScheduleRuns" (
    "Id" uuid DEFAULT gen_random_uuid() NOT NULL,
    "TenantId" uuid NOT NULL,
    "ScheduleId" uuid NOT NULL,
    "StartedAt" timestamptz DEFAULT now() NOT NULL,
    "FinishedAt" timestamptz,
    "Status" varchar(10) NOT NULL,
    "Detail" text,
    "FromDate" date,
    "ToDate" date,
    CONSTRAINT "PK_ReportScheduleRuns" PRIMARY KEY ("Id")
);

CREATE INDEX "IX_ReportScheduleRuns_Schedule" ON "dbo"."ReportScheduleRuns" ("ScheduleId", "StartedAt" DESC);

ALTER TABLE "dbo"."ReportSchedules" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."ReportSchedules" FORCE ROW LEVEL SECURITY;
CREATE POLICY "ReportSchedulesTenantPolicy_select" ON "dbo"."ReportSchedules" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ReportSchedulesTenantPolicy_update" ON "dbo"."ReportSchedules" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ReportSchedulesTenantPolicy_delete" ON "dbo"."ReportSchedules" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ReportSchedulesTenantPolicy_insert" ON "dbo"."ReportSchedules" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."ReportScheduleRuns" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."ReportScheduleRuns" FORCE ROW LEVEL SECURITY;
CREATE POLICY "ReportScheduleRunsTenantPolicy_select" ON "dbo"."ReportScheduleRuns" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ReportScheduleRunsTenantPolicy_update" ON "dbo"."ReportScheduleRuns" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ReportScheduleRunsTenantPolicy_delete" ON "dbo"."ReportScheduleRuns" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ReportScheduleRunsTenantPolicy_insert" ON "dbo"."ReportScheduleRuns" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

GRANT SELECT, INSERT, UPDATE, DELETE ON "dbo"."ReportSchedules" TO sms_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON "dbo"."ReportScheduleRuns" TO sms_app;
