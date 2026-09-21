-- Remaining Catre dashboard/team/audit procs: PL/pgSQL conversions.
-- Source: full OBJECT_DEFINITION() extracted read-only from the live SQL Server Sms database on
-- 2026-09-21, cross-checked against sqlserver-object-inventory.csv.
--
-- NOTE on categorization: the CSV inventory lists Team_Invite/Team_Update/TeamDocument_Add under
-- a "Comms" family label and Dashboard_CatreOverview/Report_Revenue under "Reporting", but the
-- actual C# call sites for all 6 procs here live in src/Sms.Modules.Tenancy/Data/
-- CatreOpsRepositories.cs and DashboardRepository.cs (Tenancy module) -- see the "CSV family name
-- doesn't always match the calling module" bug class. Converting these based on where the C# code
-- actually calls them, not the CSV label.
--
-- Team_Invite/Team_Update/TeamDocument_Add/Audit_Insert are called via QuerySingleProcAsync<T> --
-- ordinary single-resultset create/update procs, same pattern as every prior module.
--
-- Dashboard_CatreOverview and Report_Revenue are called via Dapper's QueryMultipleAsync against a
-- single stored-proc call, reading 5 and 3 result sets respectively in a fixed order. PL/pgSQL
-- functions can only return one result set each, so each original proc is split here into N
-- separate single-resultset functions (one per original SELECT), and the C# call site
-- (DashboardRepository.OverviewAsync / ReportRepository.RevenueAsync) is rewritten to call each
-- function separately instead of one QueryMultipleAsync round-trip. This trades one round-trip
-- for N cheap ones; no cursor/refcursor machinery needed.

