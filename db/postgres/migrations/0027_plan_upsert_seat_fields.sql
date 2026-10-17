-- 0027: expose the plan-level seat/billing fields in the plan editor. Extends dbo.plan_upsert with
-- trailing IncludedStudents (flat-plan bundled seats), AllowedCycles (CSV of month,quarter,year) and
-- SeatAlertThreshold (default near-limit gap) params, persists them (columns added in 0023), and
-- returns them. Signature changes, so the old 19-arg function is dropped first.
-- Rollback: restore the 19-arg dbo.plan_upsert from db/postgres/10_tenancy_procs.sql and
--   DROP FUNCTION IF EXISTS dbo.plan_upsert(uuid,varchar,varchar,varchar,numeric,numeric,int,varchar,varchar,int,int,int,varchar,varchar,varchar,varchar,int,varchar,varchar,int,varchar,int);

DROP FUNCTION IF EXISTS dbo.plan_upsert;

CREATE OR REPLACE FUNCTION dbo.plan_upsert(
    Id uuid, Name varchar(100), Tier varchar(20), Pricing varchar(20),
    Price numeric(18,2), PerStudent numeric(18,2), MinStudents int, Period varchar(20),
    FeaturesCsv varchar(4000), LimitsStudents int, LimitsStaff int, LimitsStorageGb int,
    Visibility varchar(20), Audience varchar(20), Band varchar(100),
    OfferLabel varchar(100), OfferPct int, Color varchar(40), Description varchar(1000),
    IncludedStudents int DEFAULT NULL, AllowedCycles varchar(40) DEFAULT 'month,quarter,year',
    SeatAlertThreshold int DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "Name" varchar(100), "Tier" varchar(20), "Pricing" varchar(20), "Price" numeric(18,2),
    "PerStudent" numeric(18,2), "MinStudents" int, "Period" varchar(20), "FeaturesCsv" varchar(4000),
    "LimitsStudents" int, "LimitsStaff" int, "LimitsStorageGb" int,
    "Visibility" varchar(20), "Audience" varchar(20), "Band" varchar(100),
    "OfferLabel" varchar(100), "OfferPct" int, "Color" varchar(40), "Description" varchar(1000),
    "IncludedStudents" int, "AllowedCycles" varchar(40), "SeatAlertThreshold" int
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_id uuid := COALESCE(plan_upsert.Id, gen_random_uuid());
BEGIN
    INSERT INTO "dbo"."Plans" ("Id", "Name", "Tier", "Pricing", "Price", "PerStudent", "MinStudents", "Period", "FeaturesCsv",
        "LimitsStudents", "LimitsStaff", "LimitsStorageGb", "Visibility", "Audience", "Band",
        "OfferLabel", "OfferPct", "Color", "Description", "IncludedStudents", "AllowedCycles", "SeatAlertThreshold")
    VALUES (v_id, plan_upsert.Name, plan_upsert.Tier, plan_upsert.Pricing,
        plan_upsert.Price, plan_upsert.PerStudent, plan_upsert.MinStudents, plan_upsert.Period, plan_upsert.FeaturesCsv,
        plan_upsert.LimitsStudents, plan_upsert.LimitsStaff, plan_upsert.LimitsStorageGb,
        plan_upsert.Visibility, plan_upsert.Audience, plan_upsert.Band,
        plan_upsert.OfferLabel, plan_upsert.OfferPct, plan_upsert.Color, plan_upsert.Description,
        plan_upsert.IncludedStudents, COALESCE(plan_upsert.AllowedCycles, 'month,quarter,year'), plan_upsert.SeatAlertThreshold)
    ON CONFLICT ("Id") DO UPDATE SET
        "Name" = EXCLUDED."Name", "Tier" = EXCLUDED."Tier", "Pricing" = EXCLUDED."Pricing",
        "Price" = EXCLUDED."Price", "PerStudent" = EXCLUDED."PerStudent", "MinStudents" = EXCLUDED."MinStudents",
        "Period" = EXCLUDED."Period", "FeaturesCsv" = EXCLUDED."FeaturesCsv",
        "LimitsStudents" = EXCLUDED."LimitsStudents", "LimitsStaff" = EXCLUDED."LimitsStaff",
        "LimitsStorageGb" = EXCLUDED."LimitsStorageGb", "Visibility" = EXCLUDED."Visibility",
        "Audience" = EXCLUDED."Audience", "Band" = EXCLUDED."Band", "OfferLabel" = EXCLUDED."OfferLabel",
        "OfferPct" = EXCLUDED."OfferPct", "Color" = EXCLUDED."Color", "Description" = EXCLUDED."Description",
        "IncludedStudents" = EXCLUDED."IncludedStudents", "AllowedCycles" = EXCLUDED."AllowedCycles",
        "SeatAlertThreshold" = EXCLUDED."SeatAlertThreshold";

    RETURN QUERY
    SELECT p."Id", p."Name", p."Tier", p."Pricing", p."Price", p."PerStudent", p."MinStudents", p."Period", p."FeaturesCsv",
           p."LimitsStudents", p."LimitsStaff", p."LimitsStorageGb", p."Visibility", p."Audience", p."Band",
           p."OfferLabel", p."OfferPct", p."Color", p."Description",
           p."IncludedStudents", p."AllowedCycles", p."SeatAlertThreshold"
    FROM "dbo"."Plans" p WHERE p."Id" = v_id;
END;
$$;
