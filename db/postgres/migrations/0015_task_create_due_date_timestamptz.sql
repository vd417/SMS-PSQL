-- 0015: fix dbo.task_create's DueDate parameter type (date -> timestamptz).
--
-- A POST /v1/staff/tasks carrying a due_date returned 500. The C# DateTime? this comes from binds
-- via Npgsql as timestamptz, and Postgres won't resolve a timestamptz argument against a `date`
-- function parameter for overload matching (SqlState 42883, "function dbo.task_create(... duedate =>
-- timestamp with time zone) does not exist"). The baseline (db/postgres/16_tasks_procs.sql) shipped
-- DueDate as `date`, so existing databases hold the wrong overload. This recreates task_create with
-- DueDate timestamptz, casting to the `date` column on insert -- exactly as dbo.feeinvoice_create
-- already does (12_finance_procs.sql). A null due_date worked before only because an untyped NULL
-- matches any parameter; any real date failed.
--
-- The parameter type is part of a function's identity, so this is a new function, not a replace:
-- the old `date` overload is dropped first (otherwise an untyped-NULL call becomes ambiguous
-- between the two), and the baseline's schema-wide GRANT EXECUTE does not carry to the new function,
-- so it is re-granted below.
--
-- Rollback: DROP FUNCTION dbo.task_create(uuid,uuid,varchar,varchar,varchar,varchar,uuid,varchar,timestamptz);
-- then restore the original date-typed definition from 16_tasks_procs.sql and re-grant.

DROP FUNCTION IF EXISTS dbo.task_create(uuid, uuid, varchar, varchar, varchar, varchar, uuid, varchar, date);

CREATE OR REPLACE FUNCTION dbo.task_create(
    TenantId uuid, CreatedByUserId uuid, Title varchar(200), Priority varchar(10),
    Detail varchar(2000) DEFAULT NULL, Category varchar(40) DEFAULT NULL,
    AssignedToUserId uuid DEFAULT NULL, AssignedToRoleKey varchar(20) DEFAULT NULL, DueDate timestamptz DEFAULT NULL
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
    VALUES (v_id, TenantId, Title, Detail, Category, AssignedToUserId, AssignedToRoleKey, Priority, DueDate::date, CreatedByUserId);

    RETURN QUERY
    SELECT "Id", "TenantId", "Title", "Detail", "Category", "AssignedToUserId", "AssignedToRoleKey", "Priority", "Status",
           "DueDate", "Remarks", "PhotoUrl", "CreatedByUserId", "CompletedByUserId", "CreatedAt", "CompletedAt"
    FROM "dbo"."Tasks" WHERE "Id" = v_id;
END;
$$;

GRANT EXECUTE ON FUNCTION dbo.task_create(uuid, uuid, varchar, varchar, varchar, varchar, uuid, varchar, timestamptz) TO sms_app;
