-- Tenancy module: PL/pgSQL conversions of the 24 remaining stored procedures in the
-- Client/Invoice/Onboarding/Plan/PlanUpgradeRequest/Subscription/Tenant/Ticket family
-- (dbo.Client_Delete was already converted in 08_sample_procedure_conversions.sql), plus
-- dbo.SchoolLocation_Upsert (a MERGE-family proc outside this module, converted here because
-- dbo.Client_UpdateProfile calls it directly).
-- Source: full OBJECT_DEFINITION() extracted read-only from the live SQL Server Sms database on
-- 2026-09-21, cross-checked against sqlserver-object-inventory.csv.
--
-- See 09_auth_procs.sql's header for the full naming convention (unquoted original identifiers;
-- quoted+lowercased only where a name collides with a reserved SQL/JSON-standard word).

-- ============================================================
-- SchoolLocation_Upsert (MERGE -> INSERT ... ON CONFLICT; dependency of Client_UpdateProfile)
-- ============================================================

-- MERGE ... ON tgt.TenantId = src.TenantId maps to ON CONFLICT (TenantId): db/postgres/06_indexes.sql
-- already has a matching UNIQUE INDEX (IX_SchoolLocations_TenantId), so no schema change needed.
CREATE OR REPLACE FUNCTION dbo.schoollocation_upsert(
    TenantId uuid, Lat double precision, Lng double precision, RadiusMeters int, Name varchar(120)
) RETURNS TABLE ("Lat" double precision, "Lng" double precision, "RadiusMeters" int, "Name" varchar(120))
LANGUAGE sql
AS $$
    INSERT INTO "dbo"."SchoolLocations" ("Id", "TenantId", "Lat", "Lng", "RadiusMeters", "Name")
    VALUES (gen_random_uuid(), TenantId, Lat, Lng, COALESCE(RadiusMeters, 50), Name)
    ON CONFLICT ("TenantId") DO UPDATE SET
        "Lat" = EXCLUDED."Lat", "Lng" = EXCLUDED."Lng",
        "RadiusMeters" = COALESCE(EXCLUDED."RadiusMeters", 50), "Name" = EXCLUDED."Name";

    SELECT "Lat", "Lng", "RadiusMeters", "Name" FROM "dbo"."SchoolLocations"
    WHERE "TenantId" = schoollocation_upsert.TenantId;
$$;

