-- 0008: let dbo.task_complete store an optional remark when a task is completed. The Tasks table
-- already has a "Remarks" column; the staff app now submits a remark on completion. COALESCE keeps
-- any existing remark when none is passed (so other callers are unaffected).

CREATE OR REPLACE FUNCTION dbo.task_complete(Id uuid, CompletedByUserId uuid, Remarks varchar(2000) DEFAULT NULL)
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
    UPDATE "dbo"."Tasks"
    SET "Status" = 'completed',
        "CompletedByUserId" = task_complete.CompletedByUserId,
        "CompletedAt" = now(),
        "Remarks" = COALESCE(task_complete.Remarks, "Remarks")
    WHERE "Id" = task_complete.Id;

    RETURN QUERY
    SELECT "Id", "TenantId", "Title", "Detail", "Category", "AssignedToUserId", "AssignedToRoleKey", "Priority", "Status",
           "DueDate", "Remarks", "PhotoUrl", "CreatedByUserId", "CompletedByUserId", "CreatedAt", "CompletedAt"
    FROM "dbo"."Tasks" WHERE "Id" = task_complete.Id;
END;
$$;
