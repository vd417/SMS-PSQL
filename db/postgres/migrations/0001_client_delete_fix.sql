-- 0001: dbo.client_delete (school deletion, DELETE /v1/clients/{id} and DELETE /v1/me/schools/{id}).
--
-- 1. Parameter name. ClientRepository.DeleteEmptyAsync calls this with named notation
--    ("id" => @Id, see BaseRepository.FunctionCallSql), but the baseline declared it as p_id, so
--    every school deletion failed with "function does not exist". The name now follows the
--    project convention: the original proc's parameter name (@Id), referenced as client_delete.Id.
-- 2. Data integrity. The baseline port omitted four DELETEs present in the source proc
--    (db/Sms.Migrations/procs/catredel/Client_Delete.sql): UserAppSettings,
--    PeriodAttendanceAudit, PeriodAttendanceRecords and Achievements, which would have left that
--    school's rows orphaned. They're restored here in the source's order.
--
-- A parameter can't be renamed with CREATE OR REPLACE, hence DROP + CREATE. dbo.client_delete_result
-- is unchanged. Not destructive: this only replaces a function definition. Rollback, if ever
-- needed, is restoring the previous definition from db/postgres/08_sample_procedure_conversions.sql.

DROP FUNCTION IF EXISTS dbo.client_delete(uuid);

CREATE FUNCTION dbo.client_delete(Id uuid)
RETURNS dbo.client_delete_result
LANGUAGE plpgsql
AS $$
DECLARE
    v_students int;
    v_teachers int;
    v_staff int;
    v_result dbo.client_delete_result;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM "dbo"."Tenants" WHERE "Id" = client_delete.Id) THEN
        v_result := (false, 'not_found', 0, 0, 0);
        RETURN v_result;
    END IF;

    SELECT COUNT(*) INTO v_students FROM "dbo"."Students" WHERE "TenantId" = client_delete.Id;
    SELECT COUNT(*) INTO v_teachers FROM "dbo"."Teachers" WHERE "TenantId" = client_delete.Id;
    SELECT COUNT(*) INTO v_staff FROM "dbo"."Staff" WHERE "TenantId" = client_delete.Id;

    IF v_students > 0 OR v_teachers > 0 OR v_staff > 0 THEN
        v_result := (false, 'has_people', v_students, v_teachers, v_staff);
        RETURN v_result;
    END IF;

    -- Runs inside the caller's transaction: any error below rolls back every DELETE.
    DELETE FROM "dbo"."PlanUpgradeRequests" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Invoices" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Subscriptions" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."OnboardingItems" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."AuditLog" WHERE "TenantId" = client_delete.Id;

    DELETE FROM "dbo"."RefreshTokens" rt
        USING "dbo"."Users" u
        WHERE u."Id" = rt."UserId" AND u."TenantId" = client_delete.Id;
    DELETE FROM "dbo"."UserRoles" ur
        USING "dbo"."Users" u
        WHERE u."Id" = ur."UserId" AND u."TenantId" = client_delete.Id;
    DELETE FROM "dbo"."UserAppSettings" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Users" WHERE "TenantId" = client_delete.Id;

    DELETE FROM "dbo"."AttendanceRecords" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."PeriodAttendanceAudit" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."PeriodAttendanceRecords" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."ExamPapers" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Exams" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Homework" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Achievements" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Assignments" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."FeePayments" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."FeeInvoices" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Payslips" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."LeaveRequests" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."TimetableSlots" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Subjects" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Classes" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Grades" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."CalendarEvents" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Announcements" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Notifications" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."ChatMessages" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."ChatThreads" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Complaints" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Tickets" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."CheckIns" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Boardings" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."TripPings" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Trips" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."BusAssignments" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."BusStops" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."Buses" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."LibraryBooks" WHERE "TenantId" = client_delete.Id;
    DELETE FROM "dbo"."SchoolLocations" WHERE "TenantId" = client_delete.Id;

    DELETE FROM "dbo"."Tenants" WHERE "Id" = client_delete.Id;

    v_result := (true, 'deleted', 0, 0, 0);
    RETURN v_result;
END;
$$;

GRANT EXECUTE ON FUNCTION dbo.client_delete(uuid) TO sms_app;
