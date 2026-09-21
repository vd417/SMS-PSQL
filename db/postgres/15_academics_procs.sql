-- Academics module: PL/pgSQL conversions of 24 stored procedures in the
-- AcademicPeriod/Assignment/ClassSubject/ClassTestSchedule/Class/ExamClass/ExamPaper/Exam/Grade/
-- Homework/Subject/TimetableSlot family. Source: full OBJECT_DEFINITION() extracted read-only
-- from the live SQL Server Sms database on 2026-09-21, cross-checked against
-- sqlserver-object-inventory.csv. `sp_GetStudents`/`sp_UpdateStudentFull`/`GetStudentById` (3
-- procs) are NOT converted: dead legacy code, zero C# call sites, same reasoning as
-- 11_sis_procs.sql's AddStudent.
--
-- See 09_auth_procs.sql's header for the naming convention, 10_tenancy_procs.sql's for the
-- `#variable_conflict use_column` note, and 13_staffing_procs.sql's for the RETURNS int (+
-- GET DIAGNOSTICS ROW_COUNT) vs RETURNS TABLE distinction driven by which BaseRepository method
-- each call site uses.
--
-- Classes/Homework/Subjects/TimetableSlots have all grown an AcademicSessionId column since
-- these procs were last written (per 04_tables.sql), but ClassResponse/SubjectResponse's own
-- comments confirm the *_Create/_Update procs are deliberately meant to keep returning the
-- original narrower column set (ListAsync/GetAsync return the wider one separately) -- this is
-- not the Staff_Create staleness bug from 13_staffing_procs.sql, so no columns were added here.

-- ============================================================
-- AcademicPeriod / ClassTestSchedule (single tenant-wide draft/published JSON blob, same shape)
-- UQ_AcademicPeriodSchedules_Tenant / UQ_ClassTestSchedules_Tenant back the upsert's
-- IF EXISTS ... ELSE INSERT -> ON CONFLICT.
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.academicperiod_get()
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "DraftJson" text, "PublishedJson" text, "DraftSavedAt" timestamptz, "PublishedAt" timestamptz)
LANGUAGE sql
AS $$
    SELECT "Id", "TenantId", "DraftJson", "PublishedJson", "DraftSavedAt", "PublishedAt"
    FROM "dbo"."AcademicPeriodSchedules" LIMIT 1;
$$;

CREATE OR REPLACE FUNCTION dbo.academicperiod_upsert(
    TenantId uuid, DraftJson text DEFAULT NULL, PublishedJson text DEFAULT NULL,
    DraftSavedAt timestamptz DEFAULT NULL, PublishedAt timestamptz DEFAULT NULL
)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "DraftJson" text, "PublishedJson" text, "DraftSavedAt" timestamptz, "PublishedAt" timestamptz)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    INSERT INTO "dbo"."AcademicPeriodSchedules" ("TenantId", "DraftJson", "PublishedJson", "DraftSavedAt", "PublishedAt")
    VALUES (TenantId, DraftJson, PublishedJson, DraftSavedAt, PublishedAt)
    ON CONFLICT ("TenantId") DO UPDATE SET
        "DraftJson" = COALESCE(academicperiod_upsert.DraftJson, "AcademicPeriodSchedules"."DraftJson"),
        "PublishedJson" = COALESCE(academicperiod_upsert.PublishedJson, "AcademicPeriodSchedules"."PublishedJson"),
        "DraftSavedAt" = COALESCE(academicperiod_upsert.DraftSavedAt, "AcademicPeriodSchedules"."DraftSavedAt"),
        "PublishedAt" = COALESCE(academicperiod_upsert.PublishedAt, "AcademicPeriodSchedules"."PublishedAt");

    RETURN QUERY
    SELECT "Id", "TenantId", "DraftJson", "PublishedJson", "DraftSavedAt", "PublishedAt"
    FROM "dbo"."AcademicPeriodSchedules" WHERE "TenantId" = academicperiod_upsert.TenantId LIMIT 1;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.classtestschedule_get()
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "DraftJson" text, "PublishedJson" text, "DraftSavedAt" timestamptz, "PublishedAt" timestamptz)
LANGUAGE sql
AS $$
    SELECT "Id", "TenantId", "DraftJson", "PublishedJson", "DraftSavedAt", "PublishedAt"
    FROM "dbo"."ClassTestSchedules" LIMIT 1;
$$;

CREATE OR REPLACE FUNCTION dbo.classtestschedule_upsert(
    TenantId uuid, DraftJson text DEFAULT NULL, PublishedJson text DEFAULT NULL,
    DraftSavedAt timestamptz DEFAULT NULL, PublishedAt timestamptz DEFAULT NULL
)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "DraftJson" text, "PublishedJson" text, "DraftSavedAt" timestamptz, "PublishedAt" timestamptz)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    INSERT INTO "dbo"."ClassTestSchedules" ("TenantId", "DraftJson", "PublishedJson", "DraftSavedAt", "PublishedAt")
    VALUES (TenantId, DraftJson, PublishedJson, DraftSavedAt, PublishedAt)
    ON CONFLICT ("TenantId") DO UPDATE SET
        "DraftJson" = COALESCE(classtestschedule_upsert.DraftJson, "ClassTestSchedules"."DraftJson"),
        "PublishedJson" = COALESCE(classtestschedule_upsert.PublishedJson, "ClassTestSchedules"."PublishedJson"),
        "DraftSavedAt" = COALESCE(classtestschedule_upsert.DraftSavedAt, "ClassTestSchedules"."DraftSavedAt"),
        "PublishedAt" = COALESCE(classtestschedule_upsert.PublishedAt, "ClassTestSchedules"."PublishedAt");

    RETURN QUERY
    SELECT "Id", "TenantId", "DraftJson", "PublishedJson", "DraftSavedAt", "PublishedAt"
    FROM "dbo"."ClassTestSchedules" WHERE "TenantId" = classtestschedule_upsert.TenantId LIMIT 1;
