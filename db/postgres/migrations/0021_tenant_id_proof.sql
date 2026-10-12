-- 0021: admin ID-proof (PAN / Aadhaar). Adds nullable dbo.Tenants."IdProofType" (varchar(16),
-- 'pan' | 'aadhaar') and "IdProofUrl" (text, a base64 data URL — no blob storage, mirrors
-- LogoUrl/ImageUrl), and extends dbo.client_create to accept and return them. Additive: existing
-- rows get NULLs. The old 11-arg client_create is dropped so there is no overload ambiguity.
-- Rollback:
--   DROP FUNCTION IF EXISTS dbo.client_create(varchar,varchar,varchar,varchar,varchar,varchar,varchar,uuid,varchar,text,text,varchar,text);
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "IdProofUrl";
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "IdProofType";
--   (then restore dbo.client_create from db/postgres/10_tenancy_procs.sql)

ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "IdProofType" varchar(16) NULL;
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "IdProofUrl" text NULL;

DROP FUNCTION IF EXISTS dbo.client_create;

CREATE OR REPLACE FUNCTION dbo.client_create(
    Name varchar(200), Slug varchar(100), Country varchar(120),
    ContactName varchar(200), ContactEmail varchar(256), ContactPhone varchar(40),
    Address varchar(300), PlanId uuid, Csm varchar(120),
    LogoUrl text DEFAULT NULL, ImageUrl text DEFAULT NULL,
    IdProofType varchar(16) DEFAULT NULL, IdProofUrl text DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "Name" varchar(200), "Slug" varchar(100), "Country" varchar(120), "Status" varchar(20),
    "PlanId" uuid, "PlanName" varchar(100), "Tier" varchar(20), "Mrr" numeric(18,2),
    "StudentsCount" int, "StaffCount" int, "StorageGb" numeric(18,2),
    "LimitsStudents" int, "LimitsStaff" int, "LimitsStorageGb" int, "CreatedAt" timestamptz,
    "Csm" varchar(120), "HealthScore" int,
    "ContactName" varchar(200), "ContactEmail" varchar(256), "ContactPhone" varchar(40),
    "Address" varchar(300), "LogoUrl" text, "ImageUrl" text,
    "IdProofType" varchar(16), "IdProofUrl" text
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_id uuid := gen_random_uuid();
    v_plan_name varchar(100); v_tier varchar(20); v_mrr numeric(18,2);
    v_ls int; v_lst int; v_lstor int;
BEGIN
    SELECT p."Name", p."Tier", p."Price", p."LimitsStudents", p."LimitsStaff", p."LimitsStorageGb"
    INTO v_plan_name, v_tier, v_mrr, v_ls, v_lst, v_lstor
    FROM "dbo"."Plans" p WHERE p."Id" = client_create.PlanId;

    INSERT INTO "dbo"."Tenants" ("Id", "Name", "Slug", "Status", "Tier", "Country", "PlanId", "PlanName", "Mrr",
        "LimitsStudents", "LimitsStaff", "LimitsStorageGb", "ContactName", "ContactEmail", "ContactPhone",
        "Address", "LogoUrl", "ImageUrl", "IdProofType", "IdProofUrl", "Csm", "HealthScore")
    VALUES (v_id, client_create.Name, client_create.Slug, 'trial', v_tier, client_create.Country,
        client_create.PlanId, v_plan_name, COALESCE(v_mrr, 0),
        v_ls, v_lst, v_lstor, client_create.ContactName, client_create.ContactEmail, client_create.ContactPhone,
        client_create.Address, client_create.LogoUrl, client_create.ImageUrl,
        client_create.IdProofType, client_create.IdProofUrl, client_create.Csm, 100);

    RETURN QUERY
    SELECT t."Id", t."Name", t."Slug", t."Country", t."Status", t."PlanId", t."PlanName", t."Tier", t."Mrr",
           t."StudentsCount", t."StaffCount", t."StorageGb",
           t."LimitsStudents", t."LimitsStaff", t."LimitsStorageGb", t."CreatedAt", t."Csm", t."HealthScore",
           t."ContactName", t."ContactEmail", t."ContactPhone", t."Address", t."LogoUrl", t."ImageUrl",
           t."IdProofType", t."IdProofUrl"
    FROM "dbo"."Tenants" t WHERE t."Id" = v_id;
END;
$$;

-- The other procs that return the ClientRow shape must also project the two new
-- columns, or Dapper's record mapping of the missing columns fails. Return-type
-- changes require DROP first. These do not accept/alter IdProof — they just return it.

DROP FUNCTION IF EXISTS dbo.client_setstatus;
CREATE OR REPLACE FUNCTION dbo.client_setstatus(Id uuid, Status varchar(20))
RETURNS TABLE (
    "Id" uuid, "Name" varchar(200), "Slug" varchar(100), "Country" varchar(120), "Status" varchar(20),
    "PlanId" uuid, "PlanName" varchar(100), "Tier" varchar(20), "Mrr" numeric(18,2),
    "StudentsCount" int, "StaffCount" int, "StorageGb" numeric(18,2),
    "LimitsStudents" int, "LimitsStaff" int, "LimitsStorageGb" int, "CreatedAt" timestamptz,
    "Csm" varchar(120), "HealthScore" int,
    "ContactName" varchar(200), "ContactEmail" varchar(256), "ContactPhone" varchar(40),
    "Address" varchar(300), "LogoUrl" text, "ImageUrl" text,
    "IdProofType" varchar(16), "IdProofUrl" text
)
LANGUAGE sql
AS $$
    UPDATE "dbo"."Tenants" SET "Status" = client_setstatus.Status WHERE "Id" = client_setstatus.Id;

    SELECT "Id", "Name", "Slug", "Country", "Status", "PlanId", "PlanName", "Tier", "Mrr",
           "StudentsCount", "StaffCount", "StorageGb",
           "LimitsStudents", "LimitsStaff", "LimitsStorageGb", "CreatedAt", "Csm", "HealthScore",
           "ContactName", "ContactEmail", "ContactPhone", "Address", "LogoUrl", "ImageUrl",
           "IdProofType", "IdProofUrl"
    FROM "dbo"."Tenants" WHERE "Id" = client_setstatus.Id;
$$;

DROP FUNCTION IF EXISTS dbo.client_changeplan;
CREATE OR REPLACE FUNCTION dbo.client_changeplan(Id uuid, PlanId uuid)
RETURNS TABLE (
    "Id" uuid, "Name" varchar(200), "Slug" varchar(100), "Country" varchar(120), "Status" varchar(20),
    "PlanId" uuid, "PlanName" varchar(100), "Tier" varchar(20), "Mrr" numeric(18,2),
    "StudentsCount" int, "StaffCount" int, "StorageGb" numeric(18,2),
    "LimitsStudents" int, "LimitsStaff" int, "LimitsStorageGb" int, "CreatedAt" timestamptz,
    "Csm" varchar(120), "HealthScore" int,
    "ContactName" varchar(200), "ContactEmail" varchar(256), "ContactPhone" varchar(40),
    "Address" varchar(300), "LogoUrl" text, "ImageUrl" text,
    "IdProofType" varchar(16), "IdProofUrl" text
)
LANGUAGE sql
AS $$
    UPDATE "dbo"."Tenants" t SET
        "PlanId" = client_changeplan.PlanId, "PlanName" = p."Name", "Tier" = p."Tier", "Mrr" = p."Price",
        "LimitsStudents" = p."LimitsStudents", "LimitsStaff" = p."LimitsStaff", "LimitsStorageGb" = p."LimitsStorageGb"
    FROM "dbo"."Plans" p
    WHERE p."Id" = client_changeplan.PlanId AND t."Id" = client_changeplan.Id;

    SELECT "Id", "Name", "Slug", "Country", "Status", "PlanId", "PlanName", "Tier", "Mrr",
           "StudentsCount", "StaffCount", "StorageGb",
           "LimitsStudents", "LimitsStaff", "LimitsStorageGb", "CreatedAt", "Csm", "HealthScore",
           "ContactName", "ContactEmail", "ContactPhone", "Address", "LogoUrl", "ImageUrl",
           "IdProofType", "IdProofUrl"
    FROM "dbo"."Tenants" WHERE "Id" = client_changeplan.Id;
$$;

DROP FUNCTION IF EXISTS dbo.client_updateprofile;
CREATE OR REPLACE FUNCTION dbo.client_updateprofile(
    Id uuid,
    Name varchar(200) DEFAULT NULL, Slug varchar(48) DEFAULT NULL, Country varchar(120) DEFAULT NULL,
    Address varchar(300) DEFAULT NULL, ContactName varchar(200) DEFAULT NULL,
    ContactEmail varchar(256) DEFAULT NULL, ContactPhone varchar(40) DEFAULT NULL,
    LogoUrl text DEFAULT NULL, ImageUrl text DEFAULT NULL,
    SetLogo boolean DEFAULT false, SetImage boolean DEFAULT false,
    Lat double precision DEFAULT NULL, Lng double precision DEFAULT NULL,
    GeofenceRadiusMeters int DEFAULT NULL, SetGeofence boolean DEFAULT false
)
RETURNS TABLE (
    "Id" uuid, "Name" varchar(200), "Slug" varchar(100), "Country" varchar(120), "Status" varchar(20),
    "PlanId" uuid, "PlanName" varchar(100), "Tier" varchar(20), "Mrr" numeric(18,2),
    "StudentsCount" int, "StaffCount" int, "StorageGb" numeric(18,2),
    "LimitsStudents" int, "LimitsStaff" int, "LimitsStorageGb" int, "CreatedAt" timestamptz,
    "Csm" varchar(120), "HealthScore" int,
    "ContactName" varchar(200), "ContactEmail" varchar(256), "ContactPhone" varchar(40),
    "Address" varchar(300), "LogoUrl" text, "ImageUrl" text,
    "IdProofType" varchar(16), "IdProofUrl" text
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_radius int;
    v_location_name varchar(120);
BEGIN
    UPDATE "dbo"."Tenants" t SET
        "Name" = COALESCE(client_updateprofile.Name, t."Name"),
        "Slug" = COALESCE(client_updateprofile.Slug, t."Slug"),
        "Country" = COALESCE(client_updateprofile.Country, t."Country"),
        "Address" = COALESCE(client_updateprofile.Address, t."Address"),
        "ContactName" = COALESCE(client_updateprofile.ContactName, t."ContactName"),
        "ContactEmail" = COALESCE(client_updateprofile.ContactEmail, t."ContactEmail"),
        "ContactPhone" = COALESCE(client_updateprofile.ContactPhone, t."ContactPhone"),
        "LogoUrl" = CASE WHEN client_updateprofile.SetLogo THEN client_updateprofile.LogoUrl ELSE t."LogoUrl" END,
        "ImageUrl" = CASE WHEN client_updateprofile.SetImage THEN client_updateprofile.ImageUrl ELSE t."ImageUrl" END,
        "Lat" = CASE WHEN client_updateprofile.SetGeofence THEN client_updateprofile.Lat ELSE t."Lat" END,
        "Lng" = CASE WHEN client_updateprofile.SetGeofence THEN client_updateprofile.Lng ELSE t."Lng" END,
        "GeofenceRadiusMeters" = CASE WHEN client_updateprofile.SetGeofence THEN client_updateprofile.GeofenceRadiusMeters ELSE t."GeofenceRadiusMeters" END
    WHERE t."Id" = client_updateprofile.Id;

    IF client_updateprofile.SetGeofence AND client_updateprofile.Lat IS NOT NULL AND client_updateprofile.Lng IS NOT NULL
       AND (client_updateprofile.Lat <> 0 OR client_updateprofile.Lng <> 0) THEN
        v_radius := COALESCE(client_updateprofile.GeofenceRadiusMeters, 250);
        SELECT COALESCE(client_updateprofile.Name, t."Name") INTO v_location_name
        FROM "dbo"."Tenants" t WHERE t."Id" = client_updateprofile.Id;
        PERFORM dbo.schoollocation_upsert(client_updateprofile.Id, client_updateprofile.Lat,
            client_updateprofile.Lng, v_radius, v_location_name);
    END IF;

    RETURN QUERY
    SELECT t."Id", t."Name", t."Slug", t."Country", t."Status", t."PlanId", t."PlanName", t."Tier", t."Mrr",
           t."StudentsCount", t."StaffCount", t."StorageGb",
           t."LimitsStudents", t."LimitsStaff", t."LimitsStorageGb", t."CreatedAt", t."Csm", t."HealthScore",
           t."ContactName", t."ContactEmail", t."ContactPhone", t."Address", t."LogoUrl", t."ImageUrl",
           t."IdProofType", t."IdProofUrl"
    FROM "dbo"."Tenants" t WHERE t."Id" = client_updateprofile.Id;
END;
$$;