-- ============================================================
-- Client (Tenants)
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.client_changeplan(Id uuid, PlanId uuid)
RETURNS TABLE (
    "Id" uuid, "Name" varchar(200), "Slug" varchar(100), "Country" varchar(120), "Status" varchar(20),
    "PlanId" uuid, "PlanName" varchar(100), "Tier" varchar(20), "Mrr" numeric(18,2),
    "StudentsCount" int, "StaffCount" int, "StorageGb" numeric(18,2),
    "LimitsStudents" int, "LimitsStaff" int, "LimitsStorageGb" int, "CreatedAt" timestamptz,
    "Csm" varchar(120), "HealthScore" int,
    "ContactName" varchar(200), "ContactEmail" varchar(256), "ContactPhone" varchar(40),
    "Address" varchar(300), "LogoUrl" text, "ImageUrl" text
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
           "ContactName", "ContactEmail", "ContactPhone", "Address", "LogoUrl", "ImageUrl"
    FROM "dbo"."Tenants" WHERE "Id" = client_changeplan.Id;
$$;

CREATE OR REPLACE FUNCTION dbo.client_create(
    Name varchar(200), Slug varchar(100), Country varchar(120),
    ContactName varchar(200), ContactEmail varchar(256), ContactPhone varchar(40),
    Address varchar(300), PlanId uuid, Csm varchar(120),
    LogoUrl text DEFAULT NULL, ImageUrl text DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "Name" varchar(200), "Slug" varchar(100), "Country" varchar(120), "Status" varchar(20),
    "PlanId" uuid, "PlanName" varchar(100), "Tier" varchar(20), "Mrr" numeric(18,2),
    "StudentsCount" int, "StaffCount" int, "StorageGb" numeric(18,2),
    "LimitsStudents" int, "LimitsStaff" int, "LimitsStorageGb" int, "CreatedAt" timestamptz,
    "Csm" varchar(120), "HealthScore" int,
    "ContactName" varchar(200), "ContactEmail" varchar(256), "ContactPhone" varchar(40),
    "Address" varchar(300), "LogoUrl" text, "ImageUrl" text
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
        "Address", "LogoUrl", "ImageUrl", "Csm", "HealthScore")
    VALUES (v_id, client_create.Name, client_create.Slug, 'trial', v_tier, client_create.Country,
        client_create.PlanId, v_plan_name, COALESCE(v_mrr, 0),
        v_ls, v_lst, v_lstor, client_create.ContactName, client_create.ContactEmail, client_create.ContactPhone,
        client_create.Address, client_create.LogoUrl, client_create.ImageUrl, client_create.Csm, 100);

    RETURN QUERY
    SELECT t."Id", t."Name", t."Slug", t."Country", t."Status", t."PlanId", t."PlanName", t."Tier", t."Mrr",
           t."StudentsCount", t."StaffCount", t."StorageGb",
           t."LimitsStudents", t."LimitsStaff", t."LimitsStorageGb", t."CreatedAt", t."Csm", t."HealthScore",
           t."ContactName", t."ContactEmail", t."ContactPhone", t."Address", t."LogoUrl", t."ImageUrl"
    FROM "dbo"."Tenants" t WHERE t."Id" = v_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.client_setstatus(Id uuid, Status varchar(20))
RETURNS TABLE (
    "Id" uuid, "Name" varchar(200), "Slug" varchar(100), "Country" varchar(120), "Status" varchar(20),
    "PlanId" uuid, "PlanName" varchar(100), "Tier" varchar(20), "Mrr" numeric(18,2),
    "StudentsCount" int, "StaffCount" int, "StorageGb" numeric(18,2),
    "LimitsStudents" int, "LimitsStaff" int, "LimitsStorageGb" int, "CreatedAt" timestamptz,
    "Csm" varchar(120), "HealthScore" int,
    "ContactName" varchar(200), "ContactEmail" varchar(256), "ContactPhone" varchar(40),
    "Address" varchar(300), "LogoUrl" text, "ImageUrl" text
)
LANGUAGE sql
AS $$
    UPDATE "dbo"."Tenants" SET "Status" = client_setstatus.Status WHERE "Id" = client_setstatus.Id;

    SELECT "Id", "Name", "Slug", "Country", "Status", "PlanId", "PlanName", "Tier", "Mrr",
           "StudentsCount", "StaffCount", "StorageGb",
           "LimitsStudents", "LimitsStaff", "LimitsStorageGb", "CreatedAt", "Csm", "HealthScore",
           "ContactName", "ContactEmail", "ContactPhone", "Address", "LogoUrl", "ImageUrl"
    FROM "dbo"."Tenants" WHERE "Id" = client_setstatus.Id;
$$;

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
    "Address" varchar(300), "LogoUrl" text, "ImageUrl" text
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
           t."ContactName", t."ContactEmail", t."ContactPhone", t."Address", t."LogoUrl", t."ImageUrl"
    FROM "dbo"."Tenants" t WHERE t."Id" = client_updateprofile.Id;
END;
$$;

-- ============================================================
-- Invoice
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.invoice_create(
    TenantId uuid, TenantName varchar(200), PlanName varchar(100), Amount numeric(18,2), Due timestamptz
)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "TenantName" varchar(200), "PlanName" varchar(100),
               "Amount" numeric(18,2), "Status" varchar(20), "Issued" timestamptz, "Due" timestamptz,
               "PaidOn" timestamptz)
LANGUAGE sql
AS $$
    INSERT INTO "dbo"."Invoices" ("Id", "TenantId", "TenantName", "PlanName", "Amount", "Status", "Issued", "Due")
    VALUES (gen_random_uuid(), TenantId, TenantName, PlanName, Amount, 'open', now(), Due)
    RETURNING "Id", "TenantId", "TenantName", "PlanName", "Amount", "Status", "Issued", "Due", "PaidOn";
$$;

CREATE OR REPLACE FUNCTION dbo.invoice_markpaid(Id uuid)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "TenantName" varchar(200), "PlanName" varchar(100),
               "Amount" numeric(18,2), "Status" varchar(20), "Issued" timestamptz, "Due" timestamptz,
               "PaidOn" timestamptz)
