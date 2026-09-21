-- Finance module: PL/pgSQL conversions of the 14 stored procedures in the
-- FeeHead/FeeInvoice/FeePayment/FeeStructure/Fee_SummaryByTenants family.
-- Source: full OBJECT_DEFINITION() extracted read-only from the live SQL Server Sms database on
-- 2026-09-21, cross-checked against sqlserver-object-inventory.csv.
--
-- See 09_auth_procs.sql's header for the naming convention and 10_tenancy_procs.sql's for the
-- `#variable_conflict use_column` note (both apply here too).
--
-- FeeStructure_Get/FeeStructure_List take no TenantId parameter, matching the original SQL Server
-- procs exactly -- both rely entirely on RLS to scope rows to the caller's tenant. This is
-- faithful to the source, not an oversight (see the audit's note on "SEE ALSO Fee_Get" - once a
-- non-superuser app role exists per Sis's flagged RLS gap, verify these still scope correctly).

-- ============================================================
-- Fee_SummaryByTenants (OPENJSON -> jsonb_array_elements_text; TenantIds stays text, not jsonb,
-- for the same function-overload-resolution reason as 09_auth_procs.sql's users_bulkcreate)
-- ============================================================

-- "from"/"to" are timestamp (not date): the C# DateOnly.ToDateTime() this comes from binds as
-- timestamp (no timezone) via Npgsql, same function-overload-resolution reasoning as
-- feeinvoice_create.DueDate in this same file.
CREATE OR REPLACE FUNCTION dbo.fee_summarybytenants("tenantids" text, "from" timestamp, "to" timestamp)
RETURNS TABLE ("TenantId" uuid, "Name" varchar(200), "Collected" numeric(18,2), "Outstanding" numeric(18,2),
               "PaymentCount" int, "InvoiceCount" int)
LANGUAGE sql
AS $$
    WITH ids AS (
        SELECT jsonb_array_elements_text("tenantids"::jsonb)::uuid AS "TenantId"
    ),
    tenants AS (
        SELECT t."Id", t."Name"
        FROM "dbo"."Tenants" t
        JOIN ids i ON i."TenantId" = t."Id"
    ),
    collected AS (
        SELECT p."TenantId",
               sum(p."Amount") AS "Collected",
               count(*) AS "PaymentCount"
        FROM "dbo"."FeePayments" p
        JOIN ids i ON i."TenantId" = p."TenantId"
        WHERE p."Date" >= "from"::date AND p."Date" <= "to"::date
        GROUP BY p."TenantId"
    ),
    outstanding AS (
        SELECT inv."TenantId",
               sum(inv."Amount") AS "Outstanding",
               count(*) AS "InvoiceCount"
        FROM "dbo"."FeeInvoices" inv
        JOIN ids i ON i."TenantId" = inv."TenantId"
        WHERE inv."Status" <> 'paid'
        GROUP BY inv."TenantId"
    )
    SELECT
        t."Id", t."Name",
        CAST(COALESCE(c."Collected", 0) AS numeric(18,2)),
        CAST(COALESCE(o."Outstanding", 0) AS numeric(18,2)),
        CAST(COALESCE(c."PaymentCount", 0) AS int),
        CAST(COALESCE(o."InvoiceCount", 0) AS int)
    FROM tenants t
    LEFT JOIN collected c ON c."TenantId" = t."Id"
    LEFT JOIN outstanding o ON o."TenantId" = t."Id"
    ORDER BY 3 DESC, t."Name";
$$;

-- ============================================================
-- FeeHead
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.feehead_create(
    TenantId uuid, Name varchar(120), Code varchar(40) DEFAULT NULL,
    Active boolean DEFAULT true, IsSystem boolean DEFAULT false, IsTransportFeeHead boolean DEFAULT false,
    Description text DEFAULT NULL
)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "Name" varchar(120), "Code" varchar(40), "Active" boolean,
               "IsSystem" boolean, "IsTransportFeeHead" boolean, "Description" text)
LANGUAGE sql
AS $$
    INSERT INTO "dbo"."FeeHeads" ("Id", "TenantId", "Name", "Code", "Active", "IsSystem", "IsTransportFeeHead", "Description")
    VALUES (gen_random_uuid(), TenantId, Name, Code, Active, IsSystem, IsTransportFeeHead, Description)
    RETURNING "Id", "TenantId", "Name", "Code", "Active", "IsSystem", "IsTransportFeeHead", "Description";
$$;

CREATE OR REPLACE FUNCTION dbo.feehead_delete(Id uuid, TenantId uuid)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    DELETE FROM "dbo"."FeeHeads" WHERE "Id" = feehead_delete.Id AND "TenantId" = feehead_delete.TenantId;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.feehead_list()
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "Name" varchar(120), "Code" varchar(40), "Active" boolean,
               "IsSystem" boolean, "IsTransportFeeHead" boolean, "Description" text)
