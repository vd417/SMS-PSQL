-- 0013: wire dbo.contact_claims_sync into the student/teacher/staff create+update procs so
-- per-tenant email/phone uniqueness is enforced authoritatively in PostgreSQL (B5). Each proc
-- PERFORMs one sync of the owner's OWN email/phone at the end of its transaction; a different-
-- person conflict raises SQLSTATE 'SMSDC' ('contact_conflict:<kind>'), rolling the write back.
-- Guardian email/phone are never claimed; students have no own phone column (phone => NULL).
-- Additive: CREATE OR REPLACE of six existing functions, mirroring db/postgres/11_sis_procs.sql
-- and db/postgres/13_staffing_procs.sql (fresh-DB baseline). Nothing else changes; the existing
-- Users unique indexes and the provisioning procs' unique_violation handling are untouched.
-- Rollback: re-run the previous bodies of these six functions (without the PERFORM line).

-- ===== SIS (db/postgres/11_sis_procs.sql) =====

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

    -- B5: claim the student's OWN email for per-tenant contact uniqueness (students have no own
    -- phone column; guardian email/phone are never claimed). Raises SMSDC on a different-person
    -- conflict, rolling back this create.
    PERFORM dbo.contact_claims_sync(student_create.TenantId, 'student', v_id::text, NULL,
                                    student_create.Email, NULL);

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

        -- B5: re-claim the student's OWN (post-update) email for per-tenant contact uniqueness.
        -- Guardian fields are never claimed. Raises SMSDC on a different-person conflict.
        PERFORM dbo.contact_claims_sync(v_tenant_id, 'student', student_update.Id::text, NULL,
            (SELECT s."Email" FROM "dbo"."Students" s WHERE s."Id" = student_update.Id), NULL);
    END IF;

    RETURN QUERY
    SELECT s."Id", s."TenantId", s."AdmissionNo", s."Name", s."Gender", s."Grade", s."Section", s."ClassLabel", s."Roll",
           s."GuardianName", s."GuardianPhone", s."GuardianEmail", s."AttendancePct", s."FeeStatus", s."FeeDue",
           s."Status", s."House", s."AvatarHue", s."Dob", s."Email", s."Address", s."PhotoUrl"
    FROM "dbo"."Students" s WHERE s."Id" = student_update.Id;
END;
$$;

-- ===== Staffing (db/postgres/13_staffing_procs.sql) =====