LANGUAGE sql
AS $$
    UPDATE "dbo"."Invoices" SET "Status" = 'paid', "PaidOn" = now()::date
    WHERE "Id" = invoice_markpaid.Id
    RETURNING "Id", "TenantId", "TenantName", "PlanName", "Amount", "Status", "Issued", "Due", "PaidOn";
$$;

CREATE OR REPLACE FUNCTION dbo.invoice_refund(Id uuid)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "TenantName" varchar(200), "PlanName" varchar(100),
               "Amount" numeric(18,2), "Status" varchar(20), "Issued" timestamptz, "Due" timestamptz,
               "PaidOn" timestamptz)
LANGUAGE sql
AS $$
    UPDATE "dbo"."Invoices" SET "Status" = 'open', "PaidOn" = NULL
    WHERE "Id" = invoice_refund.Id AND "Status" = 'paid';

    SELECT "Id", "TenantId", "TenantName", "PlanName", "Amount", "Status", "Issued", "Due", "PaidOn"
    FROM "dbo"."Invoices" WHERE "Id" = invoice_refund.Id;
$$;

-- ============================================================
-- Onboarding
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.onboarding_advance(Id uuid, Stage varchar(20))
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    UPDATE "dbo"."OnboardingItems" SET "Stage" = onboarding_advance.Stage WHERE "Id" = onboarding_advance.Id;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.onboarding_create(
    Name varchar(200), Slug varchar(100), Owner varchar(120), Value numeric(18,2), Stage varchar(20),
    ContactName varchar(200) DEFAULT NULL, ContactEmail varchar(256) DEFAULT NULL,
    ContactPhone varchar(40) DEFAULT NULL, Address varchar(300) DEFAULT NULL, TenantId uuid DEFAULT NULL
)
RETURNS uuid
LANGUAGE plpgsql
AS $$
DECLARE v_id uuid := gen_random_uuid();
BEGIN
    INSERT INTO "dbo"."OnboardingItems" ("Id", "TenantId", "Name", "Slug", "Owner", "Value", "Stage",
        "ContactName", "ContactEmail", "ContactPhone", "Address")
    VALUES (v_id, onboarding_create.TenantId, onboarding_create.Name, onboarding_create.Slug,
        onboarding_create.Owner, COALESCE(onboarding_create.Value, 0), COALESCE(onboarding_create.Stage, 'lead'),
        onboarding_create.ContactName, onboarding_create.ContactEmail, onboarding_create.ContactPhone,
        onboarding_create.Address);

    INSERT INTO "dbo"."OnboardingChecklist" ("OnboardingId", "Seq", "Label", "Done") VALUES
        (v_id, 1, 'Account created', true), (v_id, 2, 'Admin invited', false), (v_id, 3, 'Data imported', false),
        (v_id, 4, 'First login', false), (v_id, 5, 'Payment set up', false);

    RETURN v_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.onboarding_setchecklist(Id uuid, Label varchar(100), Done boolean)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    UPDATE "dbo"."OnboardingChecklist" SET "Done" = onboarding_setchecklist.Done
    WHERE "OnboardingId" = onboarding_setchecklist.Id AND "Label" = onboarding_setchecklist.Label;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

