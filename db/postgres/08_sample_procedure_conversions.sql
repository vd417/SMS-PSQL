-- Worked proof-of-pattern conversions for 4 of the 200 SQL Server stored procedures, chosen to
-- span the distinct construct patterns found across the full set (see the audit doc section E /
-- "Object conversion matrix" for the per-pattern counts these were drawn from). These 4 are
-- CONVERTED. The remaining ~196 procedures are NOT converted in this pass -- each is classified
-- in sqlserver-object-inventory.csv by which of these patterns it matches, with status
-- "PENDING PER-PROCEDURE CONVERSION". Converting all 200 by hand is a multi-week follow-on
-- effort, not something to fake here.

-- =====================================================================================
-- 1) dbo.User_Create -- simplest pattern: single INSERT + ISNULL defaults + scalar return.
--    Represents ~40 similar "_Create" procs (see csv). Pattern: ISNULL -> COALESCE,
--    NEWID() -> gen_random_uuid(), single-row INSERT ... RETURNING replaces the trailing
--    SELECT @Id AS Id.
-- =====================================================================================
-- Original (SQL Server):
--   CREATE PROCEDURE dbo.User_Create
--       @TenantId uniqueidentifier, @Email nvarchar(256), @Phone nvarchar(32), @IsPlatform bit,
--       @StudentId nvarchar(64) = NULL, @MustSetPassword bit = 0
--   AS BEGIN
--       SET NOCOUNT ON;
--       DECLARE @Id uniqueidentifier = NEWID();
--       INSERT dbo.Users (Id, TenantId, Email, Phone, IsPlatform, Status, StudentId, MustSetPassword)
--       VALUES (@Id, @TenantId, @Email, @Phone, ISNULL(@IsPlatform, 0), 'active', @StudentId, ISNULL(@MustSetPassword, 0));
--       SELECT @Id AS Id;
--   END

CREATE OR REPLACE FUNCTION dbo.user_create(
    p_tenant_id uuid,
    p_email varchar(256),
    p_phone varchar(32),
    p_is_platform boolean,
    p_student_id varchar(64) DEFAULT NULL,
    p_must_set_password boolean DEFAULT false
) RETURNS uuid
LANGUAGE plpgsql
AS $$
DECLARE
    v_id uuid;
BEGIN
    INSERT INTO "dbo"."Users" ("Id", "TenantId", "Email", "Phone", "IsPlatform", "Status", "StudentId", "MustSetPassword")
    VALUES (gen_random_uuid(), p_tenant_id, p_email, p_phone, COALESCE(p_is_platform, false), 'active', p_student_id, COALESCE(p_must_set_password, false))
    RETURNING "Id" INTO v_id;
    RETURN v_id;
END;
$$;
-- Call site change (Sms.Infrastructure/DAO/UserProvisioningDao.cs QuerySingleProcAsync<Guid>):
-- Dapper CommandType.StoredProcedure against a SQL Server proc maps to a Postgres FUNCTION call
-- via `SELECT * FROM dbo.user_create(@TenantId, @Email, ...)` with CommandType.Text (Npgsql has
-- no native "call a stored proc and get a resultset back" CommandType.StoredProcedure equivalent
-- for functions returning a scalar/table -- this is a real, required BaseRepository change, not
-- optional; see the audit's backend-impact section).


-- =====================================================================================
-- 2) dbo.TripPing_BulkInsert -- TVP bulk insert. Represents the "READONLY table parameter"
--    family (6 procs: Attendance_BulkUpsert, ExamAttendance_BulkUpsert,
--    PeriodAttendance_BulkUpsert, StaffAttendance_BulkUpsert, TripPing_BulkInsert,
--    Users_BulkCreate). TVP -> jsonb parameter + jsonb_to_recordset(), per the strategy
--    documented in 02_types.sql.
-- =====================================================================================
-- Original (SQL Server):
--   CREATE PROCEDURE dbo.TripPing_BulkInsert
--       @TenantId uniqueidentifier, @TripId uniqueidentifier, @Rows dbo.TripPingTvp READONLY
--   AS BEGIN
--       SET NOCOUNT ON;
--       INSERT dbo.TripPings (Id, TenantId, TripId, Lat, Lng, SpeedKmh, Heading, At, Accuracy)
--       SELECT NEWID(), @TenantId, @TripId, Lat, Lng, SpeedKmh, Heading, At, Accuracy FROM @Rows;
--   END