LANGUAGE sql
AS $$
    SELECT "Id", "TenantId", "Name", "Code", "Active", "IsSystem", "IsTransportFeeHead", "Description"
    FROM "dbo"."FeeHeads"
    ORDER BY "Name";
$$;

CREATE OR REPLACE FUNCTION dbo.feehead_update(
    Id uuid, TenantId uuid, Name varchar(120) DEFAULT NULL, Code varchar(40) DEFAULT NULL,
    CodeSpecified boolean DEFAULT false, Active boolean DEFAULT NULL, IsTransportFeeHead boolean DEFAULT NULL,
    Description text DEFAULT NULL, DescriptionSpecified boolean DEFAULT false
)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "Name" varchar(120), "Code" varchar(40), "Active" boolean,
               "IsSystem" boolean, "IsTransportFeeHead" boolean, "Description" text)
LANGUAGE sql
AS $$
    UPDATE "dbo"."FeeHeads"
    SET "Name" = COALESCE(feehead_update.Name, "Name"),
        "Code" = CASE WHEN feehead_update.CodeSpecified THEN feehead_update.Code ELSE "Code" END,
        "Active" = COALESCE(feehead_update.Active, "Active"),
        "IsTransportFeeHead" = COALESCE(feehead_update.IsTransportFeeHead, "IsTransportFeeHead"),
        "Description" = CASE WHEN feehead_update.DescriptionSpecified THEN feehead_update.Description ELSE "Description" END
    WHERE "Id" = feehead_update.Id AND "TenantId" = feehead_update.TenantId;

    SELECT "Id", "TenantId", "Name", "Code", "Active", "IsSystem", "IsTransportFeeHead", "Description"
    FROM "dbo"."FeeHeads" WHERE "Id" = feehead_update.Id AND "TenantId" = feehead_update.TenantId;
$$;

-- ============================================================
-- FeeInvoice
-- ============================================================

-- DueDate is timestamptz, not date: the C# DateTime? this comes from binds as timestamptz via
-- Npgsql, and Postgres won't implicitly resolve a timestamptz argument against a `date` parameter
-- for function-overload matching (same reasoning as 09_auth_procs.sql's "json"/text parameters).
CREATE OR REPLACE FUNCTION dbo.feeinvoice_create(
    TenantId uuid, StudentId uuid, Period varchar(60), DueDate timestamptz, Amount numeric(18,2)
)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "StudentId" uuid, "Period" varchar(60), "DueDate" date,
               "Amount" numeric(18,2), "Status" varchar(10), "PaidOn" date, "Method" varchar(40))
LANGUAGE sql
AS $$
    INSERT INTO "dbo"."FeeInvoices" ("Id", "TenantId", "StudentId", "Period", "DueDate", "Amount")
    VALUES (gen_random_uuid(), TenantId, StudentId, Period, DueDate::date, COALESCE(Amount, 0))
    RETURNING "Id", "TenantId", "StudentId", "Period", "DueDate", "Amount", "Status", "PaidOn", "Method";
$$;

CREATE OR REPLACE FUNCTION dbo.feeinvoice_markpaid(Id uuid, Method varchar(40))
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "StudentId" uuid, "Period" varchar(60), "DueDate" date,
               "Amount" numeric(18,2), "Status" varchar(10), "PaidOn" date, "Method" varchar(40))
LANGUAGE sql
AS $$
    UPDATE "dbo"."FeeInvoices" SET "Status" = 'paid', "PaidOn" = now()::date, "Method" = feeinvoice_markpaid.Method
    WHERE "Id" = feeinvoice_markpaid.Id AND "Status" <> 'paid';

    SELECT "Id", "TenantId", "StudentId", "Period", "DueDate", "Amount", "Status", "PaidOn", "Method"
    FROM "dbo"."FeeInvoices" WHERE "Id" = feeinvoice_markpaid.Id;
