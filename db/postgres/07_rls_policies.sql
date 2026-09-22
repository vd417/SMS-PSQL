-- PostgreSQL native Row-Level Security, modeled 1:1 on the SQL Server mechanism read from
-- rls.fn_tenant_predicate (db/Sms.Migrations/M0002_Rls_Policies.cs) and the live
-- sys.security_policies / sys.security_predicates catalog (84 policy rows = 42 tables x
-- filter+AFTER INSERT block predicate, except dbo.AuditLog which is filter-only/append-only).
--
-- SQL Server predicate (session-scoped, set once per connection by SqlConnectionFactory
-- via sp_set_session_context before every query -- see
-- src/Sms.Shared.Kernel/Data/SqlConnectionFactory.cs):
--   allowed WHEN SESSION_CONTEXT('IsPlatform') = 1 OR @TenantId = SESSION_CONTEXT('TenantId')
--
-- Postgres equivalent: the backend must open every connection and immediately run
--   SELECT set_config('app.tenant_id', <tenant-guid-text>, false);
--   SELECT set_config('app.is_platform', <'1' or '0'>, false);
-- (false = session-scoped, not transaction-scoped, matching sp_set_session_context's
-- per-connection lifetime -- see backend-impact section of the audit for the exact
-- SqlConnectionFactory replacement needed). RLS policies below read these via
-- current_setting(..., true) with a NULL-safe default so a connection that forgets to
-- stamp context fails CLOSED (denies all rows) rather than open.

CREATE OR REPLACE FUNCTION rls.current_tenant_id() RETURNS uuid AS $$
  SELECT NULLIF(current_setting('app.tenant_id', true), '')::uuid
$$ LANGUAGE sql STABLE;

CREATE OR REPLACE FUNCTION rls.is_platform() RETURNS boolean AS $$
  SELECT COALESCE(NULLIF(current_setting('app.is_platform', true), ''), '0')::int = 1
$$ LANGUAGE sql STABLE;

ALTER TABLE "dbo"."AcademicPeriodSchedules" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."AcademicPeriodSchedules" FORCE ROW LEVEL SECURITY;
CREATE POLICY "AcademicPeriodSchedulesTenantPolicy_select" ON "dbo"."AcademicPeriodSchedules" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AcademicPeriodSchedulesTenantPolicy_update" ON "dbo"."AcademicPeriodSchedules" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AcademicPeriodSchedulesTenantPolicy_delete" ON "dbo"."AcademicPeriodSchedules" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AcademicPeriodSchedulesTenantPolicy_insert" ON "dbo"."AcademicPeriodSchedules" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."AcademicSessions" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."AcademicSessions" FORCE ROW LEVEL SECURITY;
CREATE POLICY "AcademicSessionsTenantPolicy_select" ON "dbo"."AcademicSessions" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AcademicSessionsTenantPolicy_update" ON "dbo"."AcademicSessions" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AcademicSessionsTenantPolicy_delete" ON "dbo"."AcademicSessions" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AcademicSessionsTenantPolicy_insert" ON "dbo"."AcademicSessions" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."Achievements" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."Achievements" FORCE ROW LEVEL SECURITY;
CREATE POLICY "AchievementsTenantPolicy_select" ON "dbo"."Achievements" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AchievementsTenantPolicy_update" ON "dbo"."Achievements" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AchievementsTenantPolicy_delete" ON "dbo"."Achievements" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AchievementsTenantPolicy_insert" ON "dbo"."Achievements" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."Announcements" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."Announcements" FORCE ROW LEVEL SECURITY;
CREATE POLICY "AnnouncementsTenantPolicy_select" ON "dbo"."Announcements" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AnnouncementsTenantPolicy_update" ON "dbo"."Announcements" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AnnouncementsTenantPolicy_delete" ON "dbo"."Announcements" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AnnouncementsTenantPolicy_insert" ON "dbo"."Announcements" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."Assignments" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."Assignments" FORCE ROW LEVEL SECURITY;
CREATE POLICY "AssignmentsTenantPolicy_select" ON "dbo"."Assignments" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AssignmentsTenantPolicy_update" ON "dbo"."Assignments" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AssignmentsTenantPolicy_delete" ON "dbo"."Assignments" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AssignmentsTenantPolicy_insert" ON "dbo"."Assignments" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."AttendanceAlertConfigs" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."AttendanceAlertConfigs" FORCE ROW LEVEL SECURITY;
CREATE POLICY "AttendanceAlertConfigsTenantPolicy_select" ON "dbo"."AttendanceAlertConfigs" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AttendanceAlertConfigsTenantPolicy_update" ON "dbo"."AttendanceAlertConfigs" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AttendanceAlertConfigsTenantPolicy_delete" ON "dbo"."AttendanceAlertConfigs" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AttendanceAlertConfigsTenantPolicy_insert" ON "dbo"."AttendanceAlertConfigs" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."AttendanceRecords" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."AttendanceRecords" FORCE ROW LEVEL SECURITY;
CREATE POLICY "AttendanceRecordsTenantPolicy_select" ON "dbo"."AttendanceRecords" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AttendanceRecordsTenantPolicy_update" ON "dbo"."AttendanceRecords" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AttendanceRecordsTenantPolicy_delete" ON "dbo"."AttendanceRecords" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AttendanceRecordsTenantPolicy_insert" ON "dbo"."AttendanceRecords" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."AuditLog" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."AuditLog" FORCE ROW LEVEL SECURITY;
CREATE POLICY "AuditLogTenantPolicy_select" ON "dbo"."AuditLog" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AuditLogTenantPolicy_update" ON "dbo"."AuditLog" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AuditLogTenantPolicy_delete" ON "dbo"."AuditLog" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
-- AuditLog: SQL Server policy has NO block/AFTER-INSERT predicate (append-only audit table --
-- writes are not tenant-restricted at the DB layer there). Unlike SQL Server, Postgres RLS
-- denies INSERT by default when a table has RLS enabled/forced and no INSERT policy exists at
-- all (there is no "no predicate = unrestricted" equivalent) -- so an explicit always-true
-- WITH CHECK policy is required here to actually preserve that unrestricted-append behavior,
-- not silently deny every insert instead.
CREATE POLICY "AuditLogTenantPolicy_insert" ON "dbo"."AuditLog" FOR INSERT WITH CHECK (true);