-- ============================================================
-- Plan (IF EXISTS UPDATE ELSE INSERT -> INSERT ... ON CONFLICT DO UPDATE, keyed on the PK)
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.plan_upsert(
    Id uuid, Name varchar(100), Tier varchar(20), Pricing varchar(20),
    Price numeric(18,2), PerStudent numeric(18,2), MinStudents int, Period varchar(20),
    FeaturesCsv varchar(4000), LimitsStudents int, LimitsStaff int, LimitsStorageGb int,
    Visibility varchar(20), Audience varchar(20), Band varchar(100),
    OfferLabel varchar(100), OfferPct int, Color varchar(40), Description varchar(1000)
)
RETURNS TABLE (
    "Id" uuid, "Name" varchar(100), "Tier" varchar(20), "Pricing" varchar(20), "Price" numeric(18,2),
    "PerStudent" numeric(18,2), "MinStudents" int, "Period" varchar(20), "FeaturesCsv" varchar(4000),
    "LimitsStudents" int, "LimitsStaff" int, "LimitsStorageGb" int,
    "Visibility" varchar(20), "Audience" varchar(20), "Band" varchar(100),
    "OfferLabel" varchar(100), "OfferPct" int, "Color" varchar(40), "Description" varchar(1000)
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_id uuid := COALESCE(plan_upsert.Id, gen_random_uuid());
BEGIN
    INSERT INTO "dbo"."Plans" ("Id", "Name", "Tier", "Pricing", "Price", "PerStudent", "MinStudents", "Period", "FeaturesCsv",
        "LimitsStudents", "LimitsStaff", "LimitsStorageGb", "Visibility", "Audience", "Band",
        "OfferLabel", "OfferPct", "Color", "Description")
    VALUES (v_id, plan_upsert.Name, plan_upsert.Tier, plan_upsert.Pricing,
        plan_upsert.Price, plan_upsert.PerStudent, plan_upsert.MinStudents, plan_upsert.Period, plan_upsert.FeaturesCsv,
        plan_upsert.LimitsStudents, plan_upsert.LimitsStaff, plan_upsert.LimitsStorageGb,
        plan_upsert.Visibility, plan_upsert.Audience, plan_upsert.Band,
        plan_upsert.OfferLabel, plan_upsert.OfferPct, plan_upsert.Color, plan_upsert.Description)
    ON CONFLICT ("Id") DO UPDATE SET
        "Name" = EXCLUDED."Name", "Tier" = EXCLUDED."Tier", "Pricing" = EXCLUDED."Pricing",
        "Price" = EXCLUDED."Price", "PerStudent" = EXCLUDED."PerStudent", "MinStudents" = EXCLUDED."MinStudents",
        "Period" = EXCLUDED."Period", "FeaturesCsv" = EXCLUDED."FeaturesCsv",
        "LimitsStudents" = EXCLUDED."LimitsStudents", "LimitsStaff" = EXCLUDED."LimitsStaff",
        "LimitsStorageGb" = EXCLUDED."LimitsStorageGb", "Visibility" = EXCLUDED."Visibility",
        "Audience" = EXCLUDED."Audience", "Band" = EXCLUDED."Band", "OfferLabel" = EXCLUDED."OfferLabel",
        "OfferPct" = EXCLUDED."OfferPct", "Color" = EXCLUDED."Color", "Description" = EXCLUDED."Description";

    RETURN QUERY
    SELECT p."Id", p."Name", p."Tier", p."Pricing", p."Price", p."PerStudent", p."MinStudents", p."Period", p."FeaturesCsv",
           p."LimitsStudents", p."LimitsStaff", p."LimitsStorageGb", p."Visibility", p."Audience", p."Band",
           p."OfferLabel", p."OfferPct", p."Color", p."Description"
    FROM "dbo"."Plans" p WHERE p."Id" = v_id;
END;
$$;

-- ============================================================
-- PlanUpgradeRequest
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.planupgraderequest_get(Id uuid)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "TenantName" varchar(200),
    "FromPlanId" uuid, "FromPlanName" varchar(100), "FromTier" varchar(20),
    "ToPlanId" uuid, "ToPlanName" varchar(100), "ToTier" varchar(20),
    "Amount" numeric(18,2), "Currency" varchar(8), "Mode" varchar(20), "Status" varchar(40), "InvoiceId" uuid,
    "RazorpayOrderId" varchar(80), "RazorpayPaymentId" varchar(80),
    "RequestedByUserId" uuid, "ReviewedByUserId" uuid, "Notes" varchar(500),
    "CreatedAt" timestamptz, "UpdatedAt" timestamptz
)
LANGUAGE sql
AS $$
    SELECT
        r."Id", r."TenantId", t."Name",
        r."FromPlanId", fp."Name", fp."Tier",
        r."ToPlanId", tp."Name", tp."Tier",
        r."Amount", r."Currency", r."Mode", r."Status", r."InvoiceId",
        r."RazorpayOrderId", r."RazorpayPaymentId",
        r."RequestedByUserId", r."ReviewedByUserId", r."Notes",
        r."CreatedAt", r."UpdatedAt"
    FROM "dbo"."PlanUpgradeRequests" r
    JOIN "dbo"."Tenants" t ON t."Id" = r."TenantId"
    LEFT JOIN "dbo"."Plans" fp ON fp."Id" = r."FromPlanId"
    JOIN "dbo"."Plans" tp ON tp."Id" = r."ToPlanId"
    WHERE r."Id" = planupgraderequest_get.Id;
