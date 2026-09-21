-- Tasks module: PL/pgSQL conversions of the 3 stored procedures in the Task family.
-- Source: full OBJECT_DEFINITION() extracted read-only from the live SQL Server Sms database on
-- 2026-09-21, cross-checked against sqlserver-object-inventory.csv.
--
-- Task_Create's Priority parameter has no default in the source but is declared after
-- AssignedToRoleKey (which does have one) -- reordered so Postgres's "defaults must trail" rule
-- holds; the call site uses named notation so this is invisible to callers, same as
-- 13_staffing_procs.sql's leave_create/vehicleinspection_upsert reordering.
--
-- All three are called via QuerySingleProcAsync<TaskResponse> (see 13_staffing_procs.sql's
-- header for the RETURNS int vs RETURNS TABLE note) and TaskResponse's secondary constructor
-- documents that the SELECT must stay at exactly these 16 columns (no joined *Name fields).

CREATE OR REPLACE FUNCTION dbo.task_create(
    TenantId uuid, CreatedByUserId uuid, Title varchar(200), Priority varchar(10),
    Detail varchar(2000) DEFAULT NULL, Category varchar(40) DEFAULT NULL,
    AssignedToUserId uuid DEFAULT NULL, AssignedToRoleKey varchar(20) DEFAULT NULL, DueDate date DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Title" varchar(200), "Detail" varchar(2000), "Category" varchar(40),
    "AssignedToUserId" uuid, "AssignedToRoleKey" varchar(20), "Priority" varchar(10), "Status" varchar(20),
    "DueDate" date, "Remarks" varchar(2000), "PhotoUrl" text, "CreatedByUserId" uuid, "CompletedByUserId" uuid,
    "CreatedAt" timestamptz, "CompletedAt" timestamptz
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_id uuid := gen_random_uuid();
BEGIN
    INSERT INTO "dbo"."Tasks"
        ("Id", "TenantId", "Title", "Detail", "Category", "AssignedToUserId", "AssignedToRoleKey", "Priority", "DueDate", "CreatedByUserId")
    VALUES (v_id, TenantId, Title, Detail, Category, AssignedToUserId, AssignedToRoleKey, Priority, DueDate, CreatedByUserId);

    RETURN QUERY
    SELECT "Id", "TenantId", "Title", "Detail", "Category", "AssignedToUserId", "AssignedToRoleKey", "Priority", "Status",
           "DueDate", "Remarks", "PhotoUrl", "CreatedByUserId", "CompletedByUserId", "CreatedAt", "CompletedAt"
    FROM "dbo"."Tasks" WHERE "Id" = v_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.task_complete(Id uuid, CompletedByUserId uuid)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Title" varchar(200), "Detail" varchar(2000), "Category" varchar(40),
    "AssignedToUserId" uuid, "AssignedToRoleKey" varchar(20), "Priority" varchar(10), "Status" varchar(20),
    "DueDate" date, "Remarks" varchar(2000), "PhotoUrl" text, "CreatedByUserId" uuid, "CompletedByUserId" uuid,
    "CreatedAt" timestamptz, "CompletedAt" timestamptz
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE "dbo"."Tasks" SET "Status" = 'completed', "CompletedByUserId" = task_complete.CompletedByUserId, "CompletedAt" = now()
    WHERE "Id" = task_complete.Id;

    RETURN QUERY
    SELECT "Id", "TenantId", "Title", "Detail", "Category", "AssignedToUserId", "AssignedToRoleKey", "Priority", "Status",
           "DueDate", "Remarks", "PhotoUrl", "CreatedByUserId", "CompletedByUserId", "CreatedAt", "CompletedAt"
    FROM "dbo"."Tasks" WHERE "Id" = task_complete.Id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.task_attachphoto(Id uuid, PhotoUrl text)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Title" varchar(200), "Detail" varchar(2000), "Category" varchar(40),
    "AssignedToUserId" uuid, "AssignedToRoleKey" varchar(20), "Priority" varchar(10), "Status" varchar(20),
    "DueDate" date, "Remarks" varchar(2000), "PhotoUrl" text, "CreatedByUserId" uuid, "CompletedByUserId" uuid,
    "CreatedAt" timestamptz, "CompletedAt" timestamptz
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE "dbo"."Tasks" SET "PhotoUrl" = task_attachphoto.PhotoUrl WHERE "Id" = task_attachphoto.Id;

    RETURN QUERY
    SELECT "Id", "TenantId", "Title", "Detail", "Category", "AssignedToUserId", "AssignedToRoleKey", "Priority", "Status",
           "DueDate", "Remarks", "PhotoUrl", "CreatedByUserId", "CompletedByUserId", "CreatedAt", "CompletedAt"
    FROM "dbo"."Tasks" WHERE "Id" = task_attachphoto.Id;
END;
$$;
