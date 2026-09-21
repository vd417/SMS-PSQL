-- Sis module: PL/pgSQL conversions of 10 of the 12 stored procedures in the
-- AddStudent/Parent_EnsureLogin/StudentBus_*/StudentTransport_*/Student_* family.
-- Source: full OBJECT_DEFINITION() extracted read-only from the live SQL Server Sms database on
-- 2026-09-21, cross-checked against sqlserver-object-inventory.csv.
--
-- dbo.AddStudent is NOT converted: it targets a completely different legacy schema (int-PK
-- Student/Parent/Address tables with OrganizationId/SchoolId, not the modern uuid-PK
-- Students/TenantId model everything else in this file uses) and has zero C# call sites anywhere
-- in src/ or tests/ -- dead code, not something to translate on spec.
--
-- See 09_auth_procs.sql's header for the naming convention, and 10_tenancy_procs.sql's header
-- for the `#variable_conflict use_column` note (needed here too, for the same
-- RETURNS-TABLE-column-vs-parameter-name reason).

-- ============================================================
-- Student_RenumberClass (helper, called by Student_Create/Student_Update)
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.student_renumberclass(TenantId uuid, Grade varchar(20), Section varchar(20))
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    IF student_renumberclass.TenantId IS NULL THEN RETURN; END IF;

    WITH ranked AS (
        SELECT "Id",
               row_number() OVER (ORDER BY "Name" ASC, "AdmissionNo" ASC, "Id" ASC) AS rn
        FROM "dbo"."Students"
        WHERE "TenantId" = student_renumberclass.TenantId
          AND "Status" = 'active'
          AND COALESCE("Grade", '') = COALESCE(student_renumberclass.Grade, '')
          AND COALESCE("Section", '') = COALESCE(student_renumberclass.Section, '')
    )
    UPDATE "dbo"."Students" s SET "Roll" = r.rn
    FROM ranked r
    WHERE r."Id" = s."Id";
END;
$$;

-- ============================================================
-- Student
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.student_getbyadmissionno(AdmissionId varchar(64))
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "AdmissionNo" varchar(64), "Name" varchar(200),
               "Email" varchar(256), "GuardianPhone" varchar(40), "Status" varchar(20), "GuardianEmail" varchar(256))
LANGUAGE sql
AS $$
    SELECT s."Id", s."TenantId", s."AdmissionNo", s."Name", s."Email", s."GuardianPhone", s."Status", s."GuardianEmail"
    FROM "dbo"."Students" s
    WHERE lower(trim(s."AdmissionNo")) = lower(trim(student_getbyadmissionno.AdmissionId))
    LIMIT 1;
$$;