CREATE OR REPLACE FUNCTION dbo.trip_ping_bulk_insert(
    p_tenant_id uuid,
    p_trip_id uuid,
    p_rows jsonb  -- array of {"Lat":..,"Lng":..,"SpeedKmh":..,"Heading":..,"At":"...","Accuracy":..}
) RETURNS void
LANGUAGE sql
AS $$
    INSERT INTO "dbo"."TripPings" ("Id", "TenantId", "TripId", "Lat", "Lng", "SpeedKmh", "Heading", "At", "Accuracy")
    SELECT gen_random_uuid(), p_tenant_id, p_trip_id, r."Lat", r."Lng", r."SpeedKmh", r."Heading", r."At", r."Accuracy"
    FROM jsonb_to_recordset(p_rows) AS r(
        "Lat" double precision, "Lng" double precision, "SpeedKmh" double precision,
        "Heading" double precision, "At" timestamptz, "Accuracy" double precision
    );
$$;
-- Call site change (Sms.Application/Services/Transport or wherever TripPing_BulkInsert is
-- invoked): the C# side currently builds a System.Data.DataTable and passes it via
-- `table.AsTableValuedParameter("dbo.TripPingTvp")` (Dapper + Microsoft.Data.SqlClient
-- SqlDbType.Structured). For Postgres this becomes `JsonSerializer.Serialize(rows)` passed as a
-- single jsonb/text parameter -- a real DAO code change, tracked in the backend-impact section,
-- not just a schema change.


-- =====================================================================================
-- 3) dbo.Attendance_BulkUpsert -- MERGE against a TVP source. Represents the MERGE family
--    (13 procs). MERGE -> INSERT ... ON CONFLICT ... DO UPDATE, keyed on the same match columns
--    the MERGE's ON clause used. Requires a matching unique constraint/index in Postgres on
--    (TenantId, ClassId, StudentId, "Date") for AttendanceRecords -- confirm this exists or add
--    it (not present in the live index/constraint extraction as a *named* unique constraint;
--    flagged as a required addition, not assumed).
-- =====================================================================================
-- Original (SQL Server):
--   CREATE PROCEDURE dbo.Attendance_BulkUpsert
--       @TenantId uniqueidentifier, @ClassId uniqueidentifier, @Date date,
--       @MarkedBy uniqueidentifier, @Rows dbo.AttendanceTvp READONLY
--   AS BEGIN
--       SET NOCOUNT ON;
--       MERGE dbo.AttendanceRecords AS tgt
--       USING (SELECT StudentId, Status FROM @Rows) AS src
--           ON tgt.TenantId = @TenantId AND tgt.ClassId = @ClassId
--              AND tgt.StudentId = src.StudentId AND tgt.[Date] = @Date
--       WHEN MATCHED THEN UPDATE SET Status = src.Status, MarkedBy = @MarkedBy
--       WHEN NOT MATCHED THEN
--           INSERT (Id, TenantId, ClassId, StudentId, [Date], Status, MarkedBy)
--           VALUES (NEWID(), @TenantId, @ClassId, src.StudentId, @Date, src.Status, @MarkedBy);
--   END

-- REQUIRED PRE-REQUISITE (not present in source schema -- must be added for ON CONFLICT to work):
--   ALTER TABLE "dbo"."AttendanceRecords"
--     ADD CONSTRAINT "UQ_AttendanceRecords_Tenant_Class_Student_Date"
--     UNIQUE ("TenantId", "ClassId", "StudentId", "Date");

CREATE OR REPLACE FUNCTION dbo.attendance_bulk_upsert(
    p_tenant_id uuid,
    p_class_id uuid,
    p_date date,
    p_marked_by uuid,
    p_rows jsonb  -- array of {"StudentId": "...", "Status": "..."}
) RETURNS void
LANGUAGE sql
AS $$
    INSERT INTO "dbo"."AttendanceRecords" ("Id", "TenantId", "ClassId", "StudentId", "Date", "Status", "MarkedBy")
    SELECT gen_random_uuid(), p_tenant_id, p_class_id, r."StudentId", p_date, r."Status", p_marked_by
    FROM jsonb_to_recordset(p_rows) AS r("StudentId" uuid, "Status" varchar(20))
    ON CONFLICT ("TenantId", "ClassId", "StudentId", "Date")
    DO UPDATE SET "Status" = EXCLUDED."Status", "MarkedBy" = EXCLUDED."MarkedBy";
$$;
-- Same call-site TVP->jsonb change as pattern (2) applies to all 6 TVP procs and to the other
-- 12 non-TVP MERGE procs' ON-clause -> ON CONFLICT translation individually (each has a
-- different natural key; do not assume they all match this shape).