$$;

-- ============================================================
-- FeePayment_Create
-- BEGIN TRY/CATCH around a duplicate-IdempotencyKey INSERT -> nested BEGIN...EXCEPTION WHEN
-- unique_violation, same pattern as 11_sis_procs.sql's Student_EnsureLogin/Parent_EnsureLogin.
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.feepayment_create(
    TenantId uuid, StudentId uuid, StudentName varchar(200), ClassLabel varchar(40),
    FeeType varchar(20), Amount numeric(18,2), Method varchar(40), Ref varchar(80),
    InvoiceId uuid DEFAULT NULL, HeadId varchar(64) DEFAULT NULL, IdempotencyKey uuid DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "StudentId" uuid, "StudentName" varchar(200), "ClassLabel" varchar(40),
    "FeeType" varchar(20), "Amount" numeric(18,2), "Method" varchar(40), "Ref" varchar(80),
    "Date" date, "InvoiceId" uuid, "HeadId" varchar(64), "WasCreated" boolean
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_existing_id uuid;
    v_id uuid;
BEGIN
    IF feepayment_create.IdempotencyKey IS NOT NULL THEN
        SELECT p."Id" INTO v_existing_id FROM "dbo"."FeePayments" p
        WHERE p."TenantId" = feepayment_create.TenantId AND p."IdempotencyKey" = feepayment_create.IdempotencyKey
        LIMIT 1;

        IF v_existing_id IS NOT NULL THEN
            RETURN QUERY
            SELECT p."Id", p."TenantId", p."StudentId", p."StudentName", p."ClassLabel", p."FeeType", p."Amount",
                   p."Method", p."Ref", p."Date", p."InvoiceId", p."HeadId", false
            FROM "dbo"."FeePayments" p WHERE p."Id" = v_existing_id;
            RETURN;
        END IF;
    END IF;

    v_id := gen_random_uuid();
    BEGIN
        INSERT INTO "dbo"."FeePayments" ("Id", "TenantId", "StudentId", "StudentName", "ClassLabel", "FeeType",
            "Amount", "Method", "Ref", "Date", "InvoiceId", "HeadId", "IdempotencyKey", "CreatedAt")
        VALUES (v_id, feepayment_create.TenantId, feepayment_create.StudentId, feepayment_create.StudentName,
            feepayment_create.ClassLabel, COALESCE(feepayment_create.FeeType, 'academic'),
            COALESCE(feepayment_create.Amount, 0), feepayment_create.Method, feepayment_create.Ref,
            now()::date, feepayment_create.InvoiceId, feepayment_create.HeadId, feepayment_create.IdempotencyKey, now());
    EXCEPTION WHEN unique_violation THEN
        IF feepayment_create.IdempotencyKey IS NULL THEN RAISE; END IF;
        RETURN QUERY
        SELECT p."Id", p."TenantId", p."StudentId", p."StudentName", p."ClassLabel", p."FeeType", p."Amount",
               p."Method", p."Ref", p."Date", p."InvoiceId", p."HeadId", false
        FROM "dbo"."FeePayments" p
        WHERE p."TenantId" = feepayment_create.TenantId AND p."IdempotencyKey" = feepayment_create.IdempotencyKey;
        RETURN;
    END;

    RETURN QUERY
    SELECT p."Id", p."TenantId", p."StudentId", p."StudentName", p."ClassLabel", p."FeeType", p."Amount",
           p."Method", p."Ref", p."Date", p."InvoiceId", p."HeadId", true
    FROM "dbo"."FeePayments" p WHERE p."Id" = v_id;
END;
$$;

-- ============================================================
-- FeeStructure
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.feestructure_delete(TenantId uuid, Id uuid)
RETURNS TABLE ("Deleted" boolean, "Reason" varchar(20))
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_status varchar(20);
BEGIN
    SELECT s."Status" INTO v_status FROM "dbo"."FeeStructures" s
    WHERE s."Id" = feestructure_delete.Id AND s."TenantId" = feestructure_delete.TenantId;

    IF v_status IS NULL THEN
        RETURN QUERY SELECT false, 'not_found'::varchar(20);
        RETURN;
    END IF;
    IF lower(v_status) = 'active' THEN
        RETURN QUERY SELECT false, 'is_active'::varchar(20);
        RETURN;
    END IF;

    DELETE FROM "dbo"."FeeStructures" WHERE "Id" = feestructure_delete.Id AND "TenantId" = feestructure_delete.TenantId;
    RETURN QUERY SELECT true, NULL::varchar(20);
END;
$$;

CREATE OR REPLACE FUNCTION dbo.feestructure_get()
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Name" varchar(200), "AcademicYear" varchar(20), "ClassGrade" varchar(40),
    "Section" varchar(20), "Currency" varchar(10), "EffectiveFrom" date, "EffectiveTo" date,
    "Status" varchar(20), "Description" text, "AmountsJson" text, "CreatedAt" timestamptz
)
LANGUAGE sql
AS $$
    SELECT "Id", "TenantId", "Name", "AcademicYear", "ClassGrade", "Section", "Currency",
           "EffectiveFrom", "EffectiveTo", "Status", "Description", "AmountsJson", "CreatedAt"
    FROM "dbo"."FeeStructures"
    ORDER BY
        CASE WHEN lower("Status") = 'active' THEN 0 ELSE 1 END,
        "CreatedAt" DESC,
        "Id" DESC
    LIMIT 1;