$$;

CREATE OR REPLACE FUNCTION dbo.planupgraderequest_getbyorder(RazorpayOrderId varchar(80))
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "TenantName" varchar(200),
    "FromPlanId" uuid, "FromPlanName" varchar(100), "FromTier" varchar(20),
    "ToPlanId" uuid, "ToPlanName" varchar(100), "ToTier" varchar(20),
    "Amount" numeric(18,2), "Currency" varchar(8), "Mode" varchar(20), "Status" varchar(40), "InvoiceId" uuid,
    "RazorpayOrderId" varchar(80), "RazorpayPaymentId" varchar(80),
    "RequestedByUserId" uuid, "ReviewedByUserId" uuid, "Notes" varchar(500),
    "CreatedAt" timestamptz, "UpdatedAt" timestamptz
)
LANGUAGE sql
AS $$
    SELECT
        r."Id", r."TenantId", t."Name",
        r."FromPlanId", fp."Name", fp."Tier",
        r."ToPlanId", tp."Name", tp."Tier",
        r."Amount", r."Currency", r."Mode", r."Status", r."InvoiceId",
        r."RazorpayOrderId", r."RazorpayPaymentId",
        r."RequestedByUserId", r."ReviewedByUserId", r."Notes",
        r."CreatedAt", r."UpdatedAt"
    FROM "dbo"."PlanUpgradeRequests" r
    JOIN "dbo"."Tenants" t ON t."Id" = r."TenantId"
    LEFT JOIN "dbo"."Plans" fp ON fp."Id" = r."FromPlanId"
    JOIN "dbo"."Plans" tp ON tp."Id" = r."ToPlanId"
    WHERE r."RazorpayOrderId" = planupgraderequest_getbyorder.RazorpayOrderId;
$$;

CREATE OR REPLACE FUNCTION dbo.planupgraderequest_list("status" varchar(40) DEFAULT NULL)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "TenantName" varchar(200),
    "FromPlanId" uuid, "FromPlanName" varchar(100), "FromTier" varchar(20),
    "ToPlanId" uuid, "ToPlanName" varchar(100), "ToTier" varchar(20),
    "Amount" numeric(18,2), "Currency" varchar(8), "Mode" varchar(20), "Status" varchar(40), "InvoiceId" uuid,
    "RazorpayOrderId" varchar(80), "RazorpayPaymentId" varchar(80),
    "RequestedByUserId" uuid, "ReviewedByUserId" uuid, "Notes" varchar(500),
    "CreatedAt" timestamptz, "UpdatedAt" timestamptz
)
LANGUAGE sql
AS $$
    SELECT
        r."Id", r."TenantId", t."Name",
        r."FromPlanId", fp."Name", fp."Tier",
        r."ToPlanId", tp."Name", tp."Tier",
        r."Amount", r."Currency", r."Mode", r."Status", r."InvoiceId",
        r."RazorpayOrderId", r."RazorpayPaymentId",
        r."RequestedByUserId", r."ReviewedByUserId", r."Notes",
        r."CreatedAt", r."UpdatedAt"
    FROM "dbo"."PlanUpgradeRequests" r
    JOIN "dbo"."Tenants" t ON t."Id" = r."TenantId"
    LEFT JOIN "dbo"."Plans" fp ON fp."Id" = r."FromPlanId"
    JOIN "dbo"."Plans" tp ON tp."Id" = r."ToPlanId"
    WHERE ("status" IS NULL OR r."Status" = "status")
    ORDER BY r."CreatedAt" DESC;
$$;

