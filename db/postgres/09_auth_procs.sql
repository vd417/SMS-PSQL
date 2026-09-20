-- Auth/User module: PL/pgSQL conversions of the 31 remaining stored procedures in the
-- Invitations/Otp/PlatformAdmin/RefreshToken/RoleTemplate/User/UserPermissions/UserRoles/Users
-- family (dbo.User_Create was already converted in 08_sample_procedure_conversions.sql).
-- Source: OBJECT_DEFINITION() extracted read-only from the live SQL Server Sms database on
-- 2026-09-21, cross-checked against sqlserver-object-inventory.csv.
--
-- Naming convention: function names and parameter names are the exact original SQL Server
-- identifiers, unquoted -- Postgres folds unquoted identifiers to lowercase, and so does every
-- existing BaseRepository call site (e.g. "dbo.User_GetById" / "TenantId" => @TenantId), so this
-- requires zero call-site string changes. Table/column identifiers stay quoted+exact-case (as
-- created in 04_tables.sql), which is a distinct identifier namespace from the unquoted
-- lowercase-folded parameters, so there is no ambiguity between e.g. "TenantId" (column) and
-- tenantid (parameter) inside a function body.

-- ============================================================
-- Invitations
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.invitations_create(
    TenantId uuid, UserId uuid, Email varchar(256), Phone varchar(32),
    RoleLabel varchar(64), InvitedByUserId uuid, ExpiresAt timestamptz
) RETURNS uuid
LANGUAGE sql
AS $$
    INSERT INTO "dbo"."Invitations"
        ("Id", "TenantId", "UserId", "Email", "Phone", "RoleLabel", "InvitedByUserId", "ExpiresAt")
    VALUES (gen_random_uuid(), TenantId, UserId, Email, Phone, RoleLabel, InvitedByUserId, ExpiresAt)
    RETURNING "Id";
$$;

CREATE OR REPLACE FUNCTION dbo.invitations_getbyid(TenantId uuid, Id uuid)
RETURNS TABLE (
    "Id" uuid, "UserId" uuid, "Email" varchar(256), "Phone" varchar(32), "RoleLabel" varchar(64),
    "InvitedAt" timestamptz, "ExpiresAt" timestamptz, "AcceptedAt" timestamptz,
    "RevokedAt" timestamptz, "LastResentAt" timestamptz
)
LANGUAGE sql
AS $$
    SELECT i."Id", i."UserId", i."Email", i."Phone", i."RoleLabel", i."InvitedAt", i."ExpiresAt",
           i."AcceptedAt", i."RevokedAt", i."LastResentAt"
    FROM "dbo"."Invitations" i
    WHERE i."TenantId" = invitations_getbyid.TenantId AND i."Id" = invitations_getbyid.Id;
$$;

CREATE OR REPLACE FUNCTION dbo.invitations_listbytenant(TenantId uuid)
RETURNS TABLE (
    "Id" uuid, "UserId" uuid, "Email" varchar(256), "Phone" varchar(32), "RoleLabel" varchar(64),
    "InvitedAt" timestamptz, "ExpiresAt" timestamptz, "AcceptedAt" timestamptz,
    "RevokedAt" timestamptz, "LastResentAt" timestamptz
)
LANGUAGE sql
AS $$
    SELECT i."Id", i."UserId", i."Email", i."Phone", i."RoleLabel", i."InvitedAt", i."ExpiresAt",
           i."AcceptedAt", i."RevokedAt", i."LastResentAt"
    FROM "dbo"."Invitations" i
    WHERE i."TenantId" = invitations_listbytenant.TenantId
    ORDER BY i."InvitedAt" DESC;
$$;

CREATE OR REPLACE FUNCTION dbo.invitations_markacceptedbyuserid(UserId uuid)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    UPDATE "dbo"."Invitations"
    SET "AcceptedAt" = now()
    WHERE "UserId" = invitations_markacceptedbyuserid.UserId
      AND "AcceptedAt" IS NULL AND "RevokedAt" IS NULL;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.invitations_markresent(Id uuid, ExpiresAt timestamptz)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    UPDATE "dbo"."Invitations"
    SET "ExpiresAt" = invitations_markresent.ExpiresAt, "LastResentAt" = now()
    WHERE "Id" = invitations_markresent.Id;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.invitations_markrevoked(Id uuid)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    UPDATE "dbo"."Invitations" SET "RevokedAt" = now() WHERE "Id" = invitations_markrevoked.Id;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