CREATE OR REPLACE FUNCTION dbo.staff_create(
    TenantId uuid, Name varchar(200), Gender varchar(1), Role varchar(80),
    Category varchar(40), Department varchar(80), Phone varchar(40), Shift varchar(40),
    Route varchar(80), AvatarHue int, EmployeeCode varchar(64) DEFAULT NULL, Email varchar(256) DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Name" varchar(200), "Gender" varchar(1), "Role" varchar(80), "Category" varchar(40),
    "Department" varchar(80), "Phone" varchar(40), "Shift" varchar(40), "Route" varchar(80),
    "AttendancePct" numeric(5,2), "Status" varchar(20), "AvatarHue" int, "EmployeeCode" varchar(64), "Email" varchar(256),
    "PhotoUrl" varchar(512)
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_id uuid := gen_random_uuid();
    v_code varchar(64) := staff_create.EmployeeCode;
    v_slug varchar(48);
    v_prefix varchar(80);
    v_next int;
BEGIN
    IF v_code IS NULL OR trim(v_code) = '' THEN
        SELECT lower("Slug") INTO v_slug FROM "dbo"."Tenants" WHERE "Id" = staff_create.TenantId;
        IF v_slug IS NULL OR v_slug = '' THEN v_slug := 'sch'; END IF;

        v_prefix := v_slug || '-STF-';
        SELECT COALESCE(max((substring("EmployeeCode" FROM length(v_prefix) + 1))::int), 0) + 1
        INTO v_next
        FROM "dbo"."Staff"
        WHERE "TenantId" = staff_create.TenantId
          AND "EmployeeCode" LIKE v_prefix || '%'
          AND substring("EmployeeCode" FROM length(v_prefix) + 1) ~ '^\d+$';

        v_code := v_prefix || lpad(v_next::text, 4, '0');
    ELSE
        v_code := lower(trim(v_code));
    END IF;

    INSERT INTO "dbo"."Staff"
        ("Id", "TenantId", "Name", "Gender", "Role", "Category", "Department", "Phone", "Shift", "Route",
         "AvatarHue", "EmployeeCode", "Email")
    VALUES (v_id, staff_create.TenantId, staff_create.Name, staff_create.Gender, staff_create.Role,
            staff_create.Category, staff_create.Department, staff_create.Phone, staff_create.Shift,
            staff_create.Route, COALESCE(staff_create.AvatarHue, 0), v_code, staff_create.Email);

    UPDATE "dbo"."Tenants" SET "StaffCount" = (
        (SELECT count(*) FROM "dbo"."Teachers" te WHERE te."TenantId" = staff_create.TenantId AND te."Status" = 'active')
      + (SELECT count(*) FROM "dbo"."Staff" st WHERE st."TenantId" = staff_create.TenantId AND st."Status" = 'active')
    ) WHERE "Id" = staff_create.TenantId;

    -- B5: claim this staff member's own email+phone for per-tenant contact uniqueness.
    -- Raises SMSDC on a different-person conflict, rolling back this create.
    PERFORM dbo.contact_claims_sync(staff_create.TenantId, 'staff', v_id::text, NULL,
                                    staff_create.Email, staff_create.Phone);

    RETURN QUERY
    SELECT "Id", "TenantId", "Name", "Gender", "Role", "Category", "Department", "Phone", "Shift", "Route",
           "AttendancePct", "Status", "AvatarHue", "EmployeeCode", "Email", CAST(NULL AS varchar(512))
    FROM "dbo"."Staff" WHERE "Id" = v_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.staff_update(
    Id uuid, Name varchar(200), Role varchar(80), Category varchar(40),
    Department varchar(80), Phone varchar(40), Shift varchar(40), Route varchar(80), Status varchar(20),
    Email varchar(256) DEFAULT NULL, Gender varchar(1) DEFAULT NULL, EmployeeCode varchar(64) DEFAULT NULL
)
RETURNS int
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_tenant_id uuid; v_count int;
BEGIN
    UPDATE "dbo"."Staff" SET
        "Name" = COALESCE(staff_update.Name, "Name"), "Role" = COALESCE(staff_update.Role, "Role"),
        "Category" = COALESCE(staff_update.Category, "Category"),
        "Department" = COALESCE(staff_update.Department, "Department"), "Phone" = COALESCE(staff_update.Phone, "Phone"),
        "Shift" = COALESCE(staff_update.Shift, "Shift"), "Route" = COALESCE(staff_update.Route, "Route"),
        "Status" = COALESCE(staff_update.Status, "Status"), "Email" = COALESCE(staff_update.Email, "Email"),
        "Gender" = COALESCE(staff_update.Gender, "Gender"),
        "EmployeeCode" = COALESCE(staff_update.EmployeeCode, "EmployeeCode")
    WHERE "Id" = staff_update.Id;
    GET DIAGNOSTICS v_count = ROW_COUNT;

    SELECT "TenantId" INTO v_tenant_id FROM "dbo"."Staff" WHERE "Id" = staff_update.Id;
    IF v_tenant_id IS NOT NULL THEN
        UPDATE "dbo"."Tenants" SET "StaffCount" = (
            (SELECT count(*) FROM "dbo"."Teachers" te WHERE te."TenantId" = v_tenant_id AND te."Status" = 'active')
          + (SELECT count(*) FROM "dbo"."Staff" st WHERE st."TenantId" = v_tenant_id AND st."Status" = 'active')
        ) WHERE "Id" = v_tenant_id;

        -- B5: re-claim this staff member's own (post-update) email+phone for contact uniqueness.
        -- Raises SMSDC on a different-person conflict, rolling back this update.
        PERFORM dbo.contact_claims_sync(v_tenant_id, 'staff', staff_update.Id::text, NULL,
            (SELECT st."Email" FROM "dbo"."Staff" st WHERE st."Id" = staff_update.Id),
            (SELECT st."Phone" FROM "dbo"."Staff" st WHERE st."Id" = staff_update.Id));
    END IF;

    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.teacher_create(
    TenantId uuid, Name varchar(200), Gender varchar(1), Department varchar(80),
    Designation varchar(80), SubjectsCsv varchar(400), ClassTeacher varchar(40),
    Phone varchar(40), Email varchar(256), Exp int, Rating numeric(4,2), Result numeric(5,2),
    Load int, AvatarHue int, Top boolean, EmployeeCode varchar(64) DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Name" varchar(200), "Gender" varchar(1), "Department" varchar(80),
    "Designation" varchar(80), "SubjectsCsv" varchar(400), "ClassTeacher" varchar(40), "Phone" varchar(40),
    "Email" varchar(256), "Exp" int, "Rating" numeric(4,2), "AttendancePct" numeric(5,2), "Result" numeric(5,2),
    "Load" int, "Status" varchar(20), "AvatarHue" int, "Top" boolean, "EmployeeCode" varchar(64),
    "PhotoUrl" varchar(512), "UserId" uuid
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_id uuid := gen_random_uuid();
    v_code varchar(64) := teacher_create.EmployeeCode;
    v_slug varchar(48);
    v_prefix varchar(80);
    v_next int;
BEGIN
    IF v_code IS NULL OR trim(v_code) = '' THEN
        SELECT lower("Slug") INTO v_slug FROM "dbo"."Tenants" WHERE "Id" = teacher_create.TenantId;
        IF v_slug IS NULL OR v_slug = '' THEN v_slug := 'sch'; END IF;

        v_prefix := v_slug || '-TCH-';
        SELECT COALESCE(max((substring("EmployeeCode" FROM length(v_prefix) + 1))::int), 0) + 1
        INTO v_next
        FROM "dbo"."Teachers"
        WHERE "TenantId" = teacher_create.TenantId
          AND "EmployeeCode" LIKE v_prefix || '%'
          AND substring("EmployeeCode" FROM length(v_prefix) + 1) ~ '^\d+$';

        v_code := v_prefix || lpad(v_next::text, 4, '0');
    ELSE
        v_code := lower(trim(v_code));
    END IF;

    INSERT INTO "dbo"."Teachers"
        ("Id", "TenantId", "Name", "Gender", "Department", "Designation", "SubjectsCsv", "ClassTeacher",
         "Phone", "Email", "Exp", "Rating", "Result", "Load", "AvatarHue", "Top", "EmployeeCode")
    VALUES (v_id, teacher_create.TenantId, teacher_create.Name, teacher_create.Gender, teacher_create.Department,
            teacher_create.Designation, teacher_create.SubjectsCsv, teacher_create.ClassTeacher,
            teacher_create.Phone, teacher_create.Email, COALESCE(teacher_create.Exp, 0),
            COALESCE(teacher_create.Rating, 0), COALESCE(teacher_create.Result, 0), COALESCE(teacher_create.Load, 0),
            COALESCE(teacher_create.AvatarHue, 0), COALESCE(teacher_create.Top, false), v_code);

    UPDATE "dbo"."Tenants" SET "StaffCount" = (
        (SELECT count(*) FROM "dbo"."Teachers" te WHERE te."TenantId" = teacher_create.TenantId AND te."Status" = 'active')
      + (SELECT count(*) FROM "dbo"."Staff" st WHERE st."TenantId" = teacher_create.TenantId AND st."Status" = 'active')
    ) WHERE "Id" = teacher_create.TenantId;

    -- B5: claim this teacher's own email+phone for per-tenant contact uniqueness.
    -- Raises SMSDC on a different-person conflict, rolling back this create.
    PERFORM dbo.contact_claims_sync(teacher_create.TenantId, 'teacher', v_id::text, NULL,
                                    teacher_create.Email, teacher_create.Phone);

    RETURN QUERY
    SELECT "Id", "TenantId", "Name", "Gender", "Department", "Designation", "SubjectsCsv", "ClassTeacher", "Phone", "Email",
           "Exp", "Rating", "AttendancePct", "Result", "Load", "Status", "AvatarHue", "Top", "EmployeeCode",
           CAST(NULL AS varchar(512)), "UserId"
    FROM "dbo"."Teachers" WHERE "Id" = v_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.teacher_update(
    Id uuid, Name varchar(200), Department varchar(80), Designation varchar(80),
    SubjectsCsv varchar(400), ClassTeacher varchar(40), Phone varchar(40), Email varchar(256),
    Status varchar(20), Gender varchar(1) DEFAULT NULL, Exp int DEFAULT NULL, EmployeeCode varchar(64) DEFAULT NULL
)
RETURNS int
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_tenant_id uuid; v_count int;
BEGIN
    UPDATE "dbo"."Teachers" SET
        "Name" = COALESCE(teacher_update.Name, "Name"), "Department" = COALESCE(teacher_update.Department, "Department"),
        "Designation" = COALESCE(teacher_update.Designation, "Designation"),
        "SubjectsCsv" = COALESCE(teacher_update.SubjectsCsv, "SubjectsCsv"),
        "ClassTeacher" = COALESCE(teacher_update.ClassTeacher, "ClassTeacher"),
        "Phone" = COALESCE(teacher_update.Phone, "Phone"), "Email" = COALESCE(teacher_update.Email, "Email"),
        "Status" = COALESCE(teacher_update.Status, "Status"), "Gender" = COALESCE(teacher_update.Gender, "Gender"),
        "Exp" = COALESCE(teacher_update.Exp, "Exp"),
        "EmployeeCode" = COALESCE(teacher_update.EmployeeCode, "EmployeeCode")
    WHERE "Id" = teacher_update.Id;
    GET DIAGNOSTICS v_count = ROW_COUNT;

    SELECT "TenantId" INTO v_tenant_id FROM "dbo"."Teachers" WHERE "Id" = teacher_update.Id;
    IF v_tenant_id IS NOT NULL THEN
        UPDATE "dbo"."Tenants" SET "StaffCount" = (
            (SELECT count(*) FROM "dbo"."Teachers" te WHERE te."TenantId" = v_tenant_id AND te."Status" = 'active')
          + (SELECT count(*) FROM "dbo"."Staff" st WHERE st."TenantId" = v_tenant_id AND st."Status" = 'active')
        ) WHERE "Id" = v_tenant_id;

        -- B5: re-claim this teacher's own (post-update) email+phone for contact uniqueness.
        -- Raises SMSDC on a different-person conflict, rolling back this update.
        PERFORM dbo.contact_claims_sync(v_tenant_id, 'teacher', teacher_update.Id::text, NULL,
            (SELECT te."Email" FROM "dbo"."Teachers" te WHERE te."Id" = teacher_update.Id),
            (SELECT te."Phone" FROM "dbo"."Teachers" te WHERE te."Id" = teacher_update.Id));
    END IF;

    RETURN v_count;
END;
$$;