$$;

CREATE OR REPLACE FUNCTION dbo.feestructure_list()
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Name" varchar(200), "AcademicYear" varchar(20), "ClassGrade" varchar(40),
    "Section" varchar(20), "Currency" varchar(10), "EffectiveFrom" date, "EffectiveTo" date,
    "Status" varchar(20), "Description" text, "CreatedAt" timestamptz, "AmountsJson" text
)
LANGUAGE sql
AS $$
    SELECT "Id", "TenantId", "Name", "AcademicYear", "ClassGrade", "Section", "Currency",
           "EffectiveFrom", "EffectiveTo", "Status", "Description", "CreatedAt", "AmountsJson"
    FROM "dbo"."FeeStructures"
    ORDER BY "CreatedAt" DESC, "Id" DESC;
$$;

CREATE OR REPLACE FUNCTION dbo.feestructure_publish(TenantId uuid, Id uuid)
RETURNS TABLE ("Found" boolean, "Id" uuid)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM "dbo"."FeeStructures" WHERE "Id" = feestructure_publish.Id AND "TenantId" = feestructure_publish.TenantId
    ) THEN
        RETURN QUERY SELECT false, NULL::uuid;
        RETURN;
    END IF;

    -- Publishing this version never retires any other version - many can be Published at once.
    UPDATE "dbo"."FeeStructures" SET "Status" = 'active'
    WHERE "Id" = feestructure_publish.Id AND "TenantId" = feestructure_publish.TenantId;

    RETURN QUERY
    SELECT true, s."Id" FROM "dbo"."FeeStructures" s
    WHERE s."Id" = feestructure_publish.Id AND s."TenantId" = feestructure_publish.TenantId;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.feestructure_unpublish(TenantId uuid, Id uuid)
RETURNS TABLE ("Found" boolean, "Id" uuid)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM "dbo"."FeeStructures" WHERE "Id" = feestructure_unpublish.Id AND "TenantId" = feestructure_unpublish.TenantId
    ) THEN
        RETURN QUERY SELECT false, NULL::uuid;
        RETURN;
    END IF;

    UPDATE "dbo"."FeeStructures" SET "Status" = 'inactive'
    WHERE "Id" = feestructure_unpublish.Id AND "TenantId" = feestructure_unpublish.TenantId;

    RETURN QUERY
    SELECT true, s."Id" FROM "dbo"."FeeStructures" s
    WHERE s."Id" = feestructure_unpublish.Id AND s."TenantId" = feestructure_unpublish.TenantId;
END;
$$;

