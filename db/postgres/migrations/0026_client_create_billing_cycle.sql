-- 0026: persist the billing cycle chosen at onboarding. Extends dbo.client_create with a trailing
-- BillingCycle parameter (default 'month') and writes it into dbo.Tenants."BillingCycle" (added in
-- 0025), so recurring billing and invoices honour the customer's monthly/quarterly/yearly choice.
-- Reuses the existing column -- no schema change. The signature changes, so the old 14-arg
-- client_create (their 0024) is dropped first to avoid overload ambiguity.
-- Rollback (restore the 14-arg form from db/postgres/migrations/0024_client_create_students_count.sql):
--   DROP FUNCTION IF EXISTS dbo.client_create(varchar,varchar,varchar,varchar,varchar,varchar,varchar,uuid,varchar,text,text,varchar,text,int,varchar);

DROP FUNCTION IF EXISTS dbo.client_create;

CREATE OR REPLACE FUNCTION dbo.client_create(
    Name varchar(200), Slug varchar(100), Country varchar(120),
    ContactName varchar(200), ContactEmail varchar(256), ContactPhone varchar(40),
    Address varchar(300), PlanId uuid, Csm varchar(120),
    LogoUrl text DEFAULT NULL, ImageUrl text DEFAULT NULL,
    IdProofType varchar(16) DEFAULT NULL, IdProofUrl text DEFAULT NULL,
    StudentsCount int DEFAULT 0, BillingCycle varchar(12) DEFAULT 'month'
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
    v_cycle varchar(12);
BEGIN
    SELECT p."Name", p."Tier", p."Price", p."LimitsStudents", p."LimitsStaff", p."LimitsStorageGb"
    INTO v_plan_name, v_tier, v_mrr, v_ls, v_lst, v_lstor
    FROM "dbo"."Plans" p WHERE p."Id" = client_create.PlanId;

    v_cycle := CASE lower(COALESCE(client_create.BillingCycle, 'month'))
                   WHEN 'quarter' THEN 'quarter'
                   WHEN 'year' THEN 'year'
                   ELSE 'month' END;

    INSERT INTO "dbo"."Tenants" ("Id", "Name", "Slug", "Status", "Tier", "Country", "PlanId", "PlanName", "Mrr",
        "StudentsCount", "LimitsStudents", "LimitsStaff", "LimitsStorageGb", "ContactName", "ContactEmail", "ContactPhone",
        "Address", "LogoUrl", "ImageUrl", "IdProofType", "IdProofUrl", "Csm", "HealthScore", "BillingCycle")
    VALUES (v_id, client_create.Name, client_create.Slug, 'trial', v_tier, client_create.Country,
        client_create.PlanId, v_plan_name, COALESCE(v_mrr, 0),
        GREATEST(COALESCE(client_create.StudentsCount, 0), 0),
        v_ls, v_lst, v_lstor, client_create.ContactName, client_create.ContactEmail, client_create.ContactPhone,
        client_create.Address, client_create.LogoUrl, client_create.ImageUrl,
        client_create.IdProofType, client_create.IdProofUrl, client_create.Csm, 100, v_cycle);

    RETURN QUERY
    SELECT t."Id", t."Name", t."Slug", t."Country", t."Status", t."PlanId", t."PlanName", t."Tier", t."Mrr",
           t."StudentsCount", t."StaffCount", t."StorageGb",
           t."LimitsStudents", t."LimitsStaff", t."LimitsStorageGb", t."CreatedAt", t."Csm", t."HealthScore",
           t."ContactName", t."ContactEmail", t."ContactPhone", t."Address", t."LogoUrl", t."ImageUrl",
           t."IdProofType", t."IdProofUrl"
    FROM "dbo"."Tenants" t WHERE t."Id" = v_id;
END;
$$;