-- ============================================================
-- Otp
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.otp_consume(Identifier varchar(256), CodeHash varchar(128))
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    UPDATE "dbo"."OtpCodes" SET "ConsumedAt" = now()
    WHERE "Identifier" = otp_consume.Identifier AND "CodeHash" = otp_consume.CodeHash
      AND "ConsumedAt" IS NULL;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.otp_consumeallforidentifier(Identifier varchar(256))
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    UPDATE "dbo"."OtpCodes" SET "ConsumedAt" = now()
    WHERE "Identifier" = otp_consumeallforidentifier.Identifier AND "ConsumedAt" IS NULL;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.otp_getactive(Identifier varchar(256))
RETURNS TABLE ("Id" uuid, "CodeHash" varchar(128))
LANGUAGE sql
AS $$
    SELECT o."Id", o."CodeHash"
    FROM "dbo"."OtpCodes" o
    WHERE o."Identifier" = otp_getactive.Identifier AND o."ConsumedAt" IS NULL
      AND o."ExpiresAt" > now()
    ORDER BY o."CreatedAt" DESC
    LIMIT 1;
$$;

CREATE OR REPLACE FUNCTION dbo.otp_insert(
    Identifier varchar(256), Channel varchar(10), CodeHash varchar(128), ExpiresAt timestamptz
) RETURNS int
LANGUAGE sql
AS $$
    INSERT INTO "dbo"."OtpCodes" ("Identifier", "Channel", "CodeHash", "ExpiresAt")
    VALUES (Identifier, Channel, CodeHash, ExpiresAt);
    SELECT 1;
$$;

-- ============================================================
-- PlatformAdmin
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.platformadmin_exists()
RETURNS int
LANGUAGE sql
AS $$
    SELECT CASE WHEN EXISTS (
        SELECT 1 FROM "dbo"."Users" WHERE "IsPlatform" = true AND "Status" = 'active'
    ) THEN 1 ELSE 0 END;
$$;

-- ============================================================
-- RefreshToken
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.refreshtoken_getactive(TokenHash varchar(128))
RETURNS TABLE ("UserId" uuid)
LANGUAGE sql
AS $$
    SELECT rt."UserId"
    FROM "dbo"."RefreshTokens" rt
    WHERE rt."TokenHash" = refreshtoken_getactive.TokenHash
      AND rt."RevokedAt" IS NULL
      AND rt."ExpiresAt" > now();
$$;

CREATE OR REPLACE FUNCTION dbo.refreshtoken_insert(UserId uuid, TokenHash varchar(128), ExpiresAt timestamptz)
RETURNS int
LANGUAGE sql
AS $$
    INSERT INTO "dbo"."RefreshTokens" ("UserId", "TokenHash", "ExpiresAt")
    VALUES (UserId, TokenHash, ExpiresAt);
    SELECT 1;
$$;

CREATE OR REPLACE FUNCTION dbo.refreshtoken_revoke(TokenHash varchar(128))
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    UPDATE "dbo"."RefreshTokens" SET "RevokedAt" = now()
    WHERE "TokenHash" = refreshtoken_revoke.TokenHash AND "RevokedAt" IS NULL;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

-- ============================================================
-- RoleTemplate (OPENJSON -> jsonb_to_recordset)
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.roletemplate_get(TenantId uuid)
RETURNS TABLE ("Role" varchar(32), "Module" varchar(64), "Cap" varchar(1), "Effect" varchar(8))
LANGUAGE sql
AS $$
    SELECT rt."Role", rt."Module", rt."Cap", rt."Effect"
    FROM "dbo"."RoleTemplateOverrides" rt
    WHERE rt."TenantId" = roletemplate_get.TenantId
    ORDER BY rt."Role", rt."Module", rt."Cap";
$$;