-- EffectiveFrom/EffectiveTo are timestamptz, not date, for the same reason as
-- feeinvoice_create.DueDate above (UpsertFeeStructureRequest converts its DateOnly? fields to
-- DateTime before the call site sends them).
CREATE OR REPLACE FUNCTION dbo.feestructure_upsert(
    TenantId uuid, Name varchar(200), AcademicYear varchar(20), Currency varchar(10),
    EffectiveFrom timestamptz, Status varchar(20), AmountsJson text,
    Id uuid DEFAULT NULL, ClassGrade varchar(40) DEFAULT NULL, Section varchar(20) DEFAULT NULL,
    EffectiveTo timestamptz DEFAULT NULL, Description text DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Name" varchar(200), "AcademicYear" varchar(20), "ClassGrade" varchar(40),
    "Section" varchar(20), "Currency" varchar(10), "EffectiveFrom" date, "EffectiveTo" date,
    "Status" varchar(20), "Description" text, "AmountsJson" text, "CreatedAt" timestamptz
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_existing_status varchar(20);
    v_target_id uuid;
BEGIN
    IF feestructure_upsert.Id IS NOT NULL THEN
        SELECT s."Status" INTO v_existing_status FROM "dbo"."FeeStructures" s
        WHERE s."Id" = feestructure_upsert.Id AND s."TenantId" = feestructure_upsert.TenantId;
    END IF;

    IF v_existing_status IS NOT NULL AND lower(v_existing_status) <> 'active' THEN
        -- Editing a draft (never the currently-published version) - update it in place.
        -- Publishing/unpublishing here never touches any other version's Status.
        UPDATE "dbo"."FeeStructures"
        SET "Name" = feestructure_upsert.Name, "AcademicYear" = feestructure_upsert.AcademicYear,
            "ClassGrade" = feestructure_upsert.ClassGrade, "Section" = feestructure_upsert.Section,
            "Currency" = feestructure_upsert.Currency, "EffectiveFrom" = feestructure_upsert.EffectiveFrom::date,
            "EffectiveTo" = feestructure_upsert.EffectiveTo::date, "Status" = feestructure_upsert.Status,
            "Description" = feestructure_upsert.Description, "AmountsJson" = feestructure_upsert.AmountsJson,
            "CreatedAt" = now()
        WHERE "Id" = feestructure_upsert.Id AND "TenantId" = feestructure_upsert.TenantId;

        RETURN QUERY
        SELECT s."Id", s."TenantId", s."Name", s."AcademicYear", s."ClassGrade", s."Section", s."Currency",
               s."EffectiveFrom", s."EffectiveTo", s."Status", s."Description", s."AmountsJson", s."CreatedAt"
        FROM "dbo"."FeeStructures" s WHERE s."Id" = feestructure_upsert.Id;
        RETURN;
    END IF;

    -- No Id, or Id names the currently-published version: always insert a new, immutable
    -- version. Before inserting, retire any other row with the exact same identity that is
    -- still active - the same conceptual fee structure never has more than one active row.
    -- Rows with a different identity (a genuinely different fee structure) are untouched, so
    -- independent multi-publish keeps working.
    UPDATE "dbo"."FeeStructures"
    SET "Status" = 'inactive'
    WHERE "TenantId" = feestructure_upsert.TenantId
      AND lower("Status") = 'active'
      AND "Name" = feestructure_upsert.Name
      AND "AcademicYear" = feestructure_upsert.AcademicYear
      AND COALESCE("ClassGrade", '') = COALESCE(feestructure_upsert.ClassGrade, '')
      AND COALESCE("Section", '') = COALESCE(feestructure_upsert.Section, '')
      AND (feestructure_upsert.Id IS NULL OR "Id" <> feestructure_upsert.Id);

    v_target_id := gen_random_uuid();

    INSERT INTO "dbo"."FeeStructures" (
        "Id", "TenantId", "Name", "AcademicYear", "ClassGrade", "Section", "Currency",
        "EffectiveFrom", "EffectiveTo", "Status", "Description", "AmountsJson", "CreatedAt")
    VALUES (
        v_target_id, feestructure_upsert.TenantId, feestructure_upsert.Name, feestructure_upsert.AcademicYear,
        feestructure_upsert.ClassGrade, feestructure_upsert.Section, feestructure_upsert.Currency,
        feestructure_upsert.EffectiveFrom::date, feestructure_upsert.EffectiveTo::date, feestructure_upsert.Status,
        feestructure_upsert.Description, feestructure_upsert.AmountsJson, now());

    RETURN QUERY
    SELECT s."Id", s."TenantId", s."Name", s."AcademicYear", s."ClassGrade", s."Section", s."Currency",
           s."EffectiveFrom", s."EffectiveTo", s."Status", s."Description", s."AmountsJson", s."CreatedAt"
    FROM "dbo"."FeeStructures" s WHERE s."Id" = v_target_id;
END;
$$;