-- =====================================================================================
-- 4) dbo.Client_Delete -- TRY/CATCH + explicit transaction + ~30 cascading DELETEs + early
--    RETURN with a result-shaped SELECT. Represents the "BEGIN TRY/BEGIN TRAN" family
--    (5 procs: Client_Delete, FeePayment_Create, Parent_EnsureLogin, Student_EnsureLogin,
--    sp_UpdateStudentFull). TRY/CATCH+THROW -> plpgsql's implicit transaction + EXCEPTION block
--    (Postgres functions are transactional by default; ROLLBACK is automatic on unhandled
--    exception, so BEGIN TRAN/COMMIT TRAN/ROLLBACK TRAN have no direct equivalent needed --
--    only re-raising needs an EXCEPTION handler when the original caught error must be
--    inspected/logged before propagating, otherwise it can often be simplified away). Early
--    RETURN with a shaped result row becomes RETURN QUERY / a composite OUT-style return.
-- =====================================================================================
-- Original (SQL Server): see 4)'s source above the DAO summary -- full text extracted verbatim
-- from OBJECT_DEFINITION(object_id) at C:\Users\user\AppData\Local\Temp\sample_client_delete.sql
-- during this audit; omitted here for brevity, reproduced faithfully in the plpgsql below.

CREATE TYPE dbo.client_delete_result AS (
    ok boolean,
    code text,
    students int,
    teachers int,
    staff int
);

CREATE OR REPLACE FUNCTION dbo.client_delete(p_id uuid)
RETURNS dbo.client_delete_result
LANGUAGE plpgsql
AS $$
DECLARE
    v_students int;
    v_teachers int;
    v_staff int;
    v_result dbo.client_delete_result;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM "dbo"."Tenants" WHERE "Id" = p_id) THEN
        v_result := (false, 'not_found', 0, 0, 0);
        RETURN v_result;
    END IF;

    SELECT COUNT(*) INTO v_students FROM "dbo"."Students" WHERE "TenantId" = p_id;
    SELECT COUNT(*) INTO v_teachers FROM "dbo"."Teachers" WHERE "TenantId" = p_id;
    SELECT COUNT(*) INTO v_staff FROM "dbo"."Staff" WHERE "TenantId" = p_id;

    IF v_students > 0 OR v_teachers > 0 OR v_staff > 0 THEN
        v_result := (false, 'has_people', v_students, v_teachers, v_staff);
        RETURN v_result;
    END IF;

    -- No explicit BEGIN/COMMIT: the calling connection's transaction (or the implicit
    -- single-statement transaction Npgsql opens for this function call) covers the whole body.
    -- Any error below raises out of the function and rolls back automatically, matching the
    -- SQL Server BEGIN TRY / ROLLBACK TRAN / THROW behavior without needing to write it.

    DELETE FROM "dbo"."PlanUpgradeRequests" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."Invoices" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."Subscriptions" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."OnboardingItems" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."AuditLog" WHERE "TenantId" = p_id;

    DELETE FROM "dbo"."RefreshTokens" rt
        USING "dbo"."Users" u
        WHERE u."Id" = rt."UserId" AND u."TenantId" = p_id;
    DELETE FROM "dbo"."UserRoles" ur
        USING "dbo"."Users" u
        WHERE u."Id" = ur."UserId" AND u."TenantId" = p_id;
    -- UserLogin.UserId is int (legacy), not linked to dbo.Users.Id -- same caveat as source.
    DELETE FROM "dbo"."Users" WHERE "TenantId" = p_id;

    DELETE FROM "dbo"."AttendanceRecords" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."ExamPapers" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."Exams" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."Homework" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."Assignments" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."FeePayments" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."FeeInvoices" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."Payslips" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."LeaveRequests" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."TimetableSlots" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."Subjects" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."Classes" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."Grades" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."CalendarEvents" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."Announcements" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."Notifications" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."ChatMessages" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."ChatThreads" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."Complaints" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."Tickets" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."CheckIns" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."Boardings" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."TripPings" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."Trips" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."BusAssignments" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."BusStops" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."Buses" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."LibraryBooks" WHERE "TenantId" = p_id;
    DELETE FROM "dbo"."SchoolLocations" WHERE "TenantId" = p_id;

    DELETE FROM "dbo"."Tenants" WHERE "Id" = p_id;

    v_result := (true, 'deleted', 0, 0, 0);
    RETURN v_result;
END;
$$;
-- NOTE ON RLS INTERACTION: every DELETE above runs under the RLS policies from
-- 07_rls_policies.sql. dbo.Tenants itself is a platform-owned table (no TenantId column, not in
-- the 84 tenant-scoped table list) -- confirm Tenants has no RLS policy applied in Postgres
-- either, matching SQL Server (it wasn't in the 84-row rls.txt extraction). The caller of this
-- function must have rls.is_platform() = true (app.is_platform = '1' session var) for these
-- cross-tenant cascading deletes to actually remove rows -- same precondition as the SQL Server
-- version relying on SESSION_CONTEXT('IsPlatform').
