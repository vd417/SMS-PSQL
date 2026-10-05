-- 0019: guardian relationship ("Mother" / "Father" / "Guardian").
--
-- WHY: the teacher-app inbox shows a parent's message but could not say which guardian (mother or
-- father) is writing — the model only had a single Students."GuardianName" and a bare
-- ParentStudentLinks (no relationship column). This adds:
--   * Students."GuardianRelation"        — roster capture point (set via Student_Create/Update).
--   * ParentStudentLinks."Relationship"  — per parent↔student link, surfaced in the inbox thread.
-- dbo.parent_ensurelogin propagates the roster value onto the link when a parent login is
-- provisioned (and backfills an existing link whose relationship is still blank).
--
-- Both columns are nullable free text (NULL = unknown): there is no gender/relationship source to
-- backfill from (the legacy dbo."Parent" table is empty and uses disconnected integer ids), so the
-- value is populated going forward as schools enter it on the roster.
--
-- Rollback:
--   ALTER TABLE "dbo"."ParentStudentLinks" DROP COLUMN IF EXISTS "Relationship";
--   ALTER TABLE "dbo"."Students" DROP COLUMN IF EXISTS "GuardianRelation";
--   -- then re-create the pre-0019 student_create/student_update/parent_ensurelogin bodies.

ALTER TABLE "dbo"."Students"           ADD COLUMN IF NOT EXISTS "GuardianRelation" varchar(20);
ALTER TABLE "dbo"."ParentStudentLinks" ADD COLUMN IF NOT EXISTS "Relationship"     varchar(20);

-- student_create / student_update gain a trailing GuardianRelation argument, which changes the
-- function arity: CREATE OR REPLACE would create a second overload rather than replace, so drop
-- the exact pre-0019 signatures first.
DROP FUNCTION IF EXISTS dbo.student_create(
    uuid, varchar, varchar, varchar, varchar, varchar, int, varchar,
    varchar, varchar, varchar, int, timestamptz, varchar, varchar);
DROP FUNCTION IF EXISTS dbo.student_update(
    uuid, varchar, varchar, varchar, int, varchar, varchar, varchar, varchar, varchar,
    numeric, varchar, text, boolean, varchar, timestamptz, varchar, varchar, int);

CREATE OR REPLACE FUNCTION dbo.student_create(
    TenantId uuid, AdmissionNo varchar(64), Name varchar(200), Gender varchar(1),
    Grade varchar(20), Section varchar(20), Roll int, GuardianName varchar(200),
    GuardianPhone varchar(40), GuardianEmail varchar(256), House varchar(40), AvatarHue int,
    Dob timestamptz, Email varchar(256), Address varchar(500), GuardianRelation varchar(20) DEFAULT NULL
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
        "GuardianName", "GuardianRelation", "GuardianPhone", "GuardianEmail", "House", "AvatarHue", "Dob", "Email", "Address")
    VALUES (v_id, student_create.TenantId, v_admission_no, student_create.Name, student_create.Gender,
        student_create.Grade, student_create.Section, v_class_label, 0,
        student_create.GuardianName, NULLIF(trim(student_create.GuardianRelation), ''),
        student_create.GuardianPhone, student_create.GuardianEmail,
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
    Email varchar(256) DEFAULT NULL, Address varchar(500) DEFAULT NULL, AvatarHue int DEFAULT NULL,
    GuardianRelation varchar(20) DEFAULT NULL
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
        "GuardianRelation" = COALESCE(NULLIF(trim(student_update.GuardianRelation), ''), "GuardianRelation"),
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

-- parent_ensurelogin keeps its (varchar) signature, so CREATE OR REPLACE replaces it in place.
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
    v_relation varchar(20);
BEGIN
    IF v_norm IS NULL OR v_norm = '' THEN RETURN; END IF;

    SELECT s."TenantId", s."AdmissionNo",
           NULLIF(trim(s."GuardianName"), ''),
           NULLIF(trim(s."GuardianEmail"), ''),
           left(NULLIF(trim(s."GuardianPhone"), ''), 32),
           s."Status",
           NULLIF(trim(s."GuardianRelation"), '')
    INTO v_tenant_id, v_admission_no, v_name, v_email, v_phone, v_status, v_relation
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
                INSERT INTO "dbo"."ParentStudentLinks" ("ParentUserId", "StudentId", "TenantId", "Relationship")
                VALUES (v_user_id, v_student_guid, v_tenant_id, v_relation);
            ELSIF v_relation IS NOT NULL THEN
                -- Keep an existing link's relationship fresh when the roster later fills it in.
                UPDATE "dbo"."ParentStudentLinks"
                SET "Relationship" = v_relation
                WHERE "ParentUserId" = v_user_id AND "StudentId" = v_student_guid
                  AND ("Relationship" IS NULL OR trim("Relationship") = '');
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
