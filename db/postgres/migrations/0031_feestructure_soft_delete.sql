-- Fee structure versions: soft-delete (archive) + "never used" delete gate.
--
-- Background: there is no link anywhere in the schema from an invoice/payment back to the
-- FeeStructures version it was billed from (FeeInvoices/FeeInvoiceLines/FeePayments have no
-- FeeStructureId). So "has this version been billed against?" can't be answered directly.
-- Publishing is what makes a version billable, so we treat *ever published* as *used*: a
-- version that was never published is the only thing safe to remove.
--
-- Changes:
--   1. Add FeeStructures."PublishedAt" — stamped on first publish, never cleared on unpublish.
--   2. feestructure_publish stamps PublishedAt (COALESCE, so re-publish keeps the first time).
--   3. feestructure_delete now SOFT-deletes (Status='archived') and refuses (Reason='in_use')
--      any version that was ever published, instead of hard-DELETE + refusing only 'active'.
--   4. feestructure_list / feestructure_get hide archived rows and expose PublishedAt.

-- 1. Track first-publish time. Backfill: anything currently Published has obviously been
--    published at least once. (Previously-published-then-unpublished versions kept no history,
--    so they read as never-published here; acceptable one-time artifact of the missing column.)
ALTER TABLE "dbo"."FeeStructures" ADD COLUMN IF NOT EXISTS "PublishedAt" timestamptz;
UPDATE "dbo"."FeeStructures"
   SET "PublishedAt" = COALESCE("PublishedAt", "CreatedAt")
 WHERE lower("Status") = 'active' AND "PublishedAt" IS NULL;

-- 2. Publish: stamp PublishedAt the first time only.
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
    UPDATE "dbo"."FeeStructures"
       SET "Status" = 'active', "PublishedAt" = COALESCE("PublishedAt", now())
     WHERE "Id" = feestructure_publish.Id AND "TenantId" = feestructure_publish.TenantId;

    RETURN QUERY
    SELECT true, s."Id" FROM "dbo"."FeeStructures" s
    WHERE s."Id" = feestructure_publish.Id AND s."TenantId" = feestructure_publish.TenantId;
END;
$$;

-- 3. Delete: soft-delete (archive) a version that was never published; refuse anything that was.
CREATE OR REPLACE FUNCTION dbo.feestructure_delete(TenantId uuid, Id uuid)
RETURNS TABLE ("Deleted" boolean, "Reason" varchar(20))
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_status      varchar(20);
    v_publishedat timestamptz;
BEGIN
    SELECT s."Status", s."PublishedAt" INTO v_status, v_publishedat
      FROM "dbo"."FeeStructures" s
     WHERE s."Id" = feestructure_delete.Id AND s."TenantId" = feestructure_delete.TenantId;

    -- Missing, or already archived, reads as not-found.
    IF v_status IS NULL OR lower(v_status) = 'archived' THEN
        RETURN QUERY SELECT false, 'not_found'::varchar(20);
        RETURN;
    END IF;

    -- Currently published, or ever published (may have been billed): can't be deleted.
    -- (A row that is 'active' always has PublishedAt stamped, but check both defensively.)
    IF lower(v_status) = 'active' OR v_publishedat IS NOT NULL THEN
        RETURN QUERY SELECT false, 'in_use'::varchar(20);
        RETURN;
    END IF;

    UPDATE "dbo"."FeeStructures" SET "Status" = 'archived'
     WHERE "Id" = feestructure_delete.Id AND "TenantId" = feestructure_delete.TenantId;
    RETURN QUERY SELECT true, NULL::varchar(20);
END;
$$;