-- "json" must be quoted here and below: it's a reserved word in this Postgres version's SQL/JSON
-- grammar (JSON_TABLE / IS JSON), so it can't appear bare as a parameter name or call-site
-- argument label — BaseRepository.FunctionCallSql always quotes+lowercases the argument label, so
-- this must match that exact quoted-lowercase spelling.
CREATE OR REPLACE FUNCTION dbo.roletemplate_set(TenantId uuid, "json" text)
RETURNS int
LANGUAGE plpgsql
AS $$
BEGIN
    DELETE FROM "dbo"."RoleTemplateOverrides" WHERE "TenantId" = roletemplate_set.TenantId;

    IF "json" IS NULL OR trim("json") IN ('', '[]') THEN
        RETURN 0;
    END IF;

    INSERT INTO "dbo"."RoleTemplateOverrides" ("TenantId", "Role", "Module", "Cap", "Effect")
    SELECT roletemplate_set.TenantId, j.role, j.module, j.cap, j.effect
    FROM jsonb_to_recordset("json"::jsonb) AS j(
        role varchar(32), module varchar(64), cap varchar(1), effect varchar(8))
    WHERE j.role IN ('admin', 'principal', 'vice_principal', 'teacher', 'staff')
      AND j.module IS NOT NULL
      AND j.cap IN ('V', 'E', 'A')
      AND j.effect IN ('grant', 'revoke');
    RETURN 1;
END;
$$;

-- ============================================================
-- User (dbo.User_Create already converted — see 08_sample_procedure_conversions.sql)
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.user_getbyemail(Email varchar(256))
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Email" varchar(256), "StudentId" varchar(64), "Phone" varchar(32),
    "PasswordHash" varchar(512), "IsPlatform" boolean, "Status" varchar(20), "Name" varchar(200),
    "MustSetPassword" boolean, "CreatedAt" timestamptz, "PhotoUrl" text
)
LANGUAGE sql
AS $$
    SELECT u."Id", u."TenantId", u."Email", u."StudentId", u."Phone", u."PasswordHash",
           u."IsPlatform", u."Status", u."Name", u."MustSetPassword", u."CreatedAt", u."PhotoUrl"
    FROM "dbo"."Users" u
    WHERE u."Email" = user_getbyemail.Email
    ORDER BY CASE WHEN u."IsPlatform" THEN 0 ELSE 1 END, u."CreatedAt"
    LIMIT 1;
$$;

CREATE OR REPLACE FUNCTION dbo.user_getbyid(Id uuid)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Email" varchar(256), "StudentId" varchar(64), "Phone" varchar(32),
    "PasswordHash" varchar(512), "IsPlatform" boolean, "Status" varchar(20), "Name" varchar(200),
    "MustSetPassword" boolean, "CreatedAt" timestamptz, "PhotoUrl" text
)
LANGUAGE sql
AS $$
    SELECT u."Id", u."TenantId", u."Email", u."StudentId", u."Phone", u."PasswordHash",
           u."IsPlatform", u."Status", u."Name", u."MustSetPassword", u."CreatedAt", u."PhotoUrl"
    FROM "dbo"."Users" u
    WHERE u."Id" = user_getbyid.Id
    LIMIT 1;
$$;

CREATE OR REPLACE FUNCTION dbo.user_getbyphone(Phone varchar(32))
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Email" varchar(256), "StudentId" varchar(64), "Phone" varchar(32),
    "PasswordHash" varchar(512), "IsPlatform" boolean, "Status" varchar(20), "Name" varchar(200),
    "MustSetPassword" boolean, "CreatedAt" timestamptz, "PhotoUrl" text
)
LANGUAGE sql
AS $$
    SELECT u."Id", u."TenantId", u."Email", u."StudentId", u."Phone", u."PasswordHash",
           u."IsPlatform", u."Status", u."Name", u."MustSetPassword", u."CreatedAt", u."PhotoUrl"
    FROM "dbo"."Users" u
    WHERE u."Phone" = user_getbyphone.Phone
    ORDER BY CASE WHEN u."IsPlatform" THEN 0 ELSE 1 END, u."CreatedAt"
    LIMIT 1;
$$;

CREATE OR REPLACE FUNCTION dbo.user_getbystudentid(StudentId varchar(64), TenantId uuid)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Email" varchar(256), "StudentId" varchar(64), "Phone" varchar(32),
    "PasswordHash" varchar(512), "IsPlatform" boolean, "Status" varchar(20)
)
LANGUAGE sql
AS $$
    SELECT u."Id", u."TenantId", u."Email", u."StudentId", u."Phone", u."PasswordHash",
           u."IsPlatform", u."Status"
    FROM "dbo"."Users" u
    WHERE u."StudentId" = user_getbystudentid.StudentId AND u."TenantId" = user_getbystudentid.TenantId;
$$;

