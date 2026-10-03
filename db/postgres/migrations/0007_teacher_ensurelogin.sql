-- 0007: teacher self-serve login. Onboarding a teacher (POST /v1/teachers) only writes
-- dbo.Teachers — no Users row — and Send invite is reserved for CRM roles
-- (owner/admin/principal/vice_principal), so a newly onboarded teacher's
-- "First time / forgot password" hit 404 "Email is not registered.". Mirrors
-- dbo.staff_ensurelogin (13_staffing_procs.sql): resolve dbo.Teachers by email, link or
-- create the tenant login with role school.teacher, and set Teachers.UserId.
-- Additive only: one new function + its grant.
-- Rollback: DROP FUNCTION dbo.teacher_ensurelogin(varchar);

CREATE OR REPLACE FUNCTION dbo.teacher_ensurelogin(Email varchar(256))
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Email" varchar(256), "StudentId" varchar(64), "Phone" varchar(32),
    "PasswordHash" varchar(512), "IsPlatform" boolean, "Status" varchar(20), "Name" varchar(200),
    "MustSetPassword" boolean, "CreatedAt" timestamptz, "PhotoUrl" text
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_norm varchar(256) := lower(trim(teacher_ensurelogin.Email));
    v_teacher_id uuid;
    v_tenant_id uuid;
    v_name varchar(200);
    v_status varchar(20);
    v_existing_user_id uuid;
    v_user_id uuid;
BEGIN
    IF v_norm IS NULL OR v_norm = '' THEN RETURN; END IF;

    SELECT t."Id", t."TenantId", t."Name", t."Status", t."UserId"
    INTO v_teacher_id, v_tenant_id, v_name, v_status, v_existing_user_id
    FROM "dbo"."Teachers" t
    WHERE t."Email" IS NOT NULL AND lower(trim(t."Email")) = v_norm
    ORDER BY t."CreatedAt"
    LIMIT 1
    FOR UPDATE OF t;

    IF v_teacher_id IS NULL THEN RETURN; END IF;
    IF v_status = 'inactive' THEN RETURN; END IF;

    IF v_existing_user_id IS NOT NULL THEN
        v_user_id := v_existing_user_id;
    ELSE
        -- The email may already belong to a login in this tenant (e.g. a CRM account) - link
        -- that one instead of creating a duplicate.
        SELECT u."Id" INTO v_user_id
        FROM "dbo"."Users" u
        WHERE u."TenantId" = v_tenant_id AND u."Email" IS NOT NULL AND lower(trim(u."Email")) = v_norm
        ORDER BY u."CreatedAt"
        LIMIT 1;

        IF v_user_id IS NULL THEN
            v_user_id := gen_random_uuid();
            INSERT INTO "dbo"."Users" ("Id", "TenantId", "Email", "Phone", "IsPlatform", "Status", "StudentId", "MustSetPassword", "Name")
            VALUES (v_user_id, v_tenant_id, v_norm, NULL, false, 'active', NULL, true, v_name);
        END IF;

        IF NOT EXISTS (SELECT 1 FROM "dbo"."UserRoles" WHERE "UserId" = v_user_id AND "Role" = 'school.teacher') THEN
            INSERT INTO "dbo"."UserRoles" ("UserId", "Role") VALUES (v_user_id, 'school.teacher');
        END IF;

        UPDATE "dbo"."Teachers" SET "UserId" = v_user_id WHERE "Id" = v_teacher_id;
    END IF;

    RETURN QUERY
    SELECT u."Id", u."TenantId", u."Email", u."StudentId", u."Phone",
           u."PasswordHash", u."IsPlatform", u."Status", u."Name", u."MustSetPassword", u."CreatedAt", u."PhotoUrl"
    FROM "dbo"."Users" u WHERE u."Id" = v_user_id
    LIMIT 1;
END;
$$;

GRANT EXECUTE ON FUNCTION dbo.teacher_ensurelogin(varchar) TO sms_app;