-- TRY_CONVERT(uniqueidentifier, ...) -> a regex-guarded cast (only this call site needs it; the
-- audit's shared safe_cast() helper for the ~4 procedures using TRY_CONVERT/TRY_CAST is a
-- separate, not-yet-done piece of work, tracked there rather than duplicated narrowly here).
CREATE OR REPLACE FUNCTION dbo.planupgraderequest_listbytenants("tenantids" text)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "TenantName" varchar(200),
    "FromPlanId" uuid, "FromPlanName" varchar(100), "FromTier" varchar(20),
    "ToPlanId" uuid, "ToPlanName" varchar(100), "ToTier" varchar(20),
    "Amount" numeric(18,2), "Currency" varchar(8), "Mode" varchar(20), "Status" varchar(40), "InvoiceId" uuid,
    "RazorpayOrderId" varchar(80), "RazorpayPaymentId" varchar(80),
    "RequestedByUserId" uuid, "ReviewedByUserId" uuid, "Notes" varchar(500),
    "CreatedAt" timestamptz, "UpdatedAt" timestamptz
)
LANGUAGE sql
AS $$
    WITH ids AS (
        SELECT trim(value)::uuid AS "Id"
        FROM unnest(string_to_array("tenantids", ',')) AS value
        WHERE trim(value) <> ''
          AND trim(value) ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
    )
    SELECT
        r."Id", r."TenantId", t."Name",
        r."FromPlanId", fp."Name", fp."Tier",
        r."ToPlanId", tp."Name", tp."Tier",
        r."Amount", r."Currency", r."Mode", r."Status", r."InvoiceId",
        r."RazorpayOrderId", r."RazorpayPaymentId",
        r."RequestedByUserId", r."ReviewedByUserId", r."Notes",
        r."CreatedAt", r."UpdatedAt"
    FROM "dbo"."PlanUpgradeRequests" r
    JOIN ids ON ids."Id" = r."TenantId"
    JOIN "dbo"."Tenants" t ON t."Id" = r."TenantId"
    LEFT JOIN "dbo"."Plans" fp ON fp."Id" = r."FromPlanId"
    JOIN "dbo"."Plans" tp ON tp."Id" = r."ToPlanId"
    ORDER BY r."CreatedAt" DESC;
$$;