ALTER TABLE "dbo"."AuditLogs" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."AuditLogs" FORCE ROW LEVEL SECURITY;
CREATE POLICY "AuditLogsTenantPolicy_select" ON "dbo"."AuditLogs" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AuditLogsTenantPolicy_update" ON "dbo"."AuditLogs" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AuditLogsTenantPolicy_delete" ON "dbo"."AuditLogs" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "AuditLogsTenantPolicy_insert" ON "dbo"."AuditLogs" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."Boardings" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."Boardings" FORCE ROW LEVEL SECURITY;
CREATE POLICY "BoardingsTenantPolicy_select" ON "dbo"."Boardings" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BoardingsTenantPolicy_update" ON "dbo"."Boardings" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BoardingsTenantPolicy_delete" ON "dbo"."Boardings" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BoardingsTenantPolicy_insert" ON "dbo"."Boardings" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."BulkImportBatches" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."BulkImportBatches" FORCE ROW LEVEL SECURITY;
CREATE POLICY "BulkImportBatchesTenantPolicy_select" ON "dbo"."BulkImportBatches" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BulkImportBatchesTenantPolicy_update" ON "dbo"."BulkImportBatches" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BulkImportBatchesTenantPolicy_delete" ON "dbo"."BulkImportBatches" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BulkImportBatchesTenantPolicy_insert" ON "dbo"."BulkImportBatches" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."BusAssignments" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."BusAssignments" FORCE ROW LEVEL SECURITY;
CREATE POLICY "BusAssignmentsTenantPolicy_select" ON "dbo"."BusAssignments" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BusAssignmentsTenantPolicy_update" ON "dbo"."BusAssignments" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BusAssignmentsTenantPolicy_delete" ON "dbo"."BusAssignments" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BusAssignmentsTenantPolicy_insert" ON "dbo"."BusAssignments" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."BusDriverAssignments" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."BusDriverAssignments" FORCE ROW LEVEL SECURITY;
CREATE POLICY "BusDriverAssignmentsTenantPolicy_select" ON "dbo"."BusDriverAssignments" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BusDriverAssignmentsTenantPolicy_update" ON "dbo"."BusDriverAssignments" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BusDriverAssignmentsTenantPolicy_delete" ON "dbo"."BusDriverAssignments" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BusDriverAssignmentsTenantPolicy_insert" ON "dbo"."BusDriverAssignments" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."BusParentAlerts" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."BusParentAlerts" FORCE ROW LEVEL SECURITY;
CREATE POLICY "BusParentAlertsTenantPolicy_select" ON "dbo"."BusParentAlerts" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BusParentAlertsTenantPolicy_update" ON "dbo"."BusParentAlerts" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BusParentAlertsTenantPolicy_delete" ON "dbo"."BusParentAlerts" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BusParentAlertsTenantPolicy_insert" ON "dbo"."BusParentAlerts" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."BusStops" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."BusStops" FORCE ROW LEVEL SECURITY;
CREATE POLICY "BusStopsTenantPolicy_select" ON "dbo"."BusStops" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BusStopsTenantPolicy_update" ON "dbo"."BusStops" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BusStopsTenantPolicy_delete" ON "dbo"."BusStops" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BusStopsTenantPolicy_insert" ON "dbo"."BusStops" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."BusTravelingTeachers" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."BusTravelingTeachers" FORCE ROW LEVEL SECURITY;
CREATE POLICY "BusTravelingTeachersTenantPolicy_select" ON "dbo"."BusTravelingTeachers" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BusTravelingTeachersTenantPolicy_update" ON "dbo"."BusTravelingTeachers" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BusTravelingTeachersTenantPolicy_delete" ON "dbo"."BusTravelingTeachers" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BusTravelingTeachersTenantPolicy_insert" ON "dbo"."BusTravelingTeachers" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."Buses" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."Buses" FORCE ROW LEVEL SECURITY;
CREATE POLICY "BusesTenantPolicy_select" ON "dbo"."Buses" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BusesTenantPolicy_update" ON "dbo"."Buses" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BusesTenantPolicy_delete" ON "dbo"."Buses" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "BusesTenantPolicy_insert" ON "dbo"."Buses" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."CalendarEvents" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."CalendarEvents" FORCE ROW LEVEL SECURITY;
CREATE POLICY "CalendarEventsTenantPolicy_select" ON "dbo"."CalendarEvents" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "CalendarEventsTenantPolicy_update" ON "dbo"."CalendarEvents" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "CalendarEventsTenantPolicy_delete" ON "dbo"."CalendarEvents" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "CalendarEventsTenantPolicy_insert" ON "dbo"."CalendarEvents" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."ChatMessages" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."ChatMessages" FORCE ROW LEVEL SECURITY;
CREATE POLICY "ChatMessagesTenantPolicy_select" ON "dbo"."ChatMessages" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ChatMessagesTenantPolicy_update" ON "dbo"."ChatMessages" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ChatMessagesTenantPolicy_delete" ON "dbo"."ChatMessages" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ChatMessagesTenantPolicy_insert" ON "dbo"."ChatMessages" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."ChatThreads" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."ChatThreads" FORCE ROW LEVEL SECURITY;
CREATE POLICY "ChatThreadsTenantPolicy_select" ON "dbo"."ChatThreads" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ChatThreadsTenantPolicy_update" ON "dbo"."ChatThreads" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ChatThreadsTenantPolicy_delete" ON "dbo"."ChatThreads" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ChatThreadsTenantPolicy_insert" ON "dbo"."ChatThreads" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."CheckIns" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."CheckIns" FORCE ROW LEVEL SECURITY;
CREATE POLICY "CheckInsTenantPolicy_select" ON "dbo"."CheckIns" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "CheckInsTenantPolicy_update" ON "dbo"."CheckIns" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "CheckInsTenantPolicy_delete" ON "dbo"."CheckIns" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "CheckInsTenantPolicy_insert" ON "dbo"."CheckIns" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."ClassSubjects" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."ClassSubjects" FORCE ROW LEVEL SECURITY;
CREATE POLICY "ClassSubjectsTenantPolicy_select" ON "dbo"."ClassSubjects" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ClassSubjectsTenantPolicy_update" ON "dbo"."ClassSubjects" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ClassSubjectsTenantPolicy_delete" ON "dbo"."ClassSubjects" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ClassSubjectsTenantPolicy_insert" ON "dbo"."ClassSubjects" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."ClassTestSchedules" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."ClassTestSchedules" FORCE ROW LEVEL SECURITY;
CREATE POLICY "ClassTestSchedulesTenantPolicy_select" ON "dbo"."ClassTestSchedules" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ClassTestSchedulesTenantPolicy_update" ON "dbo"."ClassTestSchedules" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ClassTestSchedulesTenantPolicy_delete" ON "dbo"."ClassTestSchedules" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ClassTestSchedulesTenantPolicy_insert" ON "dbo"."ClassTestSchedules" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."Classes" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."Classes" FORCE ROW LEVEL SECURITY;
CREATE POLICY "ClassesTenantPolicy_select" ON "dbo"."Classes" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ClassesTenantPolicy_update" ON "dbo"."Classes" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ClassesTenantPolicy_delete" ON "dbo"."Classes" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ClassesTenantPolicy_insert" ON "dbo"."Classes" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."Complaints" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."Complaints" FORCE ROW LEVEL SECURITY;
CREATE POLICY "ComplaintsTenantPolicy_select" ON "dbo"."Complaints" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ComplaintsTenantPolicy_update" ON "dbo"."Complaints" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ComplaintsTenantPolicy_delete" ON "dbo"."Complaints" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ComplaintsTenantPolicy_insert" ON "dbo"."Complaints" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."ExamAttendanceRecords" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."ExamAttendanceRecords" FORCE ROW LEVEL SECURITY;
CREATE POLICY "ExamAttendanceRecordsTenantPolicy_select" ON "dbo"."ExamAttendanceRecords" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ExamAttendanceRecordsTenantPolicy_update" ON "dbo"."ExamAttendanceRecords" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ExamAttendanceRecordsTenantPolicy_delete" ON "dbo"."ExamAttendanceRecords" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ExamAttendanceRecordsTenantPolicy_insert" ON "dbo"."ExamAttendanceRecords" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."ExamClasses" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."ExamClasses" FORCE ROW LEVEL SECURITY;
CREATE POLICY "ExamClassesTenantPolicy_select" ON "dbo"."ExamClasses" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ExamClassesTenantPolicy_update" ON "dbo"."ExamClasses" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ExamClassesTenantPolicy_delete" ON "dbo"."ExamClasses" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ExamClassesTenantPolicy_insert" ON "dbo"."ExamClasses" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."ExamPapers" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."ExamPapers" FORCE ROW LEVEL SECURITY;
CREATE POLICY "ExamPapersTenantPolicy_select" ON "dbo"."ExamPapers" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ExamPapersTenantPolicy_update" ON "dbo"."ExamPapers" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ExamPapersTenantPolicy_delete" ON "dbo"."ExamPapers" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ExamPapersTenantPolicy_insert" ON "dbo"."ExamPapers" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."Exams" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."Exams" FORCE ROW LEVEL SECURITY;
CREATE POLICY "ExamsTenantPolicy_select" ON "dbo"."Exams" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ExamsTenantPolicy_update" ON "dbo"."Exams" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ExamsTenantPolicy_delete" ON "dbo"."Exams" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ExamsTenantPolicy_insert" ON "dbo"."Exams" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."FeeHeads" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."FeeHeads" FORCE ROW LEVEL SECURITY;
CREATE POLICY "FeeHeadsTenantPolicy_select" ON "dbo"."FeeHeads" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "FeeHeadsTenantPolicy_update" ON "dbo"."FeeHeads" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "FeeHeadsTenantPolicy_delete" ON "dbo"."FeeHeads" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "FeeHeadsTenantPolicy_insert" ON "dbo"."FeeHeads" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."FeeInvoiceLines" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."FeeInvoiceLines" FORCE ROW LEVEL SECURITY;
CREATE POLICY "FeeInvoiceLinesTenantPolicy_select" ON "dbo"."FeeInvoiceLines" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "FeeInvoiceLinesTenantPolicy_update" ON "dbo"."FeeInvoiceLines" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "FeeInvoiceLinesTenantPolicy_delete" ON "dbo"."FeeInvoiceLines" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "FeeInvoiceLinesTenantPolicy_insert" ON "dbo"."FeeInvoiceLines" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."FeeInvoices" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."FeeInvoices" FORCE ROW LEVEL SECURITY;
CREATE POLICY "FeeInvoicesTenantPolicy_select" ON "dbo"."FeeInvoices" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "FeeInvoicesTenantPolicy_update" ON "dbo"."FeeInvoices" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "FeeInvoicesTenantPolicy_delete" ON "dbo"."FeeInvoices" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "FeeInvoicesTenantPolicy_insert" ON "dbo"."FeeInvoices" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."FeePaymentOrders" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."FeePaymentOrders" FORCE ROW LEVEL SECURITY;
CREATE POLICY "FeePaymentOrdersTenantPolicy_select" ON "dbo"."FeePaymentOrders" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "FeePaymentOrdersTenantPolicy_update" ON "dbo"."FeePaymentOrders" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "FeePaymentOrdersTenantPolicy_delete" ON "dbo"."FeePaymentOrders" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "FeePaymentOrdersTenantPolicy_insert" ON "dbo"."FeePaymentOrders" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."FeePayments" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."FeePayments" FORCE ROW LEVEL SECURITY;
CREATE POLICY "FeePaymentsTenantPolicy_select" ON "dbo"."FeePayments" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "FeePaymentsTenantPolicy_update" ON "dbo"."FeePayments" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "FeePaymentsTenantPolicy_delete" ON "dbo"."FeePayments" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "FeePaymentsTenantPolicy_insert" ON "dbo"."FeePayments" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."FeeStructures" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."FeeStructures" FORCE ROW LEVEL SECURITY;
CREATE POLICY "FeeStructuresTenantPolicy_select" ON "dbo"."FeeStructures" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "FeeStructuresTenantPolicy_update" ON "dbo"."FeeStructures" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "FeeStructuresTenantPolicy_delete" ON "dbo"."FeeStructures" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "FeeStructuresTenantPolicy_insert" ON "dbo"."FeeStructures" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."FuelLogs" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."FuelLogs" FORCE ROW LEVEL SECURITY;
CREATE POLICY "FuelLogsTenantPolicy_select" ON "dbo"."FuelLogs" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "FuelLogsTenantPolicy_update" ON "dbo"."FuelLogs" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "FuelLogsTenantPolicy_delete" ON "dbo"."FuelLogs" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "FuelLogsTenantPolicy_insert" ON "dbo"."FuelLogs" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."Grades" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."Grades" FORCE ROW LEVEL SECURITY;
CREATE POLICY "GradesTenantPolicy_select" ON "dbo"."Grades" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "GradesTenantPolicy_update" ON "dbo"."Grades" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "GradesTenantPolicy_delete" ON "dbo"."Grades" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "GradesTenantPolicy_insert" ON "dbo"."Grades" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."Homework" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."Homework" FORCE ROW LEVEL SECURITY;
CREATE POLICY "HomeworkTenantPolicy_select" ON "dbo"."Homework" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "HomeworkTenantPolicy_update" ON "dbo"."Homework" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "HomeworkTenantPolicy_delete" ON "dbo"."Homework" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "HomeworkTenantPolicy_insert" ON "dbo"."Homework" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."HostelBlocks" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."HostelBlocks" FORCE ROW LEVEL SECURITY;
CREATE POLICY "HostelBlocksTenantPolicy_select" ON "dbo"."HostelBlocks" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "HostelBlocksTenantPolicy_update" ON "dbo"."HostelBlocks" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "HostelBlocksTenantPolicy_delete" ON "dbo"."HostelBlocks" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "HostelBlocksTenantPolicy_insert" ON "dbo"."HostelBlocks" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."HostelResidents" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."HostelResidents" FORCE ROW LEVEL SECURITY;
CREATE POLICY "HostelResidentsTenantPolicy_select" ON "dbo"."HostelResidents" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "HostelResidentsTenantPolicy_update" ON "dbo"."HostelResidents" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "HostelResidentsTenantPolicy_delete" ON "dbo"."HostelResidents" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "HostelResidentsTenantPolicy_insert" ON "dbo"."HostelResidents" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."HostelRooms" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."HostelRooms" FORCE ROW LEVEL SECURITY;
CREATE POLICY "HostelRoomsTenantPolicy_select" ON "dbo"."HostelRooms" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "HostelRoomsTenantPolicy_update" ON "dbo"."HostelRooms" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "HostelRoomsTenantPolicy_delete" ON "dbo"."HostelRooms" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "HostelRoomsTenantPolicy_insert" ON "dbo"."HostelRooms" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."Invitations" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."Invitations" FORCE ROW LEVEL SECURITY;
CREATE POLICY "InvitationsTenantPolicy_select" ON "dbo"."Invitations" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "InvitationsTenantPolicy_update" ON "dbo"."Invitations" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "InvitationsTenantPolicy_delete" ON "dbo"."Invitations" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "InvitationsTenantPolicy_insert" ON "dbo"."Invitations" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."IssueNotes" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."IssueNotes" FORCE ROW LEVEL SECURITY;
CREATE POLICY "IssueNotesTenantPolicy_select" ON "dbo"."IssueNotes" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "IssueNotesTenantPolicy_update" ON "dbo"."IssueNotes" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "IssueNotesTenantPolicy_delete" ON "dbo"."IssueNotes" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "IssueNotesTenantPolicy_insert" ON "dbo"."IssueNotes" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."Issues" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."Issues" FORCE ROW LEVEL SECURITY;
CREATE POLICY "IssuesTenantPolicy_select" ON "dbo"."Issues" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "IssuesTenantPolicy_update" ON "dbo"."Issues" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "IssuesTenantPolicy_delete" ON "dbo"."Issues" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "IssuesTenantPolicy_insert" ON "dbo"."Issues" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."LeaveEntitlements" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."LeaveEntitlements" FORCE ROW LEVEL SECURITY;
CREATE POLICY "LeaveEntitlementsTenantPolicy_select" ON "dbo"."LeaveEntitlements" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "LeaveEntitlementsTenantPolicy_update" ON "dbo"."LeaveEntitlements" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "LeaveEntitlementsTenantPolicy_delete" ON "dbo"."LeaveEntitlements" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "LeaveEntitlementsTenantPolicy_insert" ON "dbo"."LeaveEntitlements" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."LeaveRequests" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."LeaveRequests" FORCE ROW LEVEL SECURITY;
CREATE POLICY "LeaveRequestsTenantPolicy_select" ON "dbo"."LeaveRequests" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "LeaveRequestsTenantPolicy_update" ON "dbo"."LeaveRequests" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "LeaveRequestsTenantPolicy_delete" ON "dbo"."LeaveRequests" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "LeaveRequestsTenantPolicy_insert" ON "dbo"."LeaveRequests" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."LibraryBooks" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."LibraryBooks" FORCE ROW LEVEL SECURITY;
CREATE POLICY "LibraryBooksTenantPolicy_select" ON "dbo"."LibraryBooks" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "LibraryBooksTenantPolicy_update" ON "dbo"."LibraryBooks" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "LibraryBooksTenantPolicy_delete" ON "dbo"."LibraryBooks" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "LibraryBooksTenantPolicy_insert" ON "dbo"."LibraryBooks" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."Notifications" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."Notifications" FORCE ROW LEVEL SECURITY;
CREATE POLICY "NotificationsTenantPolicy_select" ON "dbo"."Notifications" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "NotificationsTenantPolicy_update" ON "dbo"."Notifications" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "NotificationsTenantPolicy_delete" ON "dbo"."Notifications" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "NotificationsTenantPolicy_insert" ON "dbo"."Notifications" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."ParentStudentLinks" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."ParentStudentLinks" FORCE ROW LEVEL SECURITY;
CREATE POLICY "ParentStudentLinksTenantPolicy_select" ON "dbo"."ParentStudentLinks" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ParentStudentLinksTenantPolicy_update" ON "dbo"."ParentStudentLinks" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ParentStudentLinksTenantPolicy_delete" ON "dbo"."ParentStudentLinks" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ParentStudentLinksTenantPolicy_insert" ON "dbo"."ParentStudentLinks" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."PayrollRunLines" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."PayrollRunLines" FORCE ROW LEVEL SECURITY;
CREATE POLICY "PayrollRunLinesTenantPolicy_select" ON "dbo"."PayrollRunLines" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PayrollRunLinesTenantPolicy_update" ON "dbo"."PayrollRunLines" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PayrollRunLinesTenantPolicy_delete" ON "dbo"."PayrollRunLines" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PayrollRunLinesTenantPolicy_insert" ON "dbo"."PayrollRunLines" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."PayrollRuns" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."PayrollRuns" FORCE ROW LEVEL SECURITY;
CREATE POLICY "PayrollRunsTenantPolicy_select" ON "dbo"."PayrollRuns" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PayrollRunsTenantPolicy_update" ON "dbo"."PayrollRuns" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PayrollRunsTenantPolicy_delete" ON "dbo"."PayrollRuns" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PayrollRunsTenantPolicy_insert" ON "dbo"."PayrollRuns" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."Payslips" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."Payslips" FORCE ROW LEVEL SECURITY;
CREATE POLICY "PayslipsTenantPolicy_select" ON "dbo"."Payslips" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PayslipsTenantPolicy_update" ON "dbo"."Payslips" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PayslipsTenantPolicy_delete" ON "dbo"."Payslips" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PayslipsTenantPolicy_insert" ON "dbo"."Payslips" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."PeriodAttendanceAudit" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."PeriodAttendanceAudit" FORCE ROW LEVEL SECURITY;
CREATE POLICY "PeriodAttendanceAuditTenantPolicy_select" ON "dbo"."PeriodAttendanceAudit" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PeriodAttendanceAuditTenantPolicy_update" ON "dbo"."PeriodAttendanceAudit" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PeriodAttendanceAuditTenantPolicy_delete" ON "dbo"."PeriodAttendanceAudit" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PeriodAttendanceAuditTenantPolicy_insert" ON "dbo"."PeriodAttendanceAudit" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."PeriodAttendanceRecords" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."PeriodAttendanceRecords" FORCE ROW LEVEL SECURITY;
CREATE POLICY "PeriodAttendanceRecordsTenantPolicy_select" ON "dbo"."PeriodAttendanceRecords" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PeriodAttendanceRecordsTenantPolicy_update" ON "dbo"."PeriodAttendanceRecords" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PeriodAttendanceRecordsTenantPolicy_delete" ON "dbo"."PeriodAttendanceRecords" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PeriodAttendanceRecordsTenantPolicy_insert" ON "dbo"."PeriodAttendanceRecords" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."PersonExtras" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."PersonExtras" FORCE ROW LEVEL SECURITY;
CREATE POLICY "PersonExtrasTenantPolicy_select" ON "dbo"."PersonExtras" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PersonExtrasTenantPolicy_update" ON "dbo"."PersonExtras" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PersonExtrasTenantPolicy_delete" ON "dbo"."PersonExtras" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PersonExtrasTenantPolicy_insert" ON "dbo"."PersonExtras" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."RoleTemplateOverrides" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."RoleTemplateOverrides" FORCE ROW LEVEL SECURITY;
CREATE POLICY "RoleTemplateOverridesTenantPolicy_select" ON "dbo"."RoleTemplateOverrides" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "RoleTemplateOverridesTenantPolicy_update" ON "dbo"."RoleTemplateOverrides" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "RoleTemplateOverridesTenantPolicy_delete" ON "dbo"."RoleTemplateOverrides" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "RoleTemplateOverridesTenantPolicy_insert" ON "dbo"."RoleTemplateOverrides" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."RouteGeometries" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."RouteGeometries" FORCE ROW LEVEL SECURITY;
CREATE POLICY "RouteGeometriesTenantPolicy_select" ON "dbo"."RouteGeometries" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "RouteGeometriesTenantPolicy_update" ON "dbo"."RouteGeometries" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "RouteGeometriesTenantPolicy_delete" ON "dbo"."RouteGeometries" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "RouteGeometriesTenantPolicy_insert" ON "dbo"."RouteGeometries" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."RouteStops" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."RouteStops" FORCE ROW LEVEL SECURITY;
CREATE POLICY "RouteStopsTenantPolicy_select" ON "dbo"."RouteStops" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "RouteStopsTenantPolicy_update" ON "dbo"."RouteStops" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "RouteStopsTenantPolicy_delete" ON "dbo"."RouteStops" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "RouteStopsTenantPolicy_insert" ON "dbo"."RouteStops" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."SalaryProfiles" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."SalaryProfiles" FORCE ROW LEVEL SECURITY;
CREATE POLICY "SalaryProfilesTenantPolicy_select" ON "dbo"."SalaryProfiles" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SalaryProfilesTenantPolicy_update" ON "dbo"."SalaryProfiles" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SalaryProfilesTenantPolicy_delete" ON "dbo"."SalaryProfiles" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SalaryProfilesTenantPolicy_insert" ON "dbo"."SalaryProfiles" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."SalaryStructures" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."SalaryStructures" FORCE ROW LEVEL SECURITY;
CREATE POLICY "SalaryStructuresTenantPolicy_select" ON "dbo"."SalaryStructures" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SalaryStructuresTenantPolicy_update" ON "dbo"."SalaryStructures" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SalaryStructuresTenantPolicy_delete" ON "dbo"."SalaryStructures" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SalaryStructuresTenantPolicy_insert" ON "dbo"."SalaryStructures" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."SchoolHouses" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."SchoolHouses" FORCE ROW LEVEL SECURITY;
CREATE POLICY "SchoolHousesTenantPolicy_select" ON "dbo"."SchoolHouses" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SchoolHousesTenantPolicy_update" ON "dbo"."SchoolHouses" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SchoolHousesTenantPolicy_delete" ON "dbo"."SchoolHouses" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SchoolHousesTenantPolicy_insert" ON "dbo"."SchoolHouses" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."SchoolLocations" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."SchoolLocations" FORCE ROW LEVEL SECURITY;
CREATE POLICY "SchoolLocationsTenantPolicy_select" ON "dbo"."SchoolLocations" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SchoolLocationsTenantPolicy_update" ON "dbo"."SchoolLocations" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SchoolLocationsTenantPolicy_delete" ON "dbo"."SchoolLocations" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SchoolLocationsTenantPolicy_insert" ON "dbo"."SchoolLocations" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."SportsEvents" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."SportsEvents" FORCE ROW LEVEL SECURITY;
CREATE POLICY "SportsEventsTenantPolicy_select" ON "dbo"."SportsEvents" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SportsEventsTenantPolicy_update" ON "dbo"."SportsEvents" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SportsEventsTenantPolicy_delete" ON "dbo"."SportsEvents" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SportsEventsTenantPolicy_insert" ON "dbo"."SportsEvents" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."SportsMedals" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."SportsMedals" FORCE ROW LEVEL SECURITY;
CREATE POLICY "SportsMedalsTenantPolicy_select" ON "dbo"."SportsMedals" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SportsMedalsTenantPolicy_update" ON "dbo"."SportsMedals" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SportsMedalsTenantPolicy_delete" ON "dbo"."SportsMedals" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SportsMedalsTenantPolicy_insert" ON "dbo"."SportsMedals" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."SportsTeams" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."SportsTeams" FORCE ROW LEVEL SECURITY;
CREATE POLICY "SportsTeamsTenantPolicy_select" ON "dbo"."SportsTeams" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SportsTeamsTenantPolicy_update" ON "dbo"."SportsTeams" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SportsTeamsTenantPolicy_delete" ON "dbo"."SportsTeams" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SportsTeamsTenantPolicy_insert" ON "dbo"."SportsTeams" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."Staff" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."Staff" FORCE ROW LEVEL SECURITY;
CREATE POLICY "StaffTenantPolicy_select" ON "dbo"."Staff" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "StaffTenantPolicy_update" ON "dbo"."Staff" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "StaffTenantPolicy_delete" ON "dbo"."Staff" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "StaffTenantPolicy_insert" ON "dbo"."Staff" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."StaffAttendanceRecords" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."StaffAttendanceRecords" FORCE ROW LEVEL SECURITY;
CREATE POLICY "StaffAttendanceRecordsTenantPolicy_select" ON "dbo"."StaffAttendanceRecords" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "StaffAttendanceRecordsTenantPolicy_update" ON "dbo"."StaffAttendanceRecords" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "StaffAttendanceRecordsTenantPolicy_delete" ON "dbo"."StaffAttendanceRecords" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "StaffAttendanceRecordsTenantPolicy_insert" ON "dbo"."StaffAttendanceRecords" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."StaffDocuments" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."StaffDocuments" FORCE ROW LEVEL SECURITY;
CREATE POLICY "StaffDocumentsTenantPolicy_select" ON "dbo"."StaffDocuments" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "StaffDocumentsTenantPolicy_update" ON "dbo"."StaffDocuments" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "StaffDocumentsTenantPolicy_delete" ON "dbo"."StaffDocuments" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "StaffDocumentsTenantPolicy_insert" ON "dbo"."StaffDocuments" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."StaffTasks" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."StaffTasks" FORCE ROW LEVEL SECURITY;
CREATE POLICY "StaffTasksTenantPolicy_select" ON "dbo"."StaffTasks" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "StaffTasksTenantPolicy_update" ON "dbo"."StaffTasks" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "StaffTasksTenantPolicy_delete" ON "dbo"."StaffTasks" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "StaffTasksTenantPolicy_insert" ON "dbo"."StaffTasks" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."StudentBusAssignments" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."StudentBusAssignments" FORCE ROW LEVEL SECURITY;
CREATE POLICY "StudentBusAssignmentsTenantPolicy_select" ON "dbo"."StudentBusAssignments" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "StudentBusAssignmentsTenantPolicy_update" ON "dbo"."StudentBusAssignments" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "StudentBusAssignmentsTenantPolicy_delete" ON "dbo"."StudentBusAssignments" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "StudentBusAssignmentsTenantPolicy_insert" ON "dbo"."StudentBusAssignments" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."StudentTransportOptOut" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."StudentTransportOptOut" FORCE ROW LEVEL SECURITY;
CREATE POLICY "StudentTransportOptOutTenantPolicy_select" ON "dbo"."StudentTransportOptOut" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "StudentTransportOptOutTenantPolicy_update" ON "dbo"."StudentTransportOptOut" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "StudentTransportOptOutTenantPolicy_delete" ON "dbo"."StudentTransportOptOut" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "StudentTransportOptOutTenantPolicy_insert" ON "dbo"."StudentTransportOptOut" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."Students" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."Students" FORCE ROW LEVEL SECURITY;
CREATE POLICY "StudentsTenantPolicy_select" ON "dbo"."Students" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "StudentsTenantPolicy_update" ON "dbo"."Students" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "StudentsTenantPolicy_delete" ON "dbo"."Students" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "StudentsTenantPolicy_insert" ON "dbo"."Students" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."Subjects" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."Subjects" FORCE ROW LEVEL SECURITY;
CREATE POLICY "SubjectsTenantPolicy_select" ON "dbo"."Subjects" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SubjectsTenantPolicy_update" ON "dbo"."Subjects" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SubjectsTenantPolicy_delete" ON "dbo"."Subjects" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "SubjectsTenantPolicy_insert" ON "dbo"."Subjects" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."Tasks" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."Tasks" FORCE ROW LEVEL SECURITY;
CREATE POLICY "TasksTenantPolicy_select" ON "dbo"."Tasks" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TasksTenantPolicy_update" ON "dbo"."Tasks" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TasksTenantPolicy_delete" ON "dbo"."Tasks" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TasksTenantPolicy_insert" ON "dbo"."Tasks" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."Teachers" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."Teachers" FORCE ROW LEVEL SECURITY;
CREATE POLICY "TeachersTenantPolicy_select" ON "dbo"."Teachers" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TeachersTenantPolicy_update" ON "dbo"."Teachers" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TeachersTenantPolicy_delete" ON "dbo"."Teachers" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TeachersTenantPolicy_insert" ON "dbo"."Teachers" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."TenantPaymentCredentials" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."TenantPaymentCredentials" FORCE ROW LEVEL SECURITY;
CREATE POLICY "TenantPaymentCredentialsTenantPolicy_select" ON "dbo"."TenantPaymentCredentials" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TenantPaymentCredentialsTenantPolicy_update" ON "dbo"."TenantPaymentCredentials" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TenantPaymentCredentialsTenantPolicy_delete" ON "dbo"."TenantPaymentCredentials" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TenantPaymentCredentialsTenantPolicy_insert" ON "dbo"."TenantPaymentCredentials" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."TimetableSlots" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."TimetableSlots" FORCE ROW LEVEL SECURITY;
CREATE POLICY "TimetableSlotsTenantPolicy_select" ON "dbo"."TimetableSlots" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TimetableSlotsTenantPolicy_update" ON "dbo"."TimetableSlots" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TimetableSlotsTenantPolicy_delete" ON "dbo"."TimetableSlots" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TimetableSlotsTenantPolicy_insert" ON "dbo"."TimetableSlots" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."TransportRoutes" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."TransportRoutes" FORCE ROW LEVEL SECURITY;
CREATE POLICY "TransportRoutesTenantPolicy_select" ON "dbo"."TransportRoutes" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TransportRoutesTenantPolicy_update" ON "dbo"."TransportRoutes" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TransportRoutesTenantPolicy_delete" ON "dbo"."TransportRoutes" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TransportRoutesTenantPolicy_insert" ON "dbo"."TransportRoutes" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."TripPings" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."TripPings" FORCE ROW LEVEL SECURITY;
CREATE POLICY "TripPingsTenantPolicy_select" ON "dbo"."TripPings" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TripPingsTenantPolicy_update" ON "dbo"."TripPings" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TripPingsTenantPolicy_delete" ON "dbo"."TripPings" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TripPingsTenantPolicy_insert" ON "dbo"."TripPings" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."TripStopProgress" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."TripStopProgress" FORCE ROW LEVEL SECURITY;
CREATE POLICY "TripStopProgressTenantPolicy_select" ON "dbo"."TripStopProgress" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TripStopProgressTenantPolicy_update" ON "dbo"."TripStopProgress" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TripStopProgressTenantPolicy_delete" ON "dbo"."TripStopProgress" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TripStopProgressTenantPolicy_insert" ON "dbo"."TripStopProgress" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."Trips" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."Trips" FORCE ROW LEVEL SECURITY;
CREATE POLICY "TripsTenantPolicy_select" ON "dbo"."Trips" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TripsTenantPolicy_update" ON "dbo"."Trips" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TripsTenantPolicy_delete" ON "dbo"."Trips" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "TripsTenantPolicy_insert" ON "dbo"."Trips" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."UserAppSettings" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."UserAppSettings" FORCE ROW LEVEL SECURITY;
CREATE POLICY "UserAppSettingsTenantPolicy_select" ON "dbo"."UserAppSettings" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "UserAppSettingsTenantPolicy_update" ON "dbo"."UserAppSettings" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "UserAppSettingsTenantPolicy_delete" ON "dbo"."UserAppSettings" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "UserAppSettingsTenantPolicy_insert" ON "dbo"."UserAppSettings" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."Users" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."Users" FORCE ROW LEVEL SECURITY;
CREATE POLICY "UsersTenantPolicy_select" ON "dbo"."Users" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "UsersTenantPolicy_update" ON "dbo"."Users" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "UsersTenantPolicy_delete" ON "dbo"."Users" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "UsersTenantPolicy_insert" ON "dbo"."Users" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

ALTER TABLE "dbo"."VehicleInspections" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."VehicleInspections" FORCE ROW LEVEL SECURITY;
CREATE POLICY "VehicleInspectionsTenantPolicy_select" ON "dbo"."VehicleInspections" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "VehicleInspectionsTenantPolicy_update" ON "dbo"."VehicleInspections" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "VehicleInspectionsTenantPolicy_delete" ON "dbo"."VehicleInspections" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "VehicleInspectionsTenantPolicy_insert" ON "dbo"."VehicleInspections" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