-- TRY_CAST(SUBSTRING(...) AS int) -> a regex-guarded cast, same narrow approach as
-- 10_tenancy_procs.sql's planupgraderequest_listbytenants (the audit's shared safe_cast() helper
-- is separate, not-yet-done work).
CREATE OR REPLACE FUNCTION dbo.student_create(
    TenantId uuid, AdmissionNo varchar(64), Name varchar(200), Gender varchar(1),
    Grade varchar(20), Section varchar(20), Roll int, GuardianName varchar(200),
    GuardianPhone varchar(40), GuardianEmail varchar(256), House varchar(40), AvatarHue int,
    Dob timestamptz, Email varchar(256), Address varchar(500)
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "AdmissionNo" varchar(64), "Name" varchar(200), "Gender" varchar(1),
    "Grade" varchar(20), "Section" varchar(20), "ClassLabel" varchar(40), "Roll" int, "GuardianName" varchar(200),
    "GuardianPhone" varchar(40), "GuardianEmail" varchar(256), "AttendancePct" numeric(5,2), "FeeStatus" varchar(20),
    "FeeDue" numeric(18,2), "Status" varchar(20), "House" varchar(40), "AvatarHue" int, "Dob" timestamptz,
    "Email" varchar(256), "Address" varchar(500), "PhotoUrl" text
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_id uuid := gen_random_uuid();
    v_class_label varchar(40) :=
        CASE WHEN student_create.Grade IS NULL OR student_create.Section IS NULL THEN NULL
             ELSE student_create.Grade || '-' || student_create.Section END;
    v_admission_no varchar(64) := student_create.AdmissionNo;
    v_slug varchar(48);
    v_year varchar(2);
    v_prefix varchar(80);
    v_next int;
BEGIN
    IF v_admission_no IS NULL OR trim(v_admission_no) = '' THEN
        SELECT lower(replace(t."Slug", '-', '')) INTO v_slug FROM "dbo"."Tenants" t WHERE t."Id" = student_create.TenantId;
        IF v_slug IS NULL OR v_slug = '' THEN v_slug := 'sch'; END IF;

        v_year := right(extract(year FROM now())::text, 2);
        v_prefix := v_slug || '/STU/' || v_year || '/';

        SELECT COALESCE(MAX(
            CASE WHEN substring(s."AdmissionNo" FROM length(v_prefix) + 1) ~ '^[0-9]+$'
                 THEN substring(s."AdmissionNo" FROM length(v_prefix) + 1)::int END
        ), 0) + 1
        INTO v_next
        FROM "dbo"."Students" s
        WHERE s."TenantId" = student_create.TenantId
          AND s."AdmissionNo" LIKE v_prefix || '%';

        v_admission_no := v_prefix || lpad(v_next::text, 4, '0');
    ELSE
        v_admission_no := trim(v_admission_no);
    END IF;

    INSERT INTO "dbo"."Students" ("Id", "TenantId", "AdmissionNo", "Name", "Gender", "Grade", "Section", "ClassLabel", "Roll",
        "GuardianName", "GuardianPhone", "GuardianEmail", "House", "AvatarHue", "Dob", "Email", "Address")
    VALUES (v_id, student_create.TenantId, v_admission_no, student_create.Name, student_create.Gender,
        student_create.Grade, student_create.Section, v_class_label, 0,
        student_create.GuardianName, student_create.GuardianPhone, student_create.GuardianEmail,
        student_create.House, COALESCE(student_create.AvatarHue, 0), student_create.Dob, student_create.Email,
        student_create.Address);

    PERFORM dbo.student_renumberclass(student_create.TenantId, student_create.Grade, student_create.Section);

    UPDATE "dbo"."Tenants"
    SET "StudentsCount" = (
        SELECT count(*) FROM "dbo"."Students" s WHERE s."TenantId" = student_create.TenantId AND s."Status" = 'active'
    )
    WHERE "Id" = student_create.TenantId;

    RETURN QUERY
    SELECT s."Id", s."TenantId", s."AdmissionNo", s."Name", s."Gender", s."Grade", s."Section", s."ClassLabel", s."Roll",
           s."GuardianName", s."GuardianPhone", s."GuardianEmail", s."AttendancePct", s."FeeStatus", s."FeeDue",
           s."Status", s."House", s."AvatarHue", s."Dob", s."Email", s."Address", s."PhotoUrl"
    FROM "dbo"."Students" s WHERE s."Id" = v_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.student_update(
    Id uuid, Name varchar(200) DEFAULT NULL, Grade varchar(20) DEFAULT NULL, Section varchar(20) DEFAULT NULL,
    Roll int DEFAULT NULL, GuardianName varchar(200) DEFAULT NULL, GuardianPhone varchar(40) DEFAULT NULL,
    GuardianEmail varchar(256) DEFAULT NULL, House varchar(40) DEFAULT NULL, FeeStatus varchar(20) DEFAULT NULL,
    FeeDue numeric(18,2) DEFAULT NULL, Status varchar(20) DEFAULT NULL, PhotoUrl text DEFAULT NULL,
    SetPhoto boolean DEFAULT false, Gender varchar(1) DEFAULT NULL, Dob timestamptz DEFAULT NULL,
    Email varchar(256) DEFAULT NULL, Address varchar(500) DEFAULT NULL, AvatarHue int DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "AdmissionNo" varchar(64), "Name" varchar(200), "Gender" varchar(1),
    "Grade" varchar(20), "Section" varchar(20), "ClassLabel" varchar(40), "Roll" int, "GuardianName" varchar(200),
    "GuardianPhone" varchar(40), "GuardianEmail" varchar(256), "AttendancePct" numeric(5,2), "FeeStatus" varchar(20),
    "FeeDue" numeric(18,2), "Status" varchar(20), "House" varchar(40), "AvatarHue" int, "Dob" timestamptz,
    "Email" varchar(256), "Address" varchar(500), "PhotoUrl" text
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_tenant_id uuid;
    v_old_grade varchar(20);
    v_old_section varchar(20);
    v_new_grade varchar(20);
    v_new_section varchar(20);
BEGIN
    SELECT s."TenantId", s."Grade", s."Section" INTO v_tenant_id, v_old_grade, v_old_section
    FROM "dbo"."Students" s WHERE s."Id" = student_update.Id;

    UPDATE "dbo"."Students" SET
        "Name" = COALESCE(student_update.Name, "Name"),
        "Grade" = COALESCE(student_update.Grade, "Grade"),
        "Section" = COALESCE(student_update.Section, "Section"),
        "ClassLabel" = COALESCE(student_update.Grade, "Grade") || '-' || COALESCE(student_update.Section, "Section"),
        "GuardianName" = COALESCE(student_update.GuardianName, "GuardianName"),
        "GuardianPhone" = COALESCE(student_update.GuardianPhone, "GuardianPhone"),
        "GuardianEmail" = COALESCE(student_update.GuardianEmail, "GuardianEmail"),
        "House" = COALESCE(student_update.House, "House"),
        "FeeStatus" = COALESCE(student_update.FeeStatus, "FeeStatus"),
        "FeeDue" = COALESCE(student_update.FeeDue, "FeeDue"),
        "Status" = COALESCE(student_update.Status, "Status"),
        "PhotoUrl" = CASE WHEN student_update.SetPhoto THEN student_update.PhotoUrl ELSE "PhotoUrl" END,
        "Gender" = COALESCE(student_update.Gender, "Gender"),
        "Dob" = COALESCE(student_update.Dob, "Dob"),
        "Email" = COALESCE(student_update.Email, "Email"),
        "Address" = COALESCE(student_update.Address, "Address"),
        "AvatarHue" = COALESCE(student_update.AvatarHue, "AvatarHue")
    WHERE "Id" = student_update.Id;

    UPDATE "dbo"."Students" SET "Roll" = 0 WHERE "Id" = student_update.Id AND "Status" <> 'active';

    IF v_tenant_id IS NOT NULL THEN
        SELECT s."Grade", s."Section" INTO v_new_grade, v_new_section
        FROM "dbo"."Students" s WHERE s."Id" = student_update.Id;

        PERFORM dbo.student_renumberclass(v_tenant_id, v_old_grade, v_old_section);
        IF COALESCE(v_new_grade, '') <> COALESCE(v_old_grade, '')
           OR COALESCE(v_new_section, '') <> COALESCE(v_old_section, '') THEN
            PERFORM dbo.student_renumberclass(v_tenant_id, v_new_grade, v_new_section);
        END IF;

        UPDATE "dbo"."Tenants"
        SET "StudentsCount" = (
            SELECT count(*) FROM "dbo"."Students" s WHERE s."TenantId" = v_tenant_id AND s."Status" = 'active'
        )
        WHERE "Id" = v_tenant_id;
    END IF;

    RETURN QUERY
    SELECT s."Id", s."TenantId", s."AdmissionNo", s."Name", s."Gender", s."Grade", s."Section", s."ClassLabel", s."Roll",
           s."GuardianName", s."GuardianPhone", s."GuardianEmail", s."AttendancePct", s."FeeStatus", s."FeeDue",
           s."Status", s."House", s."AvatarHue", s."Dob", s."Email", s."Address", s."PhotoUrl"
    FROM "dbo"."Students" s WHERE s."Id" = student_update.Id;
END;
$$;

-- ============================================================
-- Student_EnsureLogin / Parent_EnsureLogin
-- UPDLOCK/HOLDLOCK -> SELECT ... FOR UPDATE: locks the matched Students row for the rest of the
-- transaction, preventing a concurrent call from racing to create a second login for the same
-- admission number. This is the closest native Postgres equivalent, reviewed for this specific
-- "don't double-provision a login" use case (per the audit's guidance to pick the right locking
-- primitive deliberately, not substitute mechanically) -- flagged here for visibility, not a
-- silent choice.
-- BEGIN TRY/CATCH around a duplicate-key INSERT -> a nested BEGIN...EXCEPTION WHEN unique_violation
-- block, Postgres's equivalent of a savepoint-scoped catch: the outer function's rollback-to-here
-- state is preserved (the whole function isn't rolled back on this specific expected error), only
-- the failed INSERT is undone before the ELSE branch's fallback logic runs.
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.student_ensurelogin(AdmissionId varchar(64))
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Email" varchar(256), "StudentId" varchar(64), "Phone" varchar(32),
    "PasswordHash" varchar(512), "IsPlatform" boolean, "Status" varchar(20), "Name" varchar(200),
    "MustSetPassword" boolean, "CreatedAt" timestamptz, "PhotoUrl" text
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_norm varchar(64) := lower(trim(student_ensurelogin.AdmissionId));
    v_tenant_id uuid;
    v_admission_no varchar(64);
    v_name varchar(200);
    v_email varchar(256);
    v_phone varchar(32);
    v_status varchar(20);
    v_user_id uuid;
BEGIN
    IF v_norm IS NULL OR v_norm = '' THEN RETURN; END IF;

    SELECT s."TenantId", s."AdmissionNo", s."Name",
           NULLIF(trim(s."Email"), ''),
           left(NULLIF(trim(s."GuardianPhone"), ''), 32),
           s."Status"
    INTO v_tenant_id, v_admission_no, v_name, v_email, v_phone, v_status
    FROM "dbo"."Students" s
    WHERE lower(trim(s."AdmissionNo")) = v_norm
    LIMIT 1
    FOR UPDATE OF s;

    IF v_tenant_id IS NULL THEN RETURN; END IF;
    IF v_status IN ('inactive', 'removed', 'left', 'withdrawn') THEN RETURN; END IF;

    -- Prefer an existing student-role login. Parent rows share StudentId and must not be reused.
    SELECT u."Id" INTO v_user_id
    FROM "dbo"."Users" u
    WHERE u."StudentId" IS NOT NULL
      AND lower(trim(u."StudentId")) = lower(trim(v_admission_no))
      AND NOT EXISTS (
            SELECT 1 FROM "dbo"."UserRoles" ur
            WHERE ur."UserId" = u."Id"
              AND (ur."Role" LIKE '%parent%'
                   OR ur."Role" LIKE '%owner%'
                   OR ur."Role" LIKE '%admin%'
                   OR ur."Role" LIKE '%teacher%'
                   OR ur."Role" LIKE '%principal%'
                   OR ur."Role" = 'staff')
      )
      AND (
            EXISTS (
                SELECT 1 FROM "dbo"."UserRoles" ur
                WHERE ur."UserId" = u."Id" AND (ur."Role" = 'student' OR ur."Role" LIKE '%.student')
            )
            OR NOT EXISTS (SELECT 1 FROM "dbo"."UserRoles" ur WHERE ur."UserId" = u."Id")
      )
    ORDER BY CASE WHEN u."IsPlatform" THEN 0 ELSE 1 END, u."CreatedAt"
    LIMIT 1;

    IF v_user_id IS NULL THEN
        IF v_email IS NOT NULL AND EXISTS (
            SELECT 1 FROM "dbo"."Users" u
            WHERE u."TenantId" = v_tenant_id AND u."Email" IS NOT NULL AND lower(trim(u."Email")) = lower(v_email)
        ) THEN
            v_email := NULL;
        END IF;

        -- GuardianPhone is often already on a parent login (UX_Users_Tenant_Phone).
        IF v_phone IS NOT NULL AND EXISTS (
            SELECT 1 FROM "dbo"."Users" u
            WHERE u."TenantId" = v_tenant_id AND u."Phone" IS NOT NULL AND u."Phone" = v_phone
        ) THEN
            v_phone := NULL;
        END IF;

        v_user_id := gen_random_uuid();
        BEGIN
            INSERT INTO "dbo"."Users" ("Id", "TenantId", "Email", "Phone", "IsPlatform", "Status", "StudentId", "MustSetPassword", "Name")
            VALUES (v_user_id, v_tenant_id, v_email, v_phone, false, 'active', v_admission_no, true, v_name);
        EXCEPTION WHEN unique_violation THEN
            BEGIN
                INSERT INTO "dbo"."Users" ("Id", "TenantId", "Email", "Phone", "IsPlatform", "Status", "StudentId", "MustSetPassword", "Name")
                VALUES (v_user_id, v_tenant_id, NULL, NULL, false, 'active', v_admission_no, true, v_name);
            EXCEPTION WHEN unique_violation THEN
                RETURN;
            END;
        END;

        IF NOT EXISTS (SELECT 1 FROM "dbo"."UserRoles" WHERE "UserId" = v_user_id AND "Role" = 'student') THEN
            INSERT INTO "dbo"."UserRoles" ("UserId", "Role") VALUES (v_user_id, 'student');
        END IF;
    END IF;

    RETURN QUERY
    SELECT u."Id", u."TenantId", u."Email", u."StudentId", u."Phone",
           u."PasswordHash", u."IsPlatform", u."Status", u."Name", u."MustSetPassword", u."CreatedAt", u."PhotoUrl"
    FROM "dbo"."Users" u WHERE u."Id" = v_user_id
    LIMIT 1;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.parent_ensurelogin(AdmissionId varchar(64))
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Email" varchar(256), "StudentId" varchar(64), "Phone" varchar(32),
    "PasswordHash" varchar(512), "IsPlatform" boolean, "Status" varchar(20), "Name" varchar(200),
    "MustSetPassword" boolean, "CreatedAt" timestamptz, "PhotoUrl" text
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_norm varchar(64) := lower(trim(parent_ensurelogin.AdmissionId));
    v_tenant_id uuid;
    v_admission_no varchar(64);
    v_name varchar(200);
    v_email varchar(256);
    v_phone varchar(32);
    v_status varchar(20);
    v_user_id uuid;
    v_student_guid uuid;
BEGIN
    IF v_norm IS NULL OR v_norm = '' THEN RETURN; END IF;

    SELECT s."TenantId", s."AdmissionNo",
           NULLIF(trim(s."GuardianName"), ''),
           NULLIF(trim(s."GuardianEmail"), ''),
           left(NULLIF(trim(s."GuardianPhone"), ''), 32),
           s."Status"
    INTO v_tenant_id, v_admission_no, v_name, v_email, v_phone, v_status
    FROM "dbo"."Students" s
    WHERE lower(trim(s."AdmissionNo")) = v_norm
    LIMIT 1
    FOR UPDATE OF s;

    IF v_tenant_id IS NULL THEN RETURN; END IF;
    IF v_status IN ('inactive', 'removed', 'left', 'withdrawn') THEN RETURN; END IF;
    IF v_email IS NULL AND v_phone IS NULL THEN RETURN; END IF;

    -- Existing parent-role login for this admission.
    SELECT u."Id" INTO v_user_id
    FROM "dbo"."Users" u
    WHERE u."StudentId" IS NOT NULL
      AND lower(trim(u."StudentId")) = lower(trim(v_admission_no))
      AND EXISTS (SELECT 1 FROM "dbo"."UserRoles" ur WHERE ur."UserId" = u."Id" AND ur."Role" LIKE '%parent%')
    ORDER BY u."CreatedAt"
    LIMIT 1;

    -- Same guardian email already has a parent login (sibling wards).
    IF v_user_id IS NULL AND v_email IS NOT NULL THEN
        SELECT u."Id" INTO v_user_id
        FROM "dbo"."Users" u
        JOIN "dbo"."UserRoles" ur ON ur."UserId" = u."Id" AND ur."Role" LIKE '%parent%'
        WHERE u."TenantId" = v_tenant_id
          AND u."Email" IS NOT NULL
          AND lower(trim(u."Email")) = lower(v_email)
        ORDER BY u."CreatedAt"
        LIMIT 1;
    END IF;

    IF v_user_id IS NULL THEN
        -- Do not collide with an existing student/staff login that already owns this email.
        IF v_email IS NOT NULL AND EXISTS (
            SELECT 1 FROM "dbo"."Users" u
            WHERE u."TenantId" = v_tenant_id AND u."Email" IS NOT NULL AND lower(trim(u."Email")) = lower(v_email)
        ) THEN
            v_email := NULL;
        END IF;

        -- Guardian phone is often copied onto the student login - still create parent by email.
        IF v_phone IS NOT NULL AND EXISTS (
            SELECT 1 FROM "dbo"."Users" u
            WHERE u."TenantId" = v_tenant_id AND u."Phone" IS NOT NULL AND u."Phone" = v_phone
        ) THEN
            v_phone := NULL;
        END IF;

        IF v_email IS NULL AND v_phone IS NULL THEN RETURN; END IF;

        v_user_id := gen_random_uuid();
        BEGIN
            INSERT INTO "dbo"."Users" ("Id", "TenantId", "Email", "Phone", "IsPlatform", "Status", "StudentId", "MustSetPassword", "Name")
            VALUES (v_user_id, v_tenant_id, v_email, v_phone, false, 'active', v_admission_no, true, v_name);
        EXCEPTION WHEN unique_violation THEN
            -- Student login often already owns GuardianPhone (UX_Users_Tenant_Phone).
            -- Keep the parent row keyed by email.
            v_phone := NULL;
            IF v_email IS NULL THEN RETURN; END IF;
            BEGIN
                INSERT INTO "dbo"."Users" ("Id", "TenantId", "Email", "Phone", "IsPlatform", "Status", "StudentId", "MustSetPassword", "Name")
                VALUES (v_user_id, v_tenant_id, v_email, NULL, false, 'active', v_admission_no, true, v_name);
            EXCEPTION WHEN unique_violation THEN
                RETURN;
            END;
        END;

        IF NOT EXISTS (SELECT 1 FROM "dbo"."UserRoles" WHERE "UserId" = v_user_id AND "Role" = 'student.parent') THEN
            INSERT INTO "dbo"."UserRoles" ("UserId", "Role") VALUES (v_user_id, 'student.parent');
        END IF;
    ELSE
        IF v_email IS NOT NULL THEN
            UPDATE "dbo"."Users" SET "Email" = v_email
            WHERE "Id" = v_user_id
              AND ("Email" IS NULL OR lower(trim("Email")) <> lower(v_email))
              AND NOT EXISTS (
                    SELECT 1 FROM "dbo"."Users" x
                    WHERE x."TenantId" = v_tenant_id AND x."Id" <> v_user_id
                      AND x."Email" IS NOT NULL AND lower(trim(x."Email")) = lower(v_email)
              );
        END IF;
        IF v_phone IS NOT NULL THEN
            UPDATE "dbo"."Users" SET "Phone" = v_phone
            WHERE "Id" = v_user_id AND "Phone" IS NULL
              AND NOT EXISTS (
                    SELECT 1 FROM "dbo"."Users" x
                    WHERE x."TenantId" = v_tenant_id AND x."Id" <> v_user_id
                      AND x."Phone" IS NOT NULL AND x."Phone" = v_phone
              );
        END IF;
        IF v_name IS NOT NULL THEN
            UPDATE "dbo"."Users" SET "Name" = v_name
            WHERE "Id" = v_user_id AND ("Name" IS NULL OR trim("Name") = '');
        END IF;
        IF NOT EXISTS (SELECT 1 FROM "dbo"."UserRoles" WHERE "UserId" = v_user_id AND "Role" LIKE '%parent%') THEN
            INSERT INTO "dbo"."UserRoles" ("UserId", "Role") VALUES (v_user_id, 'student.parent');
        END IF;
    END IF;

    -- Multi-child roster. Idempotent; login must not fail if the row already exists.
    SELECT s."Id" INTO v_student_guid
    FROM "dbo"."Students" s
    WHERE s."TenantId" = v_tenant_id
      AND lower(trim(s."AdmissionNo")) = lower(trim(v_admission_no));

    IF v_student_guid IS NOT NULL THEN
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM "dbo"."ParentStudentLinks"
                WHERE "ParentUserId" = v_user_id AND "StudentId" = v_student_guid
            ) THEN
                INSERT INTO "dbo"."ParentStudentLinks" ("ParentUserId", "StudentId", "TenantId")
                VALUES (v_user_id, v_student_guid, v_tenant_id);
            END IF;
        EXCEPTION WHEN unique_violation THEN
            NULL;
        END;
    END IF;

    RETURN QUERY
    SELECT u."Id", u."TenantId", u."Email", u."StudentId", u."Phone",
           u."PasswordHash", u."IsPlatform", u."Status", u."Name", u."MustSetPassword", u."CreatedAt", u."PhotoUrl"
    FROM "dbo"."Users" u WHERE u."Id" = v_user_id
    LIMIT 1;
END;
$$;

-- ============================================================
-- StudentBus / StudentTransport
-- (StudentBus_Assign, StudentTransport_Upsert: MERGE -> INSERT ... ON CONFLICT; both keyed on
-- ("TenantId", "StudentId"), matching the existing UQ_StudentBus_Tenant_Student /
-- UQ_StudentTransportOptOut_Tenant_Student unique constraints in 05_constraints.sql.)
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.studentbus_assign(TenantId uuid, StudentId uuid, BusId uuid, StopId uuid DEFAULT NULL)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    INSERT INTO "dbo"."StudentBusAssignments" ("TenantId", "StudentId", "BusId", "StopId")
    VALUES (studentbus_assign.TenantId, studentbus_assign.StudentId, studentbus_assign.BusId, studentbus_assign.StopId)
    ON CONFLICT ("TenantId", "StudentId") DO UPDATE SET
        "BusId" = EXCLUDED."BusId", "StopId" = EXCLUDED."StopId", "CreatedAt" = now();
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.studentbus_unassign(TenantId uuid, StudentId uuid)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    DELETE FROM "dbo"."StudentBusAssignments"
    WHERE "TenantId" = studentbus_unassign.TenantId AND "StudentId" = studentbus_unassign.StudentId;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.studenttransport_optin(TenantId uuid, StudentId uuid)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    DELETE FROM "dbo"."StudentTransportOptOut"
    WHERE "TenantId" = studenttransport_optin.TenantId AND "StudentId" = studenttransport_optin.StudentId;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.studenttransport_optout(TenantId uuid, StudentId uuid)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM "dbo"."StudentTransportOptOut"
        WHERE "TenantId" = studenttransport_optout.TenantId AND "StudentId" = studenttransport_optout.StudentId
    ) THEN
        INSERT INTO "dbo"."StudentTransportOptOut" ("TenantId", "StudentId")
        VALUES (studenttransport_optout.TenantId, studenttransport_optout.StudentId);
    END IF;

    DELETE FROM "dbo"."StudentBusAssignments"
    WHERE "TenantId" = studenttransport_optout.TenantId AND "StudentId" = studenttransport_optout.StudentId;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.studenttransport_upsert(
    TenantId uuid, StudentId uuid, RouteId uuid,
    StopId uuid DEFAULT NULL, FeeHeadId uuid DEFAULT NULL, BusId uuid DEFAULT NULL
)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    INSERT INTO "dbo"."StudentBusAssignments" ("TenantId", "StudentId", "RouteId", "StopId", "FeeHeadId", "BusId")
    VALUES (studenttransport_upsert.TenantId, studenttransport_upsert.StudentId, studenttransport_upsert.RouteId,
        studenttransport_upsert.StopId, studenttransport_upsert.FeeHeadId, studenttransport_upsert.BusId)
    ON CONFLICT ("TenantId", "StudentId") DO UPDATE SET
        "RouteId" = EXCLUDED."RouteId", "StopId" = EXCLUDED."StopId",
        "FeeHeadId" = EXCLUDED."FeeHeadId", "BusId" = EXCLUDED."BusId", "CreatedAt" = now();
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;