-- 4a. List: hide archived rows, expose PublishedAt.
-- Adding a column to RETURNS TABLE changes the return type, which CREATE OR REPLACE can't do
-- (42P13), so drop first. DROP loses the grant, so re-GRANT EXECUTE to sms_app afterwards.
DROP FUNCTION IF EXISTS dbo.feestructure_list();
CREATE FUNCTION dbo.feestructure_list()
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Name" varchar(200), "AcademicYear" varchar(20), "ClassGrade" varchar(40),
    "Section" varchar(20), "Currency" varchar(10), "EffectiveFrom" date, "EffectiveTo" date,
    "Status" varchar(20), "Description" text, "CreatedAt" timestamptz, "AmountsJson" text, "PublishedAt" timestamptz
)
LANGUAGE sql
AS $$
    SELECT "Id", "TenantId", "Name", "AcademicYear", "ClassGrade", "Section", "Currency",
           "EffectiveFrom", "EffectiveTo", "Status", "Description", "CreatedAt", "AmountsJson", "PublishedAt"
    FROM "dbo"."FeeStructures"
    WHERE lower("Status") <> 'archived'
    ORDER BY "CreatedAt" DESC, "Id" DESC;
$$;
GRANT EXECUTE ON FUNCTION dbo.feestructure_list() TO sms_app;

-- 4b. Get (single active-or-latest for the Structure tab): hide archived rows.
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
    WHERE lower("Status") <> 'archived'
    ORDER BY
        CASE WHEN lower("Status") = 'active' THEN 0 ELSE 1 END,
        "CreatedAt" DESC,
        "Id" DESC
    LIMIT 1;
$$;

-- 5. Upsert: a version can also become Published straight from a save (Status='active') without
--    ever going through feestructure_publish, so stamp PublishedAt here too whenever the written
--    row is active. Everything else is copied verbatim from 12_finance_procs.sql.
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
        UPDATE "dbo"."FeeStructures"
        SET "Name" = feestructure_upsert.Name, "AcademicYear" = feestructure_upsert.AcademicYear,
            "ClassGrade" = feestructure_upsert.ClassGrade, "Section" = feestructure_upsert.Section,
            "Currency" = feestructure_upsert.Currency, "EffectiveFrom" = feestructure_upsert.EffectiveFrom::date,
            "EffectiveTo" = feestructure_upsert.EffectiveTo::date, "Status" = feestructure_upsert.Status,
            "Description" = feestructure_upsert.Description, "AmountsJson" = feestructure_upsert.AmountsJson,
            "CreatedAt" = now(),
            -- Publishing a draft straight from an edit counts as first publish.
            "PublishedAt" = CASE WHEN lower(feestructure_upsert.Status) = 'active'
                                 THEN COALESCE("PublishedAt", now()) ELSE "PublishedAt" END
        WHERE "Id" = feestructure_upsert.Id AND "TenantId" = feestructure_upsert.TenantId;

        RETURN QUERY
        SELECT s."Id", s."TenantId", s."Name", s."AcademicYear", s."ClassGrade", s."Section", s."Currency",
               s."EffectiveFrom", s."EffectiveTo", s."Status", s."Description", s."AmountsJson", s."CreatedAt"
        FROM "dbo"."FeeStructures" s WHERE s."Id" = feestructure_upsert.Id;
        RETURN;
    END IF;

    -- No Id, or Id names the currently-published version: always insert a new, immutable version.
    -- Retire any other active row with the exact same identity first.
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
        "EffectiveFrom", "EffectiveTo", "Status", "Description", "AmountsJson", "CreatedAt", "PublishedAt")
    VALUES (
        v_target_id, feestructure_upsert.TenantId, feestructure_upsert.Name, feestructure_upsert.AcademicYear,
        feestructure_upsert.ClassGrade, feestructure_upsert.Section, feestructure_upsert.Currency,
        feestructure_upsert.EffectiveFrom::date, feestructure_upsert.EffectiveTo::date, feestructure_upsert.Status,
        feestructure_upsert.Description, feestructure_upsert.AmountsJson, now(),
        -- Saved directly as Published => stamp first-publish time now.
        CASE WHEN lower(feestructure_upsert.Status) = 'active' THEN now() ELSE NULL END);

    RETURN QUERY
    SELECT s."Id", s."TenantId", s."Name", s."AcademicYear", s."ClassGrade", s."Section", s."Currency",
           s."EffectiveFrom", s."EffectiveTo", s."Status", s."Description", s."AmountsJson", s."CreatedAt"
    FROM "dbo"."FeeStructures" s WHERE s."Id" = v_target_id;
END;
$$;