CREATE OR REPLACE FUNCTION dbo.user_listbyadmissionid(AdmissionId varchar(64))
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Email" varchar(256), "StudentId" varchar(64), "Phone" varchar(32),
    "PasswordHash" varchar(512), "IsPlatform" boolean, "Status" varchar(20), "Name" varchar(200),
    "MustSetPassword" boolean, "CreatedAt" timestamptz, "PhotoUrl" text
)
LANGUAGE sql
AS $$
    SELECT u."Id", u."TenantId", u."Email", u."StudentId", u."Phone", u."PasswordHash",
           u."IsPlatform", u."Status", u."Name", u."MustSetPassword", u."CreatedAt", u."PhotoUrl"
    FROM "dbo"."Users" u
    WHERE u."StudentId" IS NOT NULL
      AND lower(trim(u."StudentId")) = lower(trim(user_listbyadmissionid.AdmissionId))
    ORDER BY CASE WHEN u."IsPlatform" THEN 0 ELSE 1 END, u."CreatedAt";
$$;

CREATE OR REPLACE FUNCTION dbo.user_setemail(UserId uuid, Email varchar(256) DEFAULT NULL)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    UPDATE "dbo"."Users" SET "Email" = user_setemail.Email WHERE "Id" = user_setemail.UserId;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.user_setpassword(UserId uuid, PasswordHash varchar(512))
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    UPDATE "dbo"."Users"
    SET "PasswordHash" = user_setpassword.PasswordHash, "Status" = 'active', "MustSetPassword" = false
    WHERE "Id" = user_setpassword.UserId;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.user_setphoto(UserId uuid, PhotoUrl text DEFAULT NULL)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    UPDATE "dbo"."Users" SET "PhotoUrl" = user_setphoto.PhotoUrl WHERE "Id" = user_setphoto.UserId;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.user_setstatus(UserId uuid, Status varchar(20))
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    UPDATE "dbo"."Users" SET "Status" = user_setstatus.Status WHERE "Id" = user_setstatus.UserId;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

-- ============================================================
-- UserPermissions (OPENJSON -> jsonb_to_recordset)
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.userpermissions_get(UserId uuid)
RETURNS TABLE ("Module" varchar(64), "Cap" varchar(1), "Effect" varchar(16))
LANGUAGE sql
AS $$
    SELECT up."Module", up."Cap", up."Effect"
    FROM "dbo"."UserPermissions" up
    WHERE up."UserId" = userpermissions_get.UserId
    ORDER BY up."Module", up."Cap";
$$;

CREATE OR REPLACE FUNCTION dbo.userpermissions_set(UserId uuid, "json" text)
RETURNS int
LANGUAGE plpgsql
AS $$
BEGIN
    DELETE FROM "dbo"."UserPermissions" WHERE "UserId" = userpermissions_set.UserId;

    IF "json" IS NULL OR trim("json") IN ('', '[]') THEN
        RETURN 0;
    END IF;

    INSERT INTO "dbo"."UserPermissions" ("UserId", "Module", "Cap", "Effect")
    SELECT userpermissions_set.UserId, j.module, j.cap, j.effect
    FROM jsonb_to_recordset("json"::jsonb) AS j(
        module varchar(64), cap varchar(1), effect varchar(16))
    WHERE j.module IS NOT NULL
      AND j.cap IN ('V', 'E', 'A')
      AND j.effect IN ('grant', 'revoke');
    RETURN 1;
END;
$$;

-- ============================================================
-- UserRole(s)
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.userrole_add(UserId uuid, Role varchar(64))
RETURNS int
LANGUAGE plpgsql
AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM "dbo"."UserRoles"
        WHERE "UserId" = userrole_add.UserId AND "Role" = userrole_add.Role
    ) THEN
        INSERT INTO "dbo"."UserRoles" ("UserId", "Role") VALUES (userrole_add.UserId, userrole_add.Role);
        RETURN 1;
    END IF;
    RETURN 0;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.userroles_getbyuser(UserId uuid)
RETURNS TABLE ("Role" varchar(64))
LANGUAGE sql
AS $$
    SELECT ur."Role"
    FROM "dbo"."UserRoles" ur
    WHERE ur."UserId" = userroles_getbyuser.UserId
    ORDER BY ur."Role";
$$;

CREATE OR REPLACE FUNCTION dbo.userroles_replace(UserId uuid, Roles text)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    DELETE FROM "dbo"."UserRoles" WHERE "UserId" = userroles_replace.UserId;

    INSERT INTO "dbo"."UserRoles" ("UserId", "Role")
    SELECT DISTINCT userroles_replace.UserId, trim(value)
    FROM unnest(string_to_array(userroles_replace.Roles, ',')) AS value
    WHERE trim(value) <> '';
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

