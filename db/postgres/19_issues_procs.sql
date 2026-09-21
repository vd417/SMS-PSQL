-- Issues module: PL/pgSQL conversions of the 3 stored procedures in the Issue family.
-- Source: full OBJECT_DEFINITION() extracted read-only from the live SQL Server Sms database
-- on 2026-09-21, cross-checked against sqlserver-object-inventory.csv.
--
-- All three are called via QuerySingleProcAsync<IssueResponse>/<IssueNoteResponse> (see
-- 13_staffing_procs.sql's header for the RETURNS int vs RETURNS TABLE note).

CREATE OR REPLACE FUNCTION dbo.issue_create(
    TenantId uuid, ReporterUserId uuid, Category varchar(20), Title varchar(200), Description varchar(2000),
    Priority varchar(20), VehicleId uuid DEFAULT NULL, RouteId uuid DEFAULT NULL, TripId uuid DEFAULT NULL,
    PhotoUrl text DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "ReporterUserId" uuid, "Category" varchar(20), "Title" varchar(200),
    "Description" varchar(2000), "Priority" varchar(20), "Status" varchar(20), "VehicleId" uuid, "RouteId" uuid,
    "TripId" uuid, "PhotoUrl" text, "CreatedAt" timestamptz, "UpdatedAt" timestamptz
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_id uuid := gen_random_uuid();
BEGIN
    INSERT INTO "dbo"."Issues"
        ("Id", "TenantId", "ReporterUserId", "Category", "Title", "Description", "Priority", "VehicleId", "RouteId", "TripId", "PhotoUrl")
    VALUES (v_id, TenantId, ReporterUserId, Category, Title, Description, Priority, VehicleId, RouteId, TripId, PhotoUrl);

    RETURN QUERY
    SELECT "Id", "TenantId", "ReporterUserId", "Category", "Title", "Description", "Priority", "Status",
           "VehicleId", "RouteId", "TripId", "PhotoUrl", "CreatedAt", "UpdatedAt"
    FROM "dbo"."Issues" WHERE "Id" = v_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.issue_update(Id uuid, Status varchar(20))
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "ReporterUserId" uuid, "Category" varchar(20), "Title" varchar(200),
    "Description" varchar(2000), "Priority" varchar(20), "Status" varchar(20), "VehicleId" uuid, "RouteId" uuid,
    "TripId" uuid, "PhotoUrl" text, "CreatedAt" timestamptz, "UpdatedAt" timestamptz
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE "dbo"."Issues" SET "Status" = issue_update.Status, "UpdatedAt" = now() AT TIME ZONE 'UTC'
    WHERE "Id" = issue_update.Id;

    RETURN QUERY
    SELECT "Id", "TenantId", "ReporterUserId", "Category", "Title", "Description", "Priority", "Status",
           "VehicleId", "RouteId", "TripId", "PhotoUrl", "CreatedAt", "UpdatedAt"
    FROM "dbo"."Issues" WHERE "Id" = issue_update.Id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.issuenote_add(TenantId uuid, IssueId uuid, AuthorUserId uuid, Note varchar(1000))
RETURNS TABLE ("Id" uuid, "IssueId" uuid, "AuthorUserId" uuid, "Note" varchar(1000), "CreatedAt" timestamptz)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_id uuid := gen_random_uuid();
BEGIN
    INSERT INTO "dbo"."IssueNotes" ("Id", "TenantId", "IssueId", "AuthorUserId", "Note")
    VALUES (v_id, TenantId, IssueId, AuthorUserId, Note);

    RETURN QUERY
    SELECT "Id", "IssueId", "AuthorUserId", "Note", "CreatedAt" FROM "dbo"."IssueNotes" WHERE "Id" = v_id;
END;
$$;