CREATE OR REPLACE FUNCTION dbo.team_invite(
    Name varchar(200), Email varchar(256), Role varchar(20),
    EmployeeId varchar(40) DEFAULT NULL, PhotoUrl text DEFAULT NULL, Phone varchar(40) DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "Name" varchar(200), "Email" varchar(256), "Role" varchar(20), "Status" varchar(20),
    "LastLogin" timestamptz, "Joined" timestamptz, "EmployeeId" varchar(40), "PhotoUrl" text, "Phone" varchar(40)
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_id uuid := gen_random_uuid();
BEGIN
    INSERT INTO "dbo"."TeamMembers" ("Id", "Name", "Email", "Role", "Status", "EmployeeId", "PhotoUrl", "Phone")
    VALUES (v_id, Name, Email, Role, 'active', EmployeeId, PhotoUrl, Phone);

    RETURN QUERY
    SELECT "Id", "Name", "Email", "Role", "Status", "LastLogin", "Joined", "EmployeeId", "PhotoUrl", "Phone"
    FROM "dbo"."TeamMembers" WHERE "Id" = v_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.team_update(
    Id uuid, Role varchar(20) DEFAULT NULL, Status varchar(20) DEFAULT NULL, Name varchar(200) DEFAULT NULL,
    EmployeeId varchar(40) DEFAULT NULL, PhotoUrl text DEFAULT NULL, Phone varchar(40) DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "Name" varchar(200), "Email" varchar(256), "Role" varchar(20), "Status" varchar(20),
    "LastLogin" timestamptz, "Joined" timestamptz, "EmployeeId" varchar(40), "PhotoUrl" text, "Phone" varchar(40)
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE "dbo"."TeamMembers" SET
        "Role" = COALESCE(team_update.Role, "Role"),
        "Status" = COALESCE(team_update.Status, "Status"),
        "Name" = COALESCE(team_update.Name, "Name"),
        "EmployeeId" = COALESCE(team_update.EmployeeId, "EmployeeId"),
        "PhotoUrl" = COALESCE(team_update.PhotoUrl, "PhotoUrl"),
        "Phone" = COALESCE(team_update.Phone, "Phone")
    WHERE "Id" = team_update.Id;

    RETURN QUERY
    SELECT "Id", "Name", "Email", "Role", "Status", "LastLogin", "Joined", "EmployeeId", "PhotoUrl", "Phone"
    FROM "dbo"."TeamMembers" WHERE "Id" = team_update.Id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.teamdocument_add(
    TeamMemberId uuid, Label varchar(120), FileName varchar(260),
    ContentType varchar(120), SizeBytes int, Content text
)
RETURNS TABLE (
    "Id" uuid, "TeamMemberId" uuid, "Label" varchar(120), "FileName" varchar(260),
    "ContentType" varchar(120), "SizeBytes" int, "Created" timestamptz
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_id uuid := gen_random_uuid();
BEGIN
    INSERT INTO "dbo"."TeamDocuments" ("Id", "TeamMemberId", "Label", "FileName", "ContentType", "SizeBytes", "Content")
    VALUES (v_id, TeamMemberId, Label, FileName, ContentType, SizeBytes, Content);

    RETURN QUERY
    SELECT "Id", "TeamMemberId", "Label", "FileName", "ContentType", "SizeBytes", "CreatedAt"
    FROM "dbo"."TeamDocuments" WHERE "Id" = v_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.audit_insert(
    Action varchar(200), ActorId uuid DEFAULT NULL, ActorName varchar(200) DEFAULT NULL,
    Role varchar(80) DEFAULT NULL, Target varchar(200) DEFAULT NULL, Kind varchar(40) DEFAULT NULL,
    TenantId uuid DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "ActorId" uuid, "ActorName" varchar(200), "Role" varchar(80),
    "Action" varchar(200), "Target" varchar(200), "Kind" varchar(40), "Time" timestamptz
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_id uuid := gen_random_uuid();
BEGIN
    INSERT INTO "dbo"."AuditLog" ("Id", "ActorId", "ActorName", "Role", "Action", "Target", "Kind", "TenantId", "At")
    VALUES (v_id, ActorId, ActorName, Role, Action, Target, Kind, TenantId, now() AT TIME ZONE 'UTC');

    RETURN QUERY
    SELECT "Id", "ActorId", "ActorName", "Role", "Action", "Target", "Kind", "At"
    FROM "dbo"."AuditLog" WHERE "Id" = v_id;
END;
$$;

-- ===== Dashboard_CatreOverview split into 5 functions (one per original result set) =====

CREATE OR REPLACE FUNCTION dbo.dashboard_catreoverview_headline()
RETURNS TABLE (
    "Total" int, "Active" int, "Trial" int, "Suspended" int, "Cancelled" int,
    "Mrr" numeric(18,2), "TrialsEnding" int, "ChurnPct" numeric(9,2)
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_currCancel int;
    v_prevActive int;
    v_prevCancel int;
    v_newChurn int;
    v_churnPct numeric(9,2);
BEGIN
    SELECT "CancelledClients" INTO v_currCancel FROM "dbo"."PlatformMetricsSnapshot" ORDER BY "Month" DESC LIMIT 1;

    SELECT x."ActiveClients", x."CancelledClients" INTO v_prevActive, v_prevCancel
    FROM (
        SELECT "ActiveClients", "CancelledClients", ROW_NUMBER() OVER (ORDER BY "Month" DESC) AS rn
        FROM "dbo"."PlatformMetricsSnapshot"
    ) x WHERE x.rn = 2;

    v_newChurn := COALESCE(v_currCancel, 0) - COALESCE(v_prevCancel, 0);
    v_churnPct := CASE WHEN COALESCE(v_prevActive, 0) > 0
        THEN CAST(v_newChurn AS numeric(9,2)) / v_prevActive * 100 ELSE 0 END;

    RETURN QUERY
    SELECT
        CAST(COUNT(*) AS int) AS "Total",
        CAST(SUM(CASE WHEN t."Status" = 'active'    THEN 1 ELSE 0 END) AS int) AS "Active",
        CAST(SUM(CASE WHEN t."Status" = 'trial'     THEN 1 ELSE 0 END) AS int) AS "Trial",
        CAST(SUM(CASE WHEN t."Status" = 'suspended' THEN 1 ELSE 0 END) AS int) AS "Suspended",
        CAST(SUM(CASE WHEN t."Status" = 'cancelled' THEN 1 ELSE 0 END) AS int) AS "Cancelled",
        COALESCE(SUM(CASE WHEN t."Status" = 'active' THEN t."Mrr" ELSE 0 END), 0) AS "Mrr",
        CAST(SUM(CASE WHEN t."Status" = 'trial' THEN 1 ELSE 0 END) AS int) AS "TrialsEnding",
        v_churnPct AS "ChurnPct"
    FROM "dbo"."Tenants" t;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.dashboard_catreoverview_planmix()
RETURNS TABLE ("Label" varchar(20), "Value" int)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT t."Tier", CAST(COUNT(*) AS int)
    FROM "dbo"."Tenants" t WHERE t."Tier" IS NOT NULL GROUP BY t."Tier";
END;
$$;

CREATE OR REPLACE FUNCTION dbo.dashboard_catreoverview_recentactivity()
RETURNS TABLE ("Actor" varchar(200), "Action" varchar(200), "Target" varchar(256), "Kind" varchar(64), "At" timestamptz)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT a."ActorName", a."Action", a."Target", a."Kind", a."At"
    FROM "dbo"."AuditLog" a ORDER BY a."At" DESC LIMIT 20;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.dashboard_catreoverview_usagealerts()
RETURNS TABLE ("Tenant" varchar(200), "Metric" text, "Used" int, "Limit" int, "Pct" int)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT t."Name", 'students'::text, t."StudentsCount", t."LimitsStudents",
           CAST(t."StudentsCount" * 100 / NULLIF(t."LimitsStudents", 0) AS int)
    FROM "dbo"."Tenants" t
    WHERE t."LimitsStudents" > 0 AND t."StudentsCount" * 100 >= t."LimitsStudents" * 80
    UNION ALL
    SELECT t."Name", 'storage'::text, CAST(t."StorageGb" AS int), t."LimitsStorageGb",
           CAST(t."StorageGb" * 100 / NULLIF(t."LimitsStorageGb", 0) AS int)
    FROM "dbo"."Tenants" t
    WHERE t."LimitsStorageGb" > 0 AND t."StorageGb" * 100 >= t."LimitsStorageGb" * 80;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.dashboard_catreoverview_months()
RETURNS TABLE ("Label" text, "Mrr" numeric(18,2), "Signups" int)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    WITH months AS (
        SELECT (date_trunc('month', (now() AT TIME ZONE 'UTC') + (n || ' months')::interval))::date AS m
        FROM unnest(ARRAY[-5,-4,-3,-2,-1,0]) AS n
    )
    SELECT
        to_char(months.m, 'Mon'),
        COALESCE(s."Mrr", 0),
        CAST((SELECT COUNT(*) FROM "dbo"."Subscriptions" sub
              WHERE sub."StartedAt" >= months.m AND sub."StartedAt" < (months.m + interval '1 month')) AS int)
    FROM months
    LEFT JOIN "dbo"."PlatformMetricsSnapshot" s ON s."Month" = months.m
    ORDER BY months.m;
END;
$$;

-- ===== Report_Revenue split into 3 functions (one per original result set) =====

CREATE OR REPLACE FUNCTION dbo.report_revenue_headline()
RETURNS TABLE ("TotalMrr" numeric(18,2), "ActiveCount" int, "NetGrowth" int, "GrossChurnPct" numeric(9,2))
LANGUAGE plpgsql
AS $$
DECLARE
    v_currActive int;
    v_currCancel int;
    v_prevActive int;
    v_prevCancel int;
    v_netGrowth int;
    v_newChurn int;
    v_churnPct numeric(9,2);
BEGIN
    SELECT "ActiveClients", "CancelledClients" INTO v_currActive, v_currCancel
    FROM "dbo"."PlatformMetricsSnapshot" ORDER BY "Month" DESC LIMIT 1;

    SELECT x."ActiveClients", x."CancelledClients" INTO v_prevActive, v_prevCancel
    FROM (
        SELECT "ActiveClients", "CancelledClients", ROW_NUMBER() OVER (ORDER BY "Month" DESC) AS rn
        FROM "dbo"."PlatformMetricsSnapshot"
    ) x WHERE x.rn = 2;

    v_netGrowth := COALESCE(v_currActive, 0) - COALESCE(v_prevActive, 0);
    v_newChurn := COALESCE(v_currCancel, 0) - COALESCE(v_prevCancel, 0);
    v_churnPct := CASE WHEN COALESCE(v_prevActive, 0) > 0
        THEN CAST(v_newChurn AS numeric(9,2)) / v_prevActive * 100 ELSE 0 END;

    RETURN QUERY
    SELECT
        COALESCE(SUM(CASE WHEN t."Status" = 'active' THEN t."Mrr" ELSE 0 END), 0) AS "TotalMrr",
        CAST(SUM(CASE WHEN t."Status" = 'active' THEN 1 ELSE 0 END) AS int) AS "ActiveCount",
        v_netGrowth AS "NetGrowth",
        v_churnPct AS "GrossChurnPct"
    FROM "dbo"."Tenants" t;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.report_revenue_perplan()
RETURNS TABLE ("PlanName" varchar(100), "Clients" int, "Mrr" numeric(18,2))
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT t."PlanName", CAST(COUNT(*) AS int), COALESCE(SUM(t."Mrr"), 0)
    FROM "dbo"."Tenants" t WHERE t."PlanName" IS NOT NULL
    GROUP BY t."PlanName" ORDER BY SUM(t."Mrr") DESC;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.report_revenue_months()
RETURNS TABLE ("Label" text, "Revenue" numeric(18,2))
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    WITH months AS (
        SELECT (date_trunc('month', (now() AT TIME ZONE 'UTC') + (n || ' months')::interval))::date AS m
        FROM unnest(ARRAY[-5,-4,-3,-2,-1,0]) AS n
    )
    SELECT
        to_char(months.m, 'Mon'),
        COALESCE((SELECT SUM(inv."Amount") FROM "dbo"."Invoices" inv
                  WHERE inv."Status" = 'paid' AND inv."PaidOn" >= months.m
                    AND inv."PaidOn" < (months.m + interval '1 month')), 0)
    FROM months
    ORDER BY months.m;
END;
$$;

-- PlatformMetrics_UpsertCurrentMonth: called by Sms.Api's MetricsSnapshotWriter at startup
-- (fire-and-forget, swallows its own errors) to refresh dbo.PlatformMetricsSnapshot, which
-- feeds the Dashboard_CatreOverview/Report_Revenue functions above. Requires a UNIQUE
-- constraint on "Month" for ON CONFLICT (added to 04_tables.sql) -- the original MERGE had no
-- such constraint on SQL Server since MERGE doesn't need one.
CREATE OR REPLACE FUNCTION dbo.platformmetrics_upsertcurrentmonth()
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    v_month date := date_trunc('month', now() AT TIME ZONE 'UTC')::date;
    v_mrr numeric(18,2);
    v_active int;
    v_cancelled int;
    v_rows int;
BEGIN
    SELECT COALESCE(SUM(CASE WHEN "Status" = 'active' THEN "Mrr" ELSE 0 END), 0) INTO v_mrr FROM "dbo"."Tenants";
    SELECT COUNT(*) INTO v_active FROM "dbo"."Tenants" WHERE "Status" = 'active';
    SELECT COUNT(*) INTO v_cancelled FROM "dbo"."Tenants" WHERE "Status" = 'cancelled';

    INSERT INTO "dbo"."PlatformMetricsSnapshot" ("Month", "Mrr", "ActiveClients", "CancelledClients", "CreatedAt")
    VALUES (v_month, v_mrr, v_active, v_cancelled, now() AT TIME ZONE 'UTC')
    ON CONFLICT ("Month") DO UPDATE SET
        "Mrr" = EXCLUDED."Mrr", "ActiveClients" = EXCLUDED."ActiveClients", "CancelledClients" = EXCLUDED."CancelledClients";

    GET DIAGNOSTICS v_rows = ROW_COUNT;
    RETURN v_rows;
END;
$$;