-- ============================================================
-- Users (TVP -> jsonb; requires the DAO change made alongside this file — see
-- UserProvisioningDao.cs / UserProvisioningRepository.cs BulkCreateAsync)
-- ============================================================

-- Data-modifying CTEs are always materialized (computed exactly once), so "eligible" below is
-- guaranteed to produce the same generated ids for both the Users insert and the UserRoles insert
-- -- no temp table needed, and no transaction-lifetime cleanup to worry about across repeated
-- calls on a pooled connection.
-- Rows is `text`, not `jsonb`: Postgres function-overload resolution won't implicitly cast a
-- text-typed bind parameter (what Dapper/Npgsql sends for a C# string) to jsonb, so the parameter
-- stays text and is cast explicitly in the body (same reasoning as roletemplate_set/
-- userpermissions_set's "json" text parameter above).
CREATE OR REPLACE FUNCTION dbo.users_bulkcreate(TenantId uuid, Rows text)
RETURNS TABLE ("Created" int, "Skipped" int)
LANGUAGE sql
AS $$
    WITH dedup AS (
        SELECT DISTINCT ON (coalesce(r."Email", ''), coalesce(r."Phone", ''))
               r."Email", r."Phone", r."Role"
        FROM jsonb_to_recordset(users_bulkcreate.Rows::jsonb)
            AS r("Email" varchar(256), "Phone" varchar(32), "Role" varchar(64))
    ),
    eligible AS (
        SELECT gen_random_uuid() AS "Id", d."Email", d."Phone", d."Role"
        FROM dedup d
        WHERE NOT EXISTS (
            SELECT 1 FROM "dbo"."Users" u
            WHERE u."TenantId" = users_bulkcreate.TenantId
              AND ((d."Email" IS NOT NULL AND u."Email" = d."Email")
                OR (d."Phone" IS NOT NULL AND u."Phone" = d."Phone"))
        )
    ),
    ins_users AS (
        INSERT INTO "dbo"."Users" ("Id", "TenantId", "Email", "Phone", "IsPlatform", "Status")
        SELECT e."Id", users_bulkcreate.TenantId, e."Email", e."Phone", false, 'active' FROM eligible e
        RETURNING "Id"
    ),
    ins_roles AS (
        INSERT INTO "dbo"."UserRoles" ("UserId", "Role")
        SELECT e."Id", e."Role" FROM eligible e WHERE e."Role" IS NOT NULL
        RETURNING 1
    )
    SELECT (SELECT count(*) FROM ins_users)::int,
           ((SELECT count(*) FROM dedup) - (SELECT count(*) FROM ins_users))::int;
$$;

CREATE OR REPLACE FUNCTION dbo.users_listbytenant(TenantId uuid)
RETURNS TABLE ("Id" uuid, "Email" varchar(256), "Phone" varchar(32), "Status" varchar(20),
               "CreatedAt" timestamptz, "Roles" text)
LANGUAGE sql
AS $$
    SELECT
        u."Id", u."Email", u."Phone", u."Status", u."CreatedAt",
        COALESCE((
            SELECT string_agg(ur."Role", ',' ORDER BY ur."Role")
            FROM "dbo"."UserRoles" ur
            WHERE ur."UserId" = u."Id"
        ), '') AS "Roles"
    FROM "dbo"."Users" u
    WHERE u."TenantId" = users_listbytenant.TenantId
      AND COALESCE(u."IsPlatform", false) = false
      AND u."Status" <> 'removed'
      -- This screen manages CRM/staff access, not the whole tenant roster - students and
      -- parents have Users rows too (for their own app logins) but never a CRM console role,
      -- so without this filter they show up here mislabeled (the frontend defaults any
      -- unrecognized role to "Teacher" for display).
      -- Non-teaching staff (driver, security, etc.) carry their job title as the UserRoles
      -- role, not the literal 'staff' string - matching a linked Staff row keeps them included
      -- without having to enumerate every job-title role here.
      AND (
          EXISTS (
              SELECT 1 FROM "dbo"."UserRoles" ur
              WHERE ur."UserId" = u."Id" AND (ur."Role" LIKE 'school.%' OR ur."Role" = 'staff')
          )
          OR EXISTS (SELECT 1 FROM "dbo"."Staff" s WHERE s."UserId" = u."Id")
      )
    ORDER BY u."Email";
$$;
