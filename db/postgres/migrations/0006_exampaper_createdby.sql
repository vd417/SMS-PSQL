-- 0006: SD-5 exam paper ownership. dbo.ExamPapers has no owner column, so PATCH/DELETE
-- /v1/exam-papers/{id} cannot tell whether the caller is the creator; the approved rule is
-- creator-or-principal (matrix row EXM-05). Additive only: nullable column + a widened
-- exampaper_create (new trailing default-valued parameter only, so the function's return
-- shape is untouched and CREATE OR REPLACE FUNCTION applies cleanly).
-- Rollback: ALTER TABLE "dbo"."ExamPapers" DROP COLUMN "CreatedBy"; then CREATE OR REPLACE
-- FUNCTION dbo.exampaper_create back to its 15_academics_procs.sql definition (drop the
-- CreatedBy parameter and its use in the INSERT).

ALTER TABLE "dbo"."ExamPapers" ADD COLUMN "CreatedBy" uuid;

CREATE OR REPLACE FUNCTION dbo.exampaper_create(
    TenantId uuid, ExamId uuid, ClassId uuid, Name varchar(120), Subject varchar(80), SubjectId uuid,
    Date timestamptz, StartTime varchar(10), DurationMin int, MaxMarks int, Room varchar(40),
    Invigilator1 varchar(120), Invigilator2 varchar(120), Topics text DEFAULT NULL, CreatedBy uuid DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "ExamId" uuid, "ClassId" uuid, "Name" varchar(120), "Subject" varchar(80),
    "SubjectId" uuid, "Date" date, "StartTime" varchar(10), "DurationMin" int, "MaxMarks" int, "Room" varchar(40),
    "Invigilator1" varchar(120), "Invigilator2" varchar(120), "Status" varchar(20), "Topics" text
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_id uuid := gen_random_uuid();
BEGIN
    INSERT INTO "dbo"."ExamPapers"
        ("Id", "TenantId", "ExamId", "ClassId", "Name", "Subject", "SubjectId", "Date", "StartTime",
         "DurationMin", "MaxMarks", "Room", "Invigilator1", "Invigilator2", "Topics", "CreatedBy")
    VALUES (v_id, exampaper_create.TenantId, exampaper_create.ExamId, exampaper_create.ClassId, exampaper_create.Name,
        exampaper_create.Subject, exampaper_create.SubjectId, exampaper_create.Date::date, exampaper_create.StartTime,
        exampaper_create.DurationMin, COALESCE(exampaper_create.MaxMarks, 100), exampaper_create.Room,
        exampaper_create.Invigilator1, exampaper_create.Invigilator2, exampaper_create.Topics, exampaper_create.CreatedBy);

    RETURN QUERY
    SELECT "Id", "TenantId", "ExamId", "ClassId", "Name", "Subject", "SubjectId", "Date", "StartTime", "DurationMin",
           "MaxMarks", "Room", "Invigilator1", "Invigilator2", "Status", "Topics"
    FROM "dbo"."ExamPapers" WHERE "Id" = v_id;
END;
$$;