CREATE OR REPLACE FUNCTION dbo.planupgraderequest_create(
    TenantId uuid, ToPlanId uuid, Amount numeric(18,2), Currency varchar(8), Mode varchar(20), "status" varchar(40),
    FromPlanId uuid DEFAULT NULL, RequestedByUserId uuid DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "TenantName" varchar(200),
    "FromPlanId" uuid, "FromPlanName" varchar(100), "FromTier" varchar(20),
    "ToPlanId" uuid, "ToPlanName" varchar(100), "ToTier" varchar(20),
    "Amount" numeric(18,2), "Currency" varchar(8), "Mode" varchar(20), "Status" varchar(40), "InvoiceId" uuid,
    "RazorpayOrderId" varchar(80), "RazorpayPaymentId" varchar(80),
    "RequestedByUserId" uuid, "ReviewedByUserId" uuid, "Notes" varchar(500),
    "CreatedAt" timestamptz, "UpdatedAt" timestamptz
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_id uuid := gen_random_uuid();
BEGIN
    INSERT INTO "dbo"."PlanUpgradeRequests" (
        "Id", "TenantId", "FromPlanId", "ToPlanId", "Amount", "Currency", "Mode", "Status",
        "RequestedByUserId", "CreatedAt", "UpdatedAt")
    VALUES (
        v_id, planupgraderequest_create.TenantId, planupgraderequest_create.FromPlanId,
        planupgraderequest_create.ToPlanId, planupgraderequest_create.Amount,
        COALESCE(planupgraderequest_create.Currency, 'INR'), planupgraderequest_create.Mode,
        planupgraderequest_create."status", planupgraderequest_create.RequestedByUserId, now(), now());

    RETURN QUERY SELECT * FROM dbo.planupgraderequest_get(v_id);
END;
$$;

CREATE OR REPLACE FUNCTION dbo.planupgraderequest_setstatus(
    Id uuid, "status" varchar(40), ReviewedByUserId uuid DEFAULT NULL, Notes varchar(500) DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "TenantName" varchar(200),
    "FromPlanId" uuid, "FromPlanName" varchar(100), "FromTier" varchar(20),
    "ToPlanId" uuid, "ToPlanName" varchar(100), "ToTier" varchar(20),
    "Amount" numeric(18,2), "Currency" varchar(8), "Mode" varchar(20), "Status" varchar(40), "InvoiceId" uuid,
    "RazorpayOrderId" varchar(80), "RazorpayPaymentId" varchar(80),
    "RequestedByUserId" uuid, "ReviewedByUserId" uuid, "Notes" varchar(500),
    "CreatedAt" timestamptz, "UpdatedAt" timestamptz
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE "dbo"."PlanUpgradeRequests" SET
        "Status" = planupgraderequest_setstatus."status",
        "ReviewedByUserId" = COALESCE(planupgraderequest_setstatus.ReviewedByUserId, "ReviewedByUserId"),
        "Notes" = COALESCE(planupgraderequest_setstatus.Notes, "Notes"),
        "UpdatedAt" = now()
    WHERE "Id" = planupgraderequest_setstatus.Id;

    RETURN QUERY SELECT * FROM dbo.planupgraderequest_get(planupgraderequest_setstatus.Id);
END;
$$;

CREATE OR REPLACE FUNCTION dbo.planupgraderequest_attachrazorpay(
    Id uuid, RazorpayOrderId varchar(80) DEFAULT NULL, RazorpayPaymentId varchar(80) DEFAULT NULL,
    "status" varchar(40) DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "TenantName" varchar(200),
    "FromPlanId" uuid, "FromPlanName" varchar(100), "FromTier" varchar(20),
    "ToPlanId" uuid, "ToPlanName" varchar(100), "ToTier" varchar(20),
    "Amount" numeric(18,2), "Currency" varchar(8), "Mode" varchar(20), "Status" varchar(40), "InvoiceId" uuid,
    "RazorpayOrderId" varchar(80), "RazorpayPaymentId" varchar(80),
    "RequestedByUserId" uuid, "ReviewedByUserId" uuid, "Notes" varchar(500),
    "CreatedAt" timestamptz, "UpdatedAt" timestamptz
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE "dbo"."PlanUpgradeRequests" SET
        "RazorpayOrderId" = COALESCE(planupgraderequest_attachrazorpay.RazorpayOrderId, "RazorpayOrderId"),
        "RazorpayPaymentId" = COALESCE(planupgraderequest_attachrazorpay.RazorpayPaymentId, "RazorpayPaymentId"),
        "Status" = COALESCE(planupgraderequest_attachrazorpay."status", "Status"),
        "UpdatedAt" = now()
    WHERE "Id" = planupgraderequest_attachrazorpay.Id;

    RETURN QUERY SELECT * FROM dbo.planupgraderequest_get(planupgraderequest_attachrazorpay.Id);
END;
$$;

CREATE OR REPLACE FUNCTION dbo.planupgraderequest_attachinvoice(Id uuid, InvoiceId uuid)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "TenantName" varchar(200),
    "FromPlanId" uuid, "FromPlanName" varchar(100), "FromTier" varchar(20),
    "ToPlanId" uuid, "ToPlanName" varchar(100), "ToTier" varchar(20),
    "Amount" numeric(18,2), "Currency" varchar(8), "Mode" varchar(20), "Status" varchar(40), "InvoiceId" uuid,
    "RazorpayOrderId" varchar(80), "RazorpayPaymentId" varchar(80),
    "RequestedByUserId" uuid, "ReviewedByUserId" uuid, "Notes" varchar(500),
    "CreatedAt" timestamptz, "UpdatedAt" timestamptz
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE "dbo"."PlanUpgradeRequests" SET
        "InvoiceId" = planupgraderequest_attachinvoice.InvoiceId,
        "UpdatedAt" = now()
    WHERE "Id" = planupgraderequest_attachinvoice.Id;

    RETURN QUERY SELECT * FROM dbo.planupgraderequest_get(planupgraderequest_attachinvoice.Id);
END;
$$;

-- ============================================================
-- Subscription
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.subscription_create(TenantId uuid, PlanId uuid, Seats int)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "PlanId" uuid, "Status" varchar(20),
               "StartedAt" timestamptz, "RenewsAt" timestamptz, "Seats" int)
LANGUAGE sql
AS $$
    INSERT INTO "dbo"."Subscriptions" ("Id", "TenantId", "PlanId", "Status", "RenewsAt", "Seats")
    VALUES (gen_random_uuid(), TenantId, PlanId, 'active', now() + interval '1 month', Seats)
    RETURNING "Id", "TenantId", "PlanId", "Status", "StartedAt", "RenewsAt", "Seats";
$$;

CREATE OR REPLACE FUNCTION dbo.subscription_setplan(TenantId uuid, PlanId uuid, Seats int)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "PlanId" uuid, "Status" varchar(20),
               "StartedAt" timestamptz, "RenewsAt" timestamptz, "Seats" int)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_id uuid;
BEGIN
    SELECT s."Id" INTO v_id FROM "dbo"."Subscriptions" s
    WHERE s."TenantId" = subscription_setplan.TenantId AND s."Status" = 'active'
    ORDER BY s."StartedAt" DESC LIMIT 1;

    IF v_id IS NULL THEN
        v_id := gen_random_uuid();
        INSERT INTO "dbo"."Subscriptions" ("Id", "TenantId", "PlanId", "Status", "RenewsAt", "Seats")
        VALUES (v_id, subscription_setplan.TenantId, subscription_setplan.PlanId, 'active',
            now() + interval '1 month', COALESCE(subscription_setplan.Seats, 0));
    ELSE
        UPDATE "dbo"."Subscriptions" SET
            "PlanId" = subscription_setplan.PlanId,
            "Seats" = COALESCE(subscription_setplan.Seats, "Seats"),
            "RenewsAt" = now() + interval '1 month'
        WHERE "Id" = v_id;
    END IF;

    RETURN QUERY
    SELECT s."Id", s."TenantId", s."PlanId", s."Status", s."StartedAt", s."RenewsAt", s."Seats"
    FROM "dbo"."Subscriptions" s WHERE s."Id" = v_id;
END;
$$;

-- ============================================================
-- Tenant
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.tenant_gettierandstatus(TenantId uuid)
RETURNS TABLE ("Tier" varchar(20), "Status" varchar(20))
LANGUAGE sql
AS $$
    SELECT t."Tier", t."Status" FROM "dbo"."Tenants" t WHERE t."Id" = tenant_gettierandstatus.TenantId;
$$;

-- ============================================================
-- Ticket ("role"/"text" quoted: reserved words, see 09_auth_procs.sql's "json" note)
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.ticket_addmessage(
    TicketId uuid, Who varchar(120), "role" varchar(20), "text" varchar(4000)
)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    INSERT INTO "dbo"."TicketMessages" ("TicketId", "Who", "Role", "Text")
    VALUES (ticket_addmessage.TicketId, ticket_addmessage.Who, COALESCE("role", 'agent'), "text");

    UPDATE "dbo"."Tickets" SET "MessagesCount" = "MessagesCount" + 1, "Updated" = now()
    WHERE "Id" = ticket_addmessage.TicketId;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.ticket_create(
    Subject varchar(300), TenantId uuid, TenantName varchar(200), Priority varchar(20)
)
RETURNS uuid
LANGUAGE sql
AS $$
    INSERT INTO "dbo"."Tickets" ("Id", "Subject", "TenantId", "TenantName", "Status", "Priority")
    VALUES (gen_random_uuid(), Subject, TenantId, TenantName, 'open', COALESCE(Priority, 'normal'))
    RETURNING "Id";
$$;

CREATE OR REPLACE FUNCTION dbo.ticket_update(Id uuid, Status varchar(20), Assignee varchar(120))
RETURNS TABLE ("Id" uuid, "Subject" varchar(300), "TenantId" uuid, "TenantName" varchar(200),
               "Status" varchar(20), "Priority" varchar(20), "Assignee" varchar(120),
               "Created" timestamptz, "Updated" timestamptz, "MessagesCount" int)
LANGUAGE sql
AS $$
    UPDATE "dbo"."Tickets"
    SET "Status" = COALESCE(ticket_update.Status, "Status"),
        "Assignee" = COALESCE(ticket_update.Assignee, "Assignee"),
        "Updated" = now()
    WHERE "Id" = ticket_update.Id;

    SELECT "Id", "Subject", "TenantId", "TenantName", "Status", "Priority", "Assignee", "Created", "Updated", "MessagesCount"
    FROM "dbo"."Tickets" WHERE "Id" = ticket_update.Id;
$$;