END;
$$;

-- ============================================================
-- Assignment
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.assignment_create(
    TenantId uuid, Title varchar(200), ClassId uuid DEFAULT NULL, ClassName varchar(80) DEFAULT NULL,
    Subject varchar(80) DEFAULT NULL, DueDate timestamptz DEFAULT NULL, Description text DEFAULT NULL,
    ImageUri text DEFAULT NULL, Period int DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Title" varchar(200), "ClassId" uuid, "ClassName" varchar(80), "Subject" varchar(80),
    "DueDate" date, "SubmissionsCount" int, "TotalStudents" int, "Status" varchar(20), "Description" text,
    "ImageUri" text, "Period" int
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_id uuid := gen_random_uuid();
BEGIN
    INSERT INTO "dbo"."Assignments" ("Id", "TenantId", "Title", "ClassId", "ClassName", "Subject", "DueDate", "Description", "ImageUri", "Period")
    VALUES (v_id, TenantId, Title, ClassId, ClassName, Subject, DueDate::date, Description, ImageUri, Period);

    RETURN QUERY
    SELECT "Id", "TenantId", "Title", "ClassId", "ClassName", "Subject", "DueDate", 0, 0, "Status", "Description", "ImageUri", "Period"
    FROM "dbo"."Assignments" WHERE "Id" = v_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.assignment_update(
    Id uuid, Title varchar(200), ClassId uuid DEFAULT NULL, ClassName varchar(80) DEFAULT NULL,
    Subject varchar(80) DEFAULT NULL, DueDate timestamptz DEFAULT NULL, Description text DEFAULT NULL,
    ImageUri text DEFAULT NULL, Period int DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Title" varchar(200), "ClassId" uuid, "ClassName" varchar(80), "Subject" varchar(80),
    "DueDate" date, "SubmissionsCount" int, "TotalStudents" int, "Status" varchar(20), "Description" text,
    "ImageUri" text, "Period" int
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE "dbo"."Assignments" SET
        "Title" = assignment_update.Title, "ClassId" = assignment_update.ClassId, "ClassName" = assignment_update.ClassName,
        "Subject" = assignment_update.Subject, "DueDate" = assignment_update.DueDate::date,
        "Description" = assignment_update.Description, "ImageUri" = assignment_update.ImageUri, "Period" = assignment_update.Period
    WHERE "Id" = assignment_update.Id;

    RETURN QUERY
    SELECT "Id", "TenantId", "Title", "ClassId", "ClassName", "Subject", "DueDate", 0, 0, "Status", "Description", "ImageUri", "Period"
    FROM "dbo"."Assignments" WHERE "Id" = assignment_update.Id;
END;
$$;

-- ============================================================
-- ClassSubject (replace-all-by-name, resolving each name to a Subjects.Id when one matches)
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.classsubject_list(ClassId uuid)
RETURNS TABLE ("Name" varchar(80))
LANGUAGE sql
AS $$
    SELECT "Name" FROM "dbo"."ClassSubjects" WHERE "ClassId" = ClassId ORDER BY "Name";
$$;

CREATE OR REPLACE FUNCTION dbo.classsubject_replace(TenantId uuid, ClassId uuid, NamesJson text)
RETURNS TABLE ("Name" varchar(80))
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    DELETE FROM "dbo"."ClassSubjects" WHERE "ClassId" = classsubject_replace.ClassId AND "TenantId" = classsubject_replace.TenantId;

    IF NamesJson IS NOT NULL AND trim(NamesJson) NOT IN ('', '[]', 'null') THEN
        INSERT INTO "dbo"."ClassSubjects" ("Id", "TenantId", "ClassId", "SubjectId", "Name")
        SELECT gen_random_uuid(), classsubject_replace.TenantId, classsubject_replace.ClassId, sub."Id", trim(j.value_text)
        FROM jsonb_array_elements_text(NamesJson::jsonb) AS j(value_text)
        LEFT JOIN "dbo"."Subjects" sub
            ON sub."TenantId" = classsubject_replace.TenantId AND lower(trim(sub."Name")) = lower(trim(j.value_text))
        WHERE trim(COALESCE(j.value_text, '')) <> '';
    END IF;

    RETURN QUERY
    SELECT "Name" FROM "dbo"."ClassSubjects" WHERE "ClassId" = classsubject_replace.ClassId ORDER BY "Name";
END;
$$;

-- ============================================================
-- Class
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.class_create(
    TenantId uuid, Name varchar(80), Grade varchar(20), Section varchar(20),
    Subject varchar(80), Room varchar(40), ClassTeacherId uuid
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Name" varchar(80), "Grade" varchar(20), "Section" varchar(20), "Subject" varchar(80),
    "Room" varchar(40), "StudentCount" int, "ClassTeacherId" uuid
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_id uuid := gen_random_uuid();
BEGIN
    INSERT INTO "dbo"."Classes" ("Id", "TenantId", "Name", "Grade", "Section", "Subject", "Room", "ClassTeacherId")
    VALUES (v_id, TenantId, Name, Grade, Section, Subject, Room, ClassTeacherId);

    RETURN QUERY
    SELECT "Id", "TenantId", "Name", "Grade", "Section", "Subject", "Room", "StudentCount", "ClassTeacherId"
    FROM "dbo"."Classes" WHERE "Id" = v_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.class_update(
    Id uuid, TenantId uuid, Name varchar(80) DEFAULT NULL, Grade varchar(20) DEFAULT NULL,
    Section varchar(20) DEFAULT NULL, Subject varchar(80) DEFAULT NULL, Room varchar(40) DEFAULT NULL,
    ClassTeacherId uuid DEFAULT NULL, ClearClassTeacher boolean DEFAULT false
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Name" varchar(80), "Grade" varchar(20), "Section" varchar(20), "Subject" varchar(80),
    "Room" varchar(40), "StudentCount" int, "ClassTeacherId" uuid
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE "dbo"."Classes" SET
        "Name" = COALESCE(class_update.Name, "Name"), "Grade" = COALESCE(class_update.Grade, "Grade"),
        "Section" = COALESCE(class_update.Section, "Section"), "Subject" = COALESCE(class_update.Subject, "Subject"),
        "Room" = COALESCE(class_update.Room, "Room"),
        "ClassTeacherId" = CASE
            WHEN class_update.ClearClassTeacher THEN NULL
            WHEN class_update.ClassTeacherId IS NOT NULL THEN class_update.ClassTeacherId
            ELSE "ClassTeacherId"
        END
    WHERE "Id" = class_update.Id AND "TenantId" = class_update.TenantId;

    RETURN QUERY
    SELECT "Id", "TenantId", "Name", "Grade", "Section", "Subject", "Room", "StudentCount", "ClassTeacherId"
    FROM "dbo"."Classes" WHERE "Id" = class_update.Id AND "TenantId" = class_update.TenantId;
END;
$$;

-- ============================================================
-- ExamClass (replace-all)
-- TRY_CONVERT(uniqueidentifier, ...) -> a bare ::uuid cast wrapped in a regex/format guard is
-- unnecessary here since a malformed uuid literal simply won't compare equal in the WHERE
-- below; kept the filter faithful to "skip anything that doesn't parse" via a guarded CTE.
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.examclass_list(ExamId uuid)
RETURNS TABLE ("ClassId" uuid)
LANGUAGE sql
AS $$
    SELECT "ClassId" FROM "dbo"."ExamClasses" WHERE "ExamId" = ExamId;
$$;

CREATE OR REPLACE FUNCTION dbo.examclass_replace(TenantId uuid, ExamId uuid, ClassIdsJson text)
RETURNS TABLE ("ClassId" uuid)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    DELETE FROM "dbo"."ExamClasses" WHERE "TenantId" = examclass_replace.TenantId AND "ExamId" = examclass_replace.ExamId;

    IF ClassIdsJson IS NOT NULL AND trim(ClassIdsJson) NOT IN ('', '[]', 'null') THEN
        INSERT INTO "dbo"."ExamClasses" ("Id", "TenantId", "ExamId", "ClassId")
        SELECT gen_random_uuid(), examclass_replace.TenantId, examclass_replace.ExamId, parsed.class_id
        FROM (
            SELECT (CASE WHEN trim(j.value_text) ~
                '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
                THEN trim(j.value_text)::uuid END) AS class_id
            FROM jsonb_array_elements_text(ClassIdsJson::jsonb) AS j(value_text)
        ) parsed
        WHERE parsed.class_id IS NOT NULL;
    END IF;

    RETURN QUERY
    SELECT "ClassId" FROM "dbo"."ExamClasses" WHERE "ExamId" = examclass_replace.ExamId;
END;
$$;

-- ============================================================
-- ExamPaper
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.exampaper_create(
    TenantId uuid, ExamId uuid, ClassId uuid, Name varchar(120), Subject varchar(80), SubjectId uuid,
    Date timestamptz, StartTime varchar(10), DurationMin int, MaxMarks int, Room varchar(40),
    Invigilator1 varchar(120), Invigilator2 varchar(120), Topics text DEFAULT NULL
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
         "DurationMin", "MaxMarks", "Room", "Invigilator1", "Invigilator2", "Topics")
    VALUES (v_id, exampaper_create.TenantId, exampaper_create.ExamId, exampaper_create.ClassId, exampaper_create.Name,
        exampaper_create.Subject, exampaper_create.SubjectId, exampaper_create.Date::date, exampaper_create.StartTime,
        exampaper_create.DurationMin, COALESCE(exampaper_create.MaxMarks, 100), exampaper_create.Room,
        exampaper_create.Invigilator1, exampaper_create.Invigilator2, exampaper_create.Topics);

    RETURN QUERY
    SELECT "Id", "TenantId", "ExamId", "ClassId", "Name", "Subject", "SubjectId", "Date", "StartTime", "DurationMin",
           "MaxMarks", "Room", "Invigilator1", "Invigilator2", "Status", "Topics"
    FROM "dbo"."ExamPapers" WHERE "Id" = v_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.exampaper_delete(Id uuid)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    DELETE FROM "dbo"."ExamPapers" WHERE "Id" = Id;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.exampaper_update(
    Id uuid, Name varchar(120) DEFAULT NULL, Subject varchar(80) DEFAULT NULL, SubjectId uuid DEFAULT NULL,
    Date timestamptz DEFAULT NULL, StartTime varchar(10) DEFAULT NULL, DurationMin int DEFAULT NULL,
    MaxMarks int DEFAULT NULL, Room varchar(40) DEFAULT NULL, Invigilator1 varchar(120) DEFAULT NULL,
    Invigilator2 varchar(120) DEFAULT NULL, Status varchar(20) DEFAULT NULL, Topics text DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "ExamId" uuid, "ClassId" uuid, "Name" varchar(120), "Subject" varchar(80),
    "SubjectId" uuid, "Date" date, "StartTime" varchar(10), "DurationMin" int, "MaxMarks" int, "Room" varchar(40),
    "Invigilator1" varchar(120), "Invigilator2" varchar(120), "Status" varchar(20), "Topics" text
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE "dbo"."ExamPapers" SET
        "Name" = COALESCE(exampaper_update.Name, "Name"), "Subject" = COALESCE(exampaper_update.Subject, "Subject"),
        "SubjectId" = COALESCE(exampaper_update.SubjectId, "SubjectId"), "Date" = COALESCE(exampaper_update.Date::date, "Date"),
        "StartTime" = COALESCE(exampaper_update.StartTime, "StartTime"), "DurationMin" = COALESCE(exampaper_update.DurationMin, "DurationMin"),
        "MaxMarks" = COALESCE(exampaper_update.MaxMarks, "MaxMarks"), "Room" = COALESCE(exampaper_update.Room, "Room"),
        "Invigilator1" = COALESCE(exampaper_update.Invigilator1, "Invigilator1"),
        "Invigilator2" = COALESCE(exampaper_update.Invigilator2, "Invigilator2"),
        "Status" = COALESCE(exampaper_update.Status, "Status"), "Topics" = COALESCE(exampaper_update.Topics, "Topics")
    WHERE "Id" = exampaper_update.Id;

    RETURN QUERY
    SELECT "Id", "TenantId", "ExamId", "ClassId", "Name", "Subject", "SubjectId", "Date", "StartTime", "DurationMin",
           "MaxMarks", "Room", "Invigilator1", "Invigilator2", "Status", "Topics"
    FROM "dbo"."ExamPapers" WHERE "Id" = exampaper_update.Id;
END;
$$;

-- ============================================================
-- Exam
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.exam_create(
    TenantId uuid, Name varchar(120), Type varchar(40), Grades varchar(40),
    FromDate timestamptz, ToDate timestamptz, SubjectCount int
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Name" varchar(120), "Type" varchar(40), "Grades" varchar(40),
    "FromDate" date, "ToDate" date, "SubjectCount" int, "Status" varchar(20), "MarksEnteredPct" numeric(5,2), "Published" boolean
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_id uuid := gen_random_uuid();
BEGIN
    INSERT INTO "dbo"."Exams" ("Id", "TenantId", "Name", "Type", "Grades", "FromDate", "ToDate", "SubjectCount")
    VALUES (v_id, TenantId, Name, Type, Grades, FromDate::date, ToDate::date, COALESCE(SubjectCount, 0));

    RETURN QUERY
    SELECT "Id", "TenantId", "Name", "Type", "Grades", "FromDate", "ToDate", "SubjectCount", "Status", "MarksEnteredPct", "Published"
    FROM "dbo"."Exams" WHERE "Id" = v_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.exam_update(Id uuid, Status varchar(20), Published boolean, MarksEnteredPct numeric(5,2))
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Name" varchar(120), "Type" varchar(40), "Grades" varchar(40),
    "FromDate" date, "ToDate" date, "SubjectCount" int, "Status" varchar(20), "MarksEnteredPct" numeric(5,2), "Published" boolean
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE "dbo"."Exams" SET
        "Status" = COALESCE(exam_update.Status, "Status"), "Published" = COALESCE(exam_update.Published, "Published"),
        "MarksEnteredPct" = COALESCE(exam_update.MarksEnteredPct, "MarksEnteredPct")
    WHERE "Id" = exam_update.Id;

    RETURN QUERY
    SELECT "Id", "TenantId", "Name", "Type", "Grades", "FromDate", "ToDate", "SubjectCount", "Status", "MarksEnteredPct", "Published"
    FROM "dbo"."Exams" WHERE "Id" = exam_update.Id;
END;
$$;

-- ============================================================
-- Grade
-- UQ_Grades_Paper_Student (TenantId, ExamPaperId, StudentId) backs the MERGE -> ON CONFLICT.
-- GradeResponse is a property-bag class (Dapper maps by name), so the RETURNS TABLE column set
-- doesn't need to exactly match a positional constructor here.
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.grade_upsert(TenantId uuid, StudentId uuid, StudentName varchar(200), ExamPaperId uuid, Marks numeric(6,2))
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "StudentId" uuid, "StudentName" varchar(200), "ExamPaperId" uuid,
    "Marks" numeric(6,2), "MaxMarks" numeric(6,2), "Grade" varchar(4), "Gpa" numeric(4,2), "Pass" boolean, "Date" date
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_max numeric(6,2);
    v_pct numeric(6,2);
    v_grade varchar(4);
    v_gpa numeric(4,2);
    v_pass boolean;
    v_today date := now()::date;
BEGIN
    SELECT "MaxMarks" INTO v_max FROM "dbo"."ExamPapers" WHERE "Id" = grade_upsert.ExamPaperId;
    v_max := COALESCE(NULLIF(v_max, 0), 100);

    v_pct := grade_upsert.Marks * 100.0 / v_max;
    v_grade := CASE WHEN v_pct >= 91 THEN 'A1' WHEN v_pct >= 81 THEN 'A2' WHEN v_pct >= 71 THEN 'B1'
        WHEN v_pct >= 61 THEN 'B2' WHEN v_pct >= 51 THEN 'C1' WHEN v_pct >= 41 THEN 'C2'
        WHEN v_pct >= 33 THEN 'D' ELSE 'E' END;
    v_gpa := CASE WHEN v_pct >= 91 THEN 10 WHEN v_pct >= 81 THEN 9 WHEN v_pct >= 71 THEN 8
        WHEN v_pct >= 61 THEN 7 WHEN v_pct >= 51 THEN 6 WHEN v_pct >= 41 THEN 5
        WHEN v_pct >= 33 THEN 4 ELSE 3 END;
    v_pass := v_pct >= 33;

    INSERT INTO "dbo"."Grades" ("Id", "TenantId", "StudentId", "StudentName", "ExamPaperId", "Marks", "MaxMarks", "Grade", "Gpa", "Pass", "Date")
    VALUES (gen_random_uuid(), TenantId, StudentId, StudentName, ExamPaperId, Marks, v_max, v_grade, v_gpa, v_pass, v_today)
    ON CONFLICT ("TenantId", "ExamPaperId", "StudentId") DO UPDATE SET
        "Marks" = grade_upsert.Marks, "MaxMarks" = v_max, "Grade" = v_grade, "Gpa" = v_gpa, "Pass" = v_pass,
        "StudentName" = grade_upsert.StudentName, "Date" = v_today;

    RETURN QUERY
    SELECT "Id", "TenantId", "StudentId", "StudentName", "ExamPaperId", "Marks", "MaxMarks", "Grade", "Gpa", "Pass", "Date"
    FROM "dbo"."Grades" WHERE "TenantId" = grade_upsert.TenantId AND "ExamPaperId" = grade_upsert.ExamPaperId AND "StudentId" = grade_upsert.StudentId;
END;
$$;

-- ============================================================
-- Homework
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.homework_create(
    TenantId uuid, StudentId uuid, AssignmentId uuid, Title varchar(200), SubjectId uuid,
    DueDate timestamptz, DueTime varchar(10), Priority varchar(10) DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "StudentId" uuid, "AssignmentId" uuid, "Title" varchar(200), "SubjectId" uuid,
    "DueDate" date, "DueTime" varchar(10), "Status" varchar(20), "Priority" varchar(10), "Grade" varchar(4)
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_id uuid := gen_random_uuid();
BEGIN
    INSERT INTO "dbo"."Homework" ("Id", "TenantId", "StudentId", "AssignmentId", "Title", "SubjectId", "DueDate", "DueTime", "Priority")
    VALUES (v_id, TenantId, StudentId, AssignmentId, Title, SubjectId, DueDate::date, DueTime, COALESCE(Priority, 'med'));

    RETURN QUERY
    SELECT "Id", "TenantId", "StudentId", "AssignmentId", "Title", "SubjectId", "DueDate", "DueTime", "Status", "Priority", "Grade"
    FROM "dbo"."Homework" WHERE "Id" = v_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.homework_setstatus(Id uuid, Status varchar(20))
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "StudentId" uuid, "AssignmentId" uuid, "Title" varchar(200), "SubjectId" uuid,
    "DueDate" date, "DueTime" varchar(10), "Status" varchar(20), "Priority" varchar(10), "Grade" varchar(4)
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE "dbo"."Homework" SET "Status" = homework_setstatus.Status WHERE "Id" = homework_setstatus.Id;

    RETURN QUERY
    SELECT "Id", "TenantId", "StudentId", "AssignmentId", "Title", "SubjectId", "DueDate", "DueTime", "Status", "Priority", "Grade"
    FROM "dbo"."Homework" WHERE "Id" = homework_setstatus.Id;
END;
$$;

-- ============================================================
-- Subject
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.subject_create(TenantId uuid, Name varchar(80), Short varchar(20), TeacherId uuid, Color varchar(40))
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "Name" varchar(80), "Short" varchar(20), "TeacherId" uuid, "Color" varchar(40))
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_id uuid := gen_random_uuid();
BEGIN
    INSERT INTO "dbo"."Subjects" ("Id", "TenantId", "Name", "Short", "TeacherId", "Color")
    VALUES (v_id, TenantId, Name, Short, TeacherId, Color);

    RETURN QUERY
    SELECT "Id", "TenantId", "Name", "Short", "TeacherId", "Color" FROM "dbo"."Subjects" WHERE "Id" = v_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.subject_delete(Id uuid, TenantId uuid)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    DELETE FROM "dbo"."Subjects" WHERE "Id" = Id AND "TenantId" = TenantId;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.subject_update(
    Id uuid, TenantId uuid, Name varchar(80) DEFAULT NULL, Short varchar(20) DEFAULT NULL,
    TeacherId uuid DEFAULT NULL, Color varchar(40) DEFAULT NULL, ClearTeacher boolean DEFAULT false
)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "Name" varchar(80), "Short" varchar(20), "TeacherId" uuid, "Color" varchar(40))
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE "dbo"."Subjects" SET
        "Name" = COALESCE(subject_update.Name, "Name"), "Short" = COALESCE(subject_update.Short, "Short"),
        "Color" = COALESCE(subject_update.Color, "Color"),
        "TeacherId" = CASE
            WHEN subject_update.ClearTeacher THEN NULL
            WHEN subject_update.TeacherId IS NOT NULL THEN subject_update.TeacherId
            ELSE "TeacherId"
        END
    WHERE "Id" = subject_update.Id AND "TenantId" = subject_update.TenantId;

    RETURN QUERY
    SELECT "Id", "TenantId", "Name", "Short", "TeacherId", "Color"
    FROM "dbo"."Subjects" WHERE "Id" = subject_update.Id AND "TenantId" = subject_update.TenantId;
END;
$$;

-- ============================================================
-- TimetableSlot
-- TimetableSlotResponse is a property-bag class (Dapper maps by name), so the RETURNS TABLE
-- column set doesn't need to exactly match a positional constructor here.
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.timetableslot_create(
    TenantId uuid, Day varchar(3), Period int, Subject varchar(80) DEFAULT NULL,
    ClassId uuid DEFAULT NULL, ClassName varchar(80) DEFAULT NULL, Room varchar(40) DEFAULT NULL,
    StartTime varchar(10) DEFAULT NULL, EndTime varchar(10) DEFAULT NULL, TeacherId uuid DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Day" varchar(3), "Period" int, "Subject" varchar(80), "ClassId" uuid,
    "ClassName" varchar(80), "Room" varchar(40), "StartTime" varchar(10), "EndTime" varchar(10), "TeacherId" uuid
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_id uuid;
BEGIN
    IF timetableslot_create.ClassId IS NOT NULL THEN
        DELETE FROM "dbo"."TimetableSlots"
        WHERE "TenantId" = timetableslot_create.TenantId AND "ClassId" = timetableslot_create.ClassId
          AND "Day" = timetableslot_create.Day AND "Period" = timetableslot_create.Period;
    END IF;

    INSERT INTO "dbo"."TimetableSlots" ("Id", "TenantId", "Day", "Period", "Subject", "ClassId", "ClassName", "Room", "StartTime", "EndTime", "TeacherId")
    VALUES (gen_random_uuid(), TenantId, Day, Period, Subject, ClassId, ClassName, Room, StartTime, EndTime, TeacherId)
    RETURNING "Id" INTO v_id;

    RETURN QUERY
    SELECT "Id", "TenantId", "Day", "Period", "Subject", "ClassId", "ClassName", "Room", "StartTime", "EndTime", "TeacherId"
    FROM "dbo"."TimetableSlots" WHERE "Id" = v_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.timetableslot_delete(Id uuid, TenantId uuid)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    DELETE FROM "dbo"."TimetableSlots" WHERE "Id" = Id AND "TenantId" = TenantId;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

-- ============================================================
-- Attendance / StaffAttendance / PeriodAttendance / ExamAttendance bulk-upsert
-- SQL Server table-valued parameters (AttendanceTvp/StaffAttendanceTvp/PeriodAttendanceTvp/
-- ExamAttendanceTvp) have no 1:1 Postgres CREATE TYPE mapping -- same jsonb-serialized `Rows`
-- text parameter + jsonb_to_recordset() pattern as 14_transport_procs.sql's
-- dbo.tripping_bulkinsert. All four are called via ExecuteProcAsync -> RETURNS int +
-- GET DIAGNOSTICS ROW_COUNT, never RETURNS TABLE.
-- UQ_Attendance_Class_Student_Date / UQ_StaffAttendance_Type_Person_Date /
-- UQ_ExamAttendance_Paper_Student back the simple three MERGE -> ON CONFLICT ports.
-- PeriodAttendance_BulkUpsert also writes an audit trail row per changed record (matching the
-- source's OUTPUT-into-@Changes-table pattern); ported as an explicit per-row loop since
-- Postgres's INSERT ... ON CONFLICT ... RETURNING can't expose both the old and new value of a
-- column in one statement the way SQL Server's OUTPUT deleted.x/inserted.x can.
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.attendance_bulkupsert(TenantId uuid, ClassId uuid, Date timestamptz, MarkedBy uuid, Rows text)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    INSERT INTO "dbo"."AttendanceRecords" ("Id", "TenantId", "ClassId", "StudentId", "Date", "Status", "MarkedBy")
    SELECT gen_random_uuid(), TenantId, ClassId, r."StudentId", Date::date, r."Status", MarkedBy
    FROM jsonb_to_recordset(Rows::jsonb) AS r("StudentId" uuid, "Status" varchar(20))
    ON CONFLICT ("TenantId", "ClassId", "StudentId", "Date") DO UPDATE
    SET "Status" = excluded."Status", "MarkedBy" = attendance_bulkupsert.MarkedBy;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.staffattendance_bulkupsert(TenantId uuid, PersonType varchar(10), Date timestamptz, MarkedBy uuid, Rows text)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    INSERT INTO "dbo"."StaffAttendanceRecords" ("Id", "TenantId", "PersonType", "PersonId", "Date", "Status", "MarkedBy")
    SELECT gen_random_uuid(), TenantId, PersonType, r."PersonId", Date::date, r."Status", MarkedBy
    FROM jsonb_to_recordset(Rows::jsonb) AS r("PersonId" uuid, "Status" varchar(20))
    ON CONFLICT ("TenantId", "PersonType", "PersonId", "Date") DO UPDATE
    SET "Status" = excluded."Status", "MarkedBy" = staffattendance_bulkupsert.MarkedBy;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.examattendance_bulkupsert(TenantId uuid, ExamPaperId uuid, MarkedBy uuid, Rows text)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    INSERT INTO "dbo"."ExamAttendanceRecords" ("Id", "TenantId", "ExamPaperId", "StudentId", "Status", "MarkedBy")
    SELECT gen_random_uuid(), TenantId, ExamPaperId, r."StudentId", r."Status", MarkedBy
    FROM jsonb_to_recordset(Rows::jsonb) AS r("StudentId" uuid, "Status" varchar(20))
    ON CONFLICT ("TenantId", "ExamPaperId", "StudentId") DO UPDATE
    SET "Status" = excluded."Status", "MarkedBy" = examattendance_bulkupsert.MarkedBy;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.periodattendance_bulkupsert(
    TenantId uuid, ClassId uuid, Date timestamptz, Period int, Subject varchar(120), Rows text,
    PeriodId uuid DEFAULT NULL, SubjectId uuid DEFAULT NULL, MarkedBy uuid DEFAULT NULL,
    MarkedByRole varchar(64) DEFAULT NULL, GeoFenceStatus varchar(32) DEFAULT NULL,
    GeoDistanceMeters int DEFAULT NULL, GeoCapturedAt timestamptz DEFAULT NULL
)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    v_now timestamptz := now();
    v_actor_name varchar(200);
    v_count int := 0;
    r record;
    v_old_status varchar(20);
    v_record_id uuid;
    v_is_insert boolean;
BEGIN
    SELECT "Name" INTO v_actor_name FROM "dbo"."Users" WHERE "Id" = periodattendance_bulkupsert.MarkedBy;

    FOR r IN SELECT * FROM jsonb_to_recordset(periodattendance_bulkupsert.Rows::jsonb) AS x("StudentId" uuid, "Status" varchar(20))
    LOOP
        v_record_id := NULL;
        v_old_status := NULL;
        SELECT "Id", "Status" INTO v_record_id, v_old_status
        FROM "dbo"."PeriodAttendanceRecords"
        WHERE "TenantId" = periodattendance_bulkupsert.TenantId AND "ClassId" = periodattendance_bulkupsert.ClassId
          AND "StudentId" = r."StudentId" AND "Date" = periodattendance_bulkupsert.Date::date
          AND "Period" = periodattendance_bulkupsert.Period AND "Subject" = periodattendance_bulkupsert.Subject;

        IF v_record_id IS NOT NULL THEN
            v_is_insert := false;
            UPDATE "dbo"."PeriodAttendanceRecords" SET
                "Status" = r."Status",
                "PeriodId" = COALESCE(periodattendance_bulkupsert.PeriodId, "PeriodId"),
                "SubjectId" = COALESCE(periodattendance_bulkupsert.SubjectId, "SubjectId"),
                "MarkedBy" = periodattendance_bulkupsert.MarkedBy, "MarkedByRole" = periodattendance_bulkupsert.MarkedByRole,
                "UpdatedBy" = periodattendance_bulkupsert.MarkedBy, "UpdatedByRole" = periodattendance_bulkupsert.MarkedByRole,
                "UpdatedAt" = v_now,
                "GeoFenceStatus" = COALESCE(periodattendance_bulkupsert.GeoFenceStatus, "GeoFenceStatus"),
                "GeoDistanceMeters" = COALESCE(periodattendance_bulkupsert.GeoDistanceMeters, "GeoDistanceMeters"),
                "GeoCapturedAt" = COALESCE(periodattendance_bulkupsert.GeoCapturedAt, "GeoCapturedAt")
            WHERE "Id" = v_record_id;
        ELSE
            v_is_insert := true;
            v_record_id := gen_random_uuid();
            INSERT INTO "dbo"."PeriodAttendanceRecords"
                ("Id", "TenantId", "ClassId", "StudentId", "Date", "Period", "PeriodId", "Subject", "SubjectId", "Status",
                 "MarkedBy", "MarkedByRole", "UpdatedBy", "UpdatedByRole", "CreatedAt", "UpdatedAt",
                 "GeoFenceStatus", "GeoDistanceMeters", "GeoCapturedAt")
            VALUES (v_record_id, periodattendance_bulkupsert.TenantId, periodattendance_bulkupsert.ClassId, r."StudentId",
                periodattendance_bulkupsert.Date::date, periodattendance_bulkupsert.Period, periodattendance_bulkupsert.PeriodId,
                periodattendance_bulkupsert.Subject, periodattendance_bulkupsert.SubjectId, r."Status",
                periodattendance_bulkupsert.MarkedBy, periodattendance_bulkupsert.MarkedByRole,
                periodattendance_bulkupsert.MarkedBy, periodattendance_bulkupsert.MarkedByRole, v_now, v_now,
                periodattendance_bulkupsert.GeoFenceStatus, periodattendance_bulkupsert.GeoDistanceMeters, periodattendance_bulkupsert.GeoCapturedAt);
        END IF;

        IF v_is_insert OR v_old_status IS NULL OR v_old_status <> r."Status" THEN
            INSERT INTO "dbo"."PeriodAttendanceAudit"
                ("Id", "TenantId", "RecordId", "ClassId", "StudentId", "Date", "Period", "Subject", "FromStatus", "ToStatus", "ActorId", "ActorName", "ActorRole", "At")
            VALUES (gen_random_uuid(), periodattendance_bulkupsert.TenantId, v_record_id, periodattendance_bulkupsert.ClassId, r."StudentId",
                periodattendance_bulkupsert.Date::date, periodattendance_bulkupsert.Period, periodattendance_bulkupsert.Subject,
                v_old_status, r."Status", periodattendance_bulkupsert.MarkedBy, v_actor_name, periodattendance_bulkupsert.MarkedByRole, v_now);
        END IF;

        v_count := v_count + 1;
    END LOOP;

    RETURN v_count;
END;
$$;

-- ============================================================
-- SchoolHouse (replace-all-by-name)
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.schoolhouse_list()
RETURNS TABLE ("Name" varchar(80))
LANGUAGE sql
AS $$
    SELECT "Name" FROM "dbo"."SchoolHouses" ORDER BY "Name";
$$;

CREATE OR REPLACE FUNCTION dbo.schoolhouse_replace(TenantId uuid, NamesJson text)
RETURNS TABLE ("Name" varchar(80))
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    DELETE FROM "dbo"."SchoolHouses" WHERE "TenantId" = schoolhouse_replace.TenantId;

    IF NamesJson IS NOT NULL AND trim(NamesJson) NOT IN ('', '[]', 'null') THEN
        INSERT INTO "dbo"."SchoolHouses" ("Id", "TenantId", "Name")
        SELECT gen_random_uuid(), schoolhouse_replace.TenantId, trim(j.value_text)
        FROM jsonb_array_elements_text(NamesJson::jsonb) AS j(value_text)
        WHERE trim(COALESCE(j.value_text, '')) <> '';
    END IF;

    RETURN QUERY SELECT "Name" FROM "dbo"."SchoolHouses" ORDER BY "Name";
END;
$$;

-- ============================================================
-- CalendarEvent (not part of the original 24 -- CalendarRepository.cs calls these two
-- directly, so converted alongside it).
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.calendarevent_create(
    TenantId uuid, Title varchar(200), Date timestamptz, Type varchar(20),
    "time" varchar(10) DEFAULT NULL, Description text DEFAULT NULL, ChannelsJson varchar(200) DEFAULT NULL
)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "Title" varchar(200), "Date" date, "Time" varchar(10), "Type" varchar(20), "Description" text, "ChannelsJson" varchar(200))
LANGUAGE sql
AS $$
    INSERT INTO "dbo"."CalendarEvents" ("TenantId", "Title", "Date", "Time", "Type", "Description", "ChannelsJson")
    VALUES (TenantId, Title, Date::date, "time", Type, Description, ChannelsJson)
    RETURNING "Id", "TenantId", "Title", "Date", "Time", "Type", "Description", "ChannelsJson";
$$;

CREATE OR REPLACE FUNCTION dbo.calendarevent_delete(TenantId uuid, Id uuid)
RETURNS TABLE ("Deleted" int)
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    DELETE FROM "dbo"."CalendarEvents" WHERE "Id" = calendarevent_delete.Id AND "TenantId" = calendarevent_delete.TenantId;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN QUERY SELECT v_count;
END;
$$;

-- ============================================================
-- LibraryBook_Create (not part of the original 24 -- LibraryRepository.cs calls it directly).
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.librarybook_create(
    TenantId uuid, Title varchar(200), Author varchar(120), Subject varchar(80) DEFAULT NULL,
    IssuedTo varchar(120) DEFAULT NULL, DueDate timestamptz DEFAULT NULL, Status varchar(20) DEFAULT 'available'
)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "Title" varchar(200), "Author" varchar(120), "Subject" varchar(80),
               "IssuedTo" varchar(120), "DueDate" date, "Status" varchar(20))
LANGUAGE sql
AS $$
    INSERT INTO "dbo"."LibraryBooks" ("TenantId", "Title", "Author", "Subject", "IssuedTo", "DueDate", "Status")
    VALUES (TenantId, Title, Author, Subject, IssuedTo, DueDate::date, Status)
    RETURNING "Id", "TenantId", "Title", "Author", "Subject", "IssuedTo", "DueDate", "Status";
$$;
