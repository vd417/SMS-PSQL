-- Staffing/Payroll module: PL/pgSQL conversions of 21 of the 28 stored procedures in the
-- Leave/PayrollRun/PersonExtras/SalaryProfile/SalaryStructure/StaffDocument/Staff/Teacher family.
-- Source: full OBJECT_DEFINITION() extracted read-only from the live SQL Server Sms database on
-- 2026-09-21, cross-checked against sqlserver-object-inventory.csv.
--
-- dbo.StaffTask_Complete/StaffTask_CompleteForUser/StaffTask_Create/StaffTask_CreateForUser/
-- StaffTask_Delete/StaffTasks_ListForStaff/StaffTasks_ListForUser (7 procs) are NOT converted:
-- zero C# call sites anywhere in src/ or tests/ -- dead code superseded by the newer
-- Sms.Modules.Tasks system (see src/Sms.Application/Services/Tasks/TaskService.cs), same
-- "dead legacy code, not on spec" reasoning as 11_sis_procs.sql's AddStudent.
--
-- See 09_auth_procs.sql's header for the naming convention, 10_tenancy_procs.sql's for the
-- `#variable_conflict use_column` note, and 11_sis_procs.sql's Student_EnsureLogin header for
-- the UPDLOCK/HOLDLOCK -> FOR UPDATE and TRY/CATCH -> nested EXCEPTION block notes (all apply
-- here too, most directly in staff_ensurelogin below).

-- ============================================================
-- Leave
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.leave_balances(TenantId uuid, RequesterId uuid, Year int)
RETURNS TABLE ("Type" varchar(20), "Total" int, "Used" int)
LANGUAGE sql
AS $$
    SELECT e."Type", e."TotalDays" AS "Total",
           CAST(COALESCE(sum((r."ToDate" - r."FromDate") + 1), 0) AS int) AS "Used"
    FROM "dbo"."LeaveEntitlements" e
    LEFT JOIN "dbo"."LeaveRequests" r
        ON r."TenantId" = e."TenantId" AND r."RequesterId" = e."RequesterId" AND r."Type" = e."Type"
        AND r."Status" = 'approved' AND EXTRACT(year FROM r."FromDate") = leave_balances.Year
    WHERE e."TenantId" = leave_balances.TenantId AND e."RequesterId" = leave_balances.RequesterId
      AND e."Year" = leave_balances.Year
    GROUP BY e."Type", e."TotalDays"
    ORDER BY e."Type";
$$;

-- FromDate/ToDate are timestamp (not date): CreateLeaveRequest sends them as C# DateTime?, which
-- Npgsql binds as timestamp, not date -- same function-overload-resolution reasoning as
-- 12_finance_procs.sql's feestructure_upsert.EffectiveFrom.
CREATE OR REPLACE FUNCTION dbo.leave_create(
    TenantId uuid, RequesterId uuid, ChildId uuid, Type varchar(20), FromDate timestamptz, ToDate timestamptz,
    Reason varchar(500), Substitute varchar(120), Priority varchar(10) DEFAULT 'medium',
    AttachmentUrls text DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "RequesterId" uuid, "ChildId" uuid, "Type" varchar(20), "FromDate" date,
    "ToDate" date, "Reason" varchar(500), "Substitute" varchar(120), "Status" varchar(20), "AppliedOn" date,
    "DecidedNote" varchar(500), "Priority" varchar(10), "AttachmentUrls" text
)
LANGUAGE sql
AS $$
    INSERT INTO "dbo"."LeaveRequests"
        ("Id", "TenantId", "RequesterId", "ChildId", "Type", "FromDate", "ToDate", "Reason", "Substitute",
         "AppliedOn", "Priority", "AttachmentUrls")
    VALUES (gen_random_uuid(), TenantId, RequesterId, ChildId, COALESCE(Type, 'casual'), FromDate::date, ToDate::date,
            Reason, Substitute, now()::date, COALESCE(Priority, 'medium'), AttachmentUrls)
    RETURNING "Id", "TenantId", "RequesterId", "ChildId", "Type", "FromDate", "ToDate", "Reason", "Substitute",
              "Status", "AppliedOn", "DecidedNote", "Priority", "AttachmentUrls";
$$;

CREATE OR REPLACE FUNCTION dbo.leave_decide(Id uuid, Status varchar(20), DecidedBy uuid, DecidedNote varchar(500))
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "RequesterId" uuid, "ChildId" uuid, "Type" varchar(20), "FromDate" date,
    "ToDate" date, "Reason" varchar(500), "Substitute" varchar(120), "Status" varchar(20), "AppliedOn" date,
    "DecidedNote" varchar(500), "Priority" varchar(10), "AttachmentUrls" text,
    "RequesterName" varchar(200), "DecidedByName" varchar(200)
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE "dbo"."LeaveRequests" SET "Status" = leave_decide.Status, "DecidedBy" = leave_decide.DecidedBy,
        "DecidedNote" = leave_decide.DecidedNote
    WHERE "Id" = leave_decide.Id;

    RETURN QUERY
    SELECT lr."Id", lr."TenantId", lr."RequesterId", lr."ChildId", lr."Type", lr."FromDate", lr."ToDate",
           lr."Reason", lr."Substitute", lr."Status", lr."AppliedOn", lr."DecidedNote", lr."Priority",
           lr."AttachmentUrls", u."Name", d."Name"
    FROM "dbo"."LeaveRequests" lr
    LEFT JOIN "dbo"."Users" u ON u."Id" = lr."RequesterId"
    LEFT JOIN "dbo"."Users" d ON d."Id" = lr."DecidedBy"
    WHERE lr."Id" = leave_decide.Id;
END;
$$;

-- ============================================================
-- PayrollRun / PayrollRunLine
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.payrollrunline_listbyperiod(TenantId uuid, Period varchar(7))
RETURNS TABLE (
    "PersonType" varchar(10), "PersonId" uuid, "Name" varchar(200), "Role" varchar(120), "Dept" varchar(120),
    "Basic" numeric(18,2), "Hra" numeric(18,2), "Allowances" numeric(18,2), "Epf" numeric(18,2),
    "ProfTax" numeric(18,2), "OtherDeductions" numeric(18,2), "Gross" numeric(18,2), "Deductions" numeric(18,2),
    "Net" numeric(18,2)
)
LANGUAGE sql
AS $$
    SELECT l."PersonType", l."PersonId", l."Name", l."Role", l."Dept",
           l."Basic", l."Hra", l."Allowances", l."Epf", l."ProfTax", l."OtherDeductions",
           l."Gross", l."Deductions", l."Net"
    FROM "dbo"."PayrollRunLines" l
    JOIN "dbo"."PayrollRuns" r ON r."Id" = l."RunId" AND r."TenantId" = l."TenantId"
    WHERE r."TenantId" = payrollrunline_listbyperiod.TenantId AND r."Period" = payrollrunline_listbyperiod.Period
    ORDER BY l."Net" DESC;
$$;

CREATE OR REPLACE FUNCTION dbo.payrollrun_approve(TenantId uuid, Period varchar(7), ApprovedBy uuid)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Period" varchar(7), "Year" int, "Month" varchar(20), "Status" varchar(20),
    "StaffCount" int, "Gross" numeric(18,2), "Deductions" numeric(18,2), "Net" numeric(18,2),
    "RunBy" uuid, "RunAt" timestamptz, "ApprovedBy" uuid, "ApprovedAt" timestamptz
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE "dbo"."PayrollRuns"
       SET "Status" = 'approved', "ApprovedBy" = payrollrun_approve.ApprovedBy, "ApprovedAt" = now()
     WHERE "TenantId" = payrollrun_approve.TenantId AND "Period" = payrollrun_approve.Period AND "Status" = 'run';

    RETURN QUERY
    SELECT "Id", "TenantId", "Period", "Year", "Month", "Status", "StaffCount", "Gross", "Deductions", "Net",
           "RunBy", "RunAt", "ApprovedBy", "ApprovedAt"
    FROM "dbo"."PayrollRuns"
    WHERE "TenantId" = payrollrun_approve.TenantId AND "Period" = payrollrun_approve.Period;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.payrollrun_get(TenantId uuid, Period varchar(7))
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Period" varchar(7), "Year" int, "Month" varchar(20), "Status" varchar(20),
    "StaffCount" int, "Gross" numeric(18,2), "Deductions" numeric(18,2), "Net" numeric(18,2),
    "RunBy" uuid, "RunAt" timestamptz, "ApprovedBy" uuid, "ApprovedAt" timestamptz
)
LANGUAGE sql
AS $$
    SELECT "Id", "TenantId", "Period", "Year", "Month", "Status", "StaffCount", "Gross", "Deductions", "Net",
           "RunBy", "RunAt", "ApprovedBy", "ApprovedAt"
    FROM "dbo"."PayrollRuns" WHERE "TenantId" = payrollrun_get.TenantId AND "Period" = payrollrun_get.Period;
$$;

-- Lines is a jsonb-serialized array (C# sends JsonSerializer.Serialize(lines) as plain text, same
-- shape as fee_summarybytenants' TenantIds), decomposed with jsonb_to_recordset -- the structured
-- per-row insert equivalent of SQL Server's OPENJSON ... WITH (...).
CREATE OR REPLACE FUNCTION dbo.payrollrun_save(
    TenantId uuid, Period varchar(7), Year int, Month varchar(20), StaffCount int,
    Gross numeric(18,2), Deductions numeric(18,2), Net numeric(18,2), RunBy uuid, Lines text
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Period" varchar(7), "Year" int, "Month" varchar(20), "Status" varchar(20),
    "StaffCount" int, "Gross" numeric(18,2), "Deductions" numeric(18,2), "Net" numeric(18,2),
    "RunBy" uuid, "RunAt" timestamptz, "ApprovedBy" uuid, "ApprovedAt" timestamptz
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_run_id uuid;
BEGIN
    SELECT "Id" INTO v_run_id FROM "dbo"."PayrollRuns"
    WHERE "TenantId" = payrollrun_save.TenantId AND "Period" = payrollrun_save.Period;

    IF v_run_id IS NULL THEN
        v_run_id := gen_random_uuid();
        INSERT INTO "dbo"."PayrollRuns"
            ("Id", "TenantId", "Period", "Year", "Month", "Status", "StaffCount", "Gross", "Deductions", "Net", "RunBy", "RunAt")
        VALUES (v_run_id, payrollrun_save.TenantId, payrollrun_save.Period, COALESCE(payrollrun_save.Year, 0),
                payrollrun_save.Month, 'run', COALESCE(payrollrun_save.StaffCount, 0),
                COALESCE(payrollrun_save.Gross, 0), COALESCE(payrollrun_save.Deductions, 0),
                COALESCE(payrollrun_save.Net, 0), payrollrun_save.RunBy, now());
    ELSE
        UPDATE "dbo"."PayrollRuns"
           SET "Year" = COALESCE(payrollrun_save.Year, 0), "Month" = payrollrun_save.Month, "Status" = 'run',
               "StaffCount" = COALESCE(payrollrun_save.StaffCount, 0), "Gross" = COALESCE(payrollrun_save.Gross, 0),
               "Deductions" = COALESCE(payrollrun_save.Deductions, 0), "Net" = COALESCE(payrollrun_save.Net, 0),
               "RunBy" = payrollrun_save.RunBy, "RunAt" = now(), "ApprovedBy" = NULL, "ApprovedAt" = NULL
         WHERE "Id" = v_run_id;
        DELETE FROM "dbo"."PayrollRunLines" WHERE "RunId" = v_run_id AND "TenantId" = payrollrun_save.TenantId;
    END IF;

    IF payrollrun_save.Lines IS NOT NULL AND length(payrollrun_save.Lines) > 2 THEN
        INSERT INTO "dbo"."PayrollRunLines"
            ("TenantId", "RunId", "PersonType", "PersonId", "Name", "Role", "Dept",
             "Basic", "Hra", "Allowances", "Epf", "ProfTax", "OtherDeductions", "Gross", "Deductions", "Net")
        SELECT payrollrun_save.TenantId, v_run_id, j."personType", j."personId", j."name", j."role", j."dept",
               j."basic", j."hra", j."allowances", j."epf", j."profTax", j."otherDeductions",
               j."gross", j."deductions", j."net"
        FROM jsonb_to_recordset(payrollrun_save.Lines::jsonb) AS j(
            "personType" varchar(10), "personId" uuid, "name" varchar(200), "role" varchar(120), "dept" varchar(120),
            "basic" numeric(18,2), "hra" numeric(18,2), "allowances" numeric(18,2), "epf" numeric(18,2),
            "profTax" numeric(18,2), "otherDeductions" numeric(18,2), "gross" numeric(18,2),
            "deductions" numeric(18,2), "net" numeric(18,2)
        );
    END IF;

    RETURN QUERY
    SELECT "Id", "TenantId", "Period", "Year", "Month", "Status", "StaffCount", "Gross", "Deductions", "Net",
           "RunBy", "RunAt", "ApprovedBy", "ApprovedAt"
    FROM "dbo"."PayrollRuns" WHERE "Id" = v_run_id;
END;
$$;

-- ============================================================
-- PersonExtras
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.personextras_get(PersonType varchar(20), PersonId uuid)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "PersonType" varchar(20), "PersonId" uuid, "ExtrasJson" text, "UpdatedAt" timestamptz)
LANGUAGE sql
AS $$
    SELECT "Id", "TenantId", "PersonType", "PersonId", "ExtrasJson", "UpdatedAt"
    FROM "dbo"."PersonExtras"
    WHERE "PersonType" = personextras_get.PersonType AND "PersonId" = personextras_get.PersonId;
$$;

CREATE OR REPLACE FUNCTION dbo.personextras_upsert(TenantId uuid, PersonType varchar(20), PersonId uuid, ExtrasJson text)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "PersonType" varchar(20), "PersonId" uuid, "ExtrasJson" text, "UpdatedAt" timestamptz)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE "dbo"."PersonExtras" SET "ExtrasJson" = personextras_upsert.ExtrasJson, "UpdatedAt" = now()
    WHERE "TenantId" = personextras_upsert.TenantId AND "PersonType" = personextras_upsert.PersonType
      AND "PersonId" = personextras_upsert.PersonId;

    IF NOT FOUND THEN
        INSERT INTO "dbo"."PersonExtras" ("TenantId", "PersonType", "PersonId", "ExtrasJson")
        VALUES (personextras_upsert.TenantId, personextras_upsert.PersonType, personextras_upsert.PersonId,
                personextras_upsert.ExtrasJson);
    END IF;

    RETURN QUERY
    SELECT "Id", "TenantId", "PersonType", "PersonId", "ExtrasJson", "UpdatedAt"
    FROM "dbo"."PersonExtras"
    WHERE "TenantId" = personextras_upsert.TenantId AND "PersonType" = personextras_upsert.PersonType
      AND "PersonId" = personextras_upsert.PersonId;
END;
$$;

-- ============================================================
-- SalaryProfile / SalaryStructure
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.salaryprofile_list(TenantId uuid)
RETURNS TABLE (
    "TenantId" uuid, "PersonType" varchar(10), "PersonId" uuid, "BasicSalary" numeric(18,2), "Hra" numeric(18,2),
    "Allowances" numeric(18,2), "Epf" numeric(18,2), "ProfTax" numeric(18,2), "OtherDeductions" numeric(18,2),
    "Uan" varchar(40), "BankHolder" varchar(120), "BankAccount" varchar(40), "BankName" varchar(120),
    "Ifsc" varchar(20), "BankBranch" varchar(120)
)
LANGUAGE sql
AS $$
    SELECT "TenantId", "PersonType", "PersonId", "BasicSalary", "Hra", "Allowances", "Epf", "ProfTax",
           "OtherDeductions", "Uan", "BankHolder", "BankAccount", "BankName", "Ifsc", "BankBranch"
    FROM "dbo"."SalaryProfiles" WHERE "TenantId" = salaryprofile_list.TenantId;
$$;

CREATE OR REPLACE FUNCTION dbo.salaryprofile_upsert(
    TenantId uuid, PersonType varchar(10), PersonId uuid,
    BasicSalary numeric(18,2), Hra numeric(18,2), Allowances numeric(18,2),
    Epf numeric(18,2), ProfTax numeric(18,2), OtherDeductions numeric(18,2), Uan varchar(40),
    BankHolder varchar(120), BankAccount varchar(40), BankName varchar(120), Ifsc varchar(20), BankBranch varchar(120)
)
RETURNS TABLE (
    "TenantId" uuid, "PersonType" varchar(10), "PersonId" uuid, "BasicSalary" numeric(18,2), "Hra" numeric(18,2),
    "Allowances" numeric(18,2), "Epf" numeric(18,2), "ProfTax" numeric(18,2), "OtherDeductions" numeric(18,2),
    "Uan" varchar(40), "BankHolder" varchar(120), "BankAccount" varchar(40), "BankName" varchar(120),
    "Ifsc" varchar(20), "BankBranch" varchar(120)
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE "dbo"."SalaryProfiles"
       SET "BasicSalary" = COALESCE(salaryprofile_upsert.BasicSalary, 0), "Hra" = COALESCE(salaryprofile_upsert.Hra, 0),
           "Allowances" = COALESCE(salaryprofile_upsert.Allowances, 0), "Epf" = COALESCE(salaryprofile_upsert.Epf, 0),
           "ProfTax" = COALESCE(salaryprofile_upsert.ProfTax, 0),
           "OtherDeductions" = COALESCE(salaryprofile_upsert.OtherDeductions, 0),
           "Uan" = salaryprofile_upsert.Uan, "BankHolder" = salaryprofile_upsert.BankHolder,
           "BankAccount" = salaryprofile_upsert.BankAccount, "BankName" = salaryprofile_upsert.BankName,
           "Ifsc" = salaryprofile_upsert.Ifsc, "BankBranch" = salaryprofile_upsert.BankBranch, "UpdatedAt" = now()
     WHERE "TenantId" = salaryprofile_upsert.TenantId AND "PersonType" = salaryprofile_upsert.PersonType
       AND "PersonId" = salaryprofile_upsert.PersonId;

    IF NOT FOUND THEN
        INSERT INTO "dbo"."SalaryProfiles"
            ("TenantId", "PersonType", "PersonId", "BasicSalary", "Hra", "Allowances", "Epf", "ProfTax",
             "OtherDeductions", "Uan", "BankHolder", "BankAccount", "BankName", "Ifsc", "BankBranch")
        VALUES (salaryprofile_upsert.TenantId, salaryprofile_upsert.PersonType, salaryprofile_upsert.PersonId,
                COALESCE(salaryprofile_upsert.BasicSalary, 0), COALESCE(salaryprofile_upsert.Hra, 0),
                COALESCE(salaryprofile_upsert.Allowances, 0), COALESCE(salaryprofile_upsert.Epf, 0),
                COALESCE(salaryprofile_upsert.ProfTax, 0), COALESCE(salaryprofile_upsert.OtherDeductions, 0),
                salaryprofile_upsert.Uan, salaryprofile_upsert.BankHolder, salaryprofile_upsert.BankAccount,
                salaryprofile_upsert.BankName, salaryprofile_upsert.Ifsc, salaryprofile_upsert.BankBranch);
    END IF;

    RETURN QUERY
    SELECT "TenantId", "PersonType", "PersonId", "BasicSalary", "Hra", "Allowances", "Epf", "ProfTax",
           "OtherDeductions", "Uan", "BankHolder", "BankAccount", "BankName", "Ifsc", "BankBranch"
    FROM "dbo"."SalaryProfiles"
    WHERE "TenantId" = salaryprofile_upsert.TenantId AND "PersonType" = salaryprofile_upsert.PersonType
      AND "PersonId" = salaryprofile_upsert.PersonId;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.salarystructure_list(TenantId uuid)
RETURNS TABLE (
    "TenantId" uuid, "PersonType" varchar(10), "RoleKey" varchar(120), "Basic" numeric(18,2), "Hra" numeric(18,2),
    "Allowances" numeric(18,2), "Epf" numeric(18,2), "ProfTax" numeric(18,2), "OtherDeductions" numeric(18,2)
)
LANGUAGE sql
AS $$
    SELECT "TenantId", "PersonType", "RoleKey", "Basic", "Hra", "Allowances", "Epf", "ProfTax", "OtherDeductions"
    FROM "dbo"."SalaryStructures" WHERE "TenantId" = salarystructure_list.TenantId;
$$;

CREATE OR REPLACE FUNCTION dbo.salarystructure_upsert(
    TenantId uuid, PersonType varchar(10), RoleKey varchar(120),
    Basic numeric(18,2), Hra numeric(18,2), Allowances numeric(18,2),
    Epf numeric(18,2), ProfTax numeric(18,2), OtherDeductions numeric(18,2)
)
RETURNS TABLE (
    "TenantId" uuid, "PersonType" varchar(10), "RoleKey" varchar(120), "Basic" numeric(18,2), "Hra" numeric(18,2),
    "Allowances" numeric(18,2), "Epf" numeric(18,2), "ProfTax" numeric(18,2), "OtherDeductions" numeric(18,2)
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE "dbo"."SalaryStructures"
       SET "Basic" = COALESCE(salarystructure_upsert.Basic, 0), "Hra" = COALESCE(salarystructure_upsert.Hra, 0),
           "Allowances" = COALESCE(salarystructure_upsert.Allowances, 0), "Epf" = COALESCE(salarystructure_upsert.Epf, 0),
           "ProfTax" = COALESCE(salarystructure_upsert.ProfTax, 0),
           "OtherDeductions" = COALESCE(salarystructure_upsert.OtherDeductions, 0), "UpdatedAt" = now()
     WHERE "TenantId" = salarystructure_upsert.TenantId AND "PersonType" = salarystructure_upsert.PersonType
       AND "RoleKey" = salarystructure_upsert.RoleKey;

    IF NOT FOUND THEN
        INSERT INTO "dbo"."SalaryStructures"
            ("TenantId", "PersonType", "RoleKey", "Basic", "Hra", "Allowances", "Epf", "ProfTax", "OtherDeductions")
        VALUES (salarystructure_upsert.TenantId, salarystructure_upsert.PersonType, salarystructure_upsert.RoleKey,
                COALESCE(salarystructure_upsert.Basic, 0), COALESCE(salarystructure_upsert.Hra, 0),
                COALESCE(salarystructure_upsert.Allowances, 0), COALESCE(salarystructure_upsert.Epf, 0),
                COALESCE(salarystructure_upsert.ProfTax, 0), COALESCE(salarystructure_upsert.OtherDeductions, 0));
    END IF;

    RETURN QUERY
    SELECT "TenantId", "PersonType", "RoleKey", "Basic", "Hra", "Allowances", "Epf", "ProfTax", "OtherDeductions"
    FROM "dbo"."SalaryStructures"
    WHERE "TenantId" = salarystructure_upsert.TenantId AND "PersonType" = salarystructure_upsert.PersonType
      AND "RoleKey" = salarystructure_upsert.RoleKey;
END;
$$;

-- ============================================================
-- StaffDocument
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.staffdocument_create(
    TenantId uuid, StaffId uuid, Label varchar(120), Value varchar(200), Ok boolean DEFAULT NULL
)
RETURNS TABLE ("Id" uuid, "Label" varchar(120), "Value" varchar(200), "Ok" boolean)
LANGUAGE sql
AS $$
    INSERT INTO "dbo"."StaffDocuments" ("Id", "TenantId", "StaffId", "Label", "Value", "Ok", "CreatedAt")
    VALUES (gen_random_uuid(), TenantId, StaffId, Label, Value, Ok, now())
    RETURNING "Id", "Label", "Value", "Ok";
$$;

-- Guarded by StaffId + TenantId, not just Id -- same defense-in-depth as Trip_Start's tenant
-- scoping: a document id that exists but belongs to a different staff member or tenant matches
-- zero rows, so the caller sees 404.
CREATE OR REPLACE FUNCTION dbo.staffdocument_update(
    Id uuid, TenantId uuid, StaffId uuid, Label varchar(120), Value varchar(200), Ok boolean DEFAULT NULL
)
RETURNS TABLE ("Id" uuid, "Label" varchar(120), "Value" varchar(200), "Ok" boolean)
LANGUAGE sql
AS $$
    WITH updated AS (
        UPDATE "dbo"."StaffDocuments" SET "Label" = staffdocument_update.Label, "Value" = staffdocument_update.Value,
            "Ok" = staffdocument_update.Ok
        WHERE "Id" = staffdocument_update.Id AND "StaffId" = staffdocument_update.StaffId
          AND "TenantId" = staffdocument_update.TenantId
        RETURNING "Id", "Label", "Value", "Ok"
    )
    SELECT "Id", "Label", "Value", "Ok" FROM updated;
$$;

-- Resolves the caller's own Staff row from their login identity (Staff.UserId), same
-- identity-join pattern Trip_Start uses for conductors.
CREATE OR REPLACE FUNCTION dbo.staffdocuments_listforuser(TenantId uuid, UserId uuid)
RETURNS TABLE ("Id" uuid, "Label" varchar(120), "Value" varchar(200), "Ok" boolean)
LANGUAGE sql
AS $$
    SELECT d."Id", d."Label", d."Value", d."Ok"
    FROM "dbo"."StaffDocuments" d
    INNER JOIN "dbo"."Staff" s ON s."Id" = d."StaffId"
    WHERE s."UserId" = staffdocuments_listforuser.UserId AND s."TenantId" = staffdocuments_listforuser.TenantId
      AND d."TenantId" = staffdocuments_listforuser.TenantId
    ORDER BY d."CreatedAt";
$$;

-- ============================================================
-- Staff
-- TRY_CAST(SUBSTRING(...) AS int) -> a regex-guarded cast, same narrow approach as
-- 11_sis_procs.sql's student_create/10_tenancy_procs.sql's planupgraderequest_listbytenants.
--
-- staff_create's RETURNS TABLE ends in a CAST(NULL AS varchar(512)) "PhotoUrl" column: the live
-- SQL Server dev instance's OBJECT_DEFINITION() for Staff_Create lacked it, but
-- db/Sms.Migrations/M0105_Staff_Email.cs (the last migration to touch this proc, per
-- procs/staffingemail/Staff_Create.sql) added it specifically so StaffResponse's documented
-- 16-parameter secondary constructor (see StaffingContracts.cs) has a column to bind against --
-- the live extraction was stale relative to the migration history, not a source of truth here.
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.staff_create(
    TenantId uuid, Name varchar(200), Gender varchar(1), Role varchar(80),
    Category varchar(40), Department varchar(80), Phone varchar(40), Shift varchar(40),
    Route varchar(80), AvatarHue int, EmployeeCode varchar(64) DEFAULT NULL, Email varchar(256) DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Name" varchar(200), "Gender" varchar(1), "Role" varchar(80), "Category" varchar(40),
    "Department" varchar(80), "Phone" varchar(40), "Shift" varchar(40), "Route" varchar(80),
    "AttendancePct" numeric(5,2), "Status" varchar(20), "AvatarHue" int, "EmployeeCode" varchar(64), "Email" varchar(256),
    "PhotoUrl" varchar(512)
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_id uuid := gen_random_uuid();
    v_code varchar(64) := staff_create.EmployeeCode;
    v_slug varchar(48);
    v_prefix varchar(80);
    v_next int;
BEGIN
    IF v_code IS NULL OR trim(v_code) = '' THEN
        SELECT lower("Slug") INTO v_slug FROM "dbo"."Tenants" WHERE "Id" = staff_create.TenantId;
        IF v_slug IS NULL OR v_slug = '' THEN v_slug := 'sch'; END IF;

        v_prefix := v_slug || '-STF-';
        SELECT COALESCE(max((substring("EmployeeCode" FROM length(v_prefix) + 1))::int), 0) + 1
        INTO v_next
        FROM "dbo"."Staff"
        WHERE "TenantId" = staff_create.TenantId
          AND "EmployeeCode" LIKE v_prefix || '%'
          AND substring("EmployeeCode" FROM length(v_prefix) + 1) ~ '^\d+$';

        v_code := v_prefix || lpad(v_next::text, 4, '0');
    ELSE
        v_code := lower(trim(v_code));
    END IF;

    INSERT INTO "dbo"."Staff"
        ("Id", "TenantId", "Name", "Gender", "Role", "Category", "Department", "Phone", "Shift", "Route",
         "AvatarHue", "EmployeeCode", "Email")
    VALUES (v_id, staff_create.TenantId, staff_create.Name, staff_create.Gender, staff_create.Role,
            staff_create.Category, staff_create.Department, staff_create.Phone, staff_create.Shift,
            staff_create.Route, COALESCE(staff_create.AvatarHue, 0), v_code, staff_create.Email);

    UPDATE "dbo"."Tenants" SET "StaffCount" = (
        (SELECT count(*) FROM "dbo"."Teachers" te WHERE te."TenantId" = staff_create.TenantId AND te."Status" = 'active')
      + (SELECT count(*) FROM "dbo"."Staff" st WHERE st."TenantId" = staff_create.TenantId AND st."Status" = 'active')
    ) WHERE "Id" = staff_create.TenantId;

    RETURN QUERY
    SELECT "Id", "TenantId", "Name", "Gender", "Role", "Category", "Department", "Phone", "Shift", "Route",
           "AttendancePct", "Status", "AvatarHue", "EmployeeCode", "Email", CAST(NULL AS varchar(512))
    FROM "dbo"."Staff" WHERE "Id" = v_id;
END;
$$;

-- RETURNS int (GET DIAGNOSTICS ROW_COUNT), not RETURNS TABLE: called via BaseRepository's
-- ExecuteProcAsync (StaffRepository.UpdateAsync discards the row and re-fetches separately via
-- GetAsync), and ExecuteProcAsync always does `ExecuteScalarAsync<int>` against the call --
-- same convention as 09_auth_procs.sql's user_setemail/user_setpassword/user_setphoto/user_setstatus.
CREATE OR REPLACE FUNCTION dbo.staff_update(
    Id uuid, Name varchar(200), Role varchar(80), Category varchar(40),
    Department varchar(80), Phone varchar(40), Shift varchar(40), Route varchar(80), Status varchar(20),
    Email varchar(256) DEFAULT NULL, Gender varchar(1) DEFAULT NULL, EmployeeCode varchar(64) DEFAULT NULL
)
RETURNS int
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_tenant_id uuid; v_count int;
BEGIN
    UPDATE "dbo"."Staff" SET
        "Name" = COALESCE(staff_update.Name, "Name"), "Role" = COALESCE(staff_update.Role, "Role"),
        "Category" = COALESCE(staff_update.Category, "Category"),
        "Department" = COALESCE(staff_update.Department, "Department"), "Phone" = COALESCE(staff_update.Phone, "Phone"),
        "Shift" = COALESCE(staff_update.Shift, "Shift"), "Route" = COALESCE(staff_update.Route, "Route"),
        "Status" = COALESCE(staff_update.Status, "Status"), "Email" = COALESCE(staff_update.Email, "Email"),
        "Gender" = COALESCE(staff_update.Gender, "Gender"),
        "EmployeeCode" = COALESCE(staff_update.EmployeeCode, "EmployeeCode")
    WHERE "Id" = staff_update.Id;
    GET DIAGNOSTICS v_count = ROW_COUNT;

    SELECT "TenantId" INTO v_tenant_id FROM "dbo"."Staff" WHERE "Id" = staff_update.Id;
    IF v_tenant_id IS NOT NULL THEN
        UPDATE "dbo"."Tenants" SET "StaffCount" = (
            (SELECT count(*) FROM "dbo"."Teachers" te WHERE te."TenantId" = v_tenant_id AND te."Status" = 'active')
          + (SELECT count(*) FROM "dbo"."Staff" st WHERE st."TenantId" = v_tenant_id AND st."Status" = 'active')
        ) WHERE "Id" = v_tenant_id;
    END IF;

    RETURN v_count;
END;
$$;

-- BEGIN TRAN/ROLLBACK/COMMIT -> plpgsql function body (implicit transaction; RETURN mid-function
-- discards all writes made so far, same effect as the source's ROLLBACK TRAN + RETURN).
-- UPDLOCK/HOLDLOCK -> FOR UPDATE, same locking-primitive substitution as student_ensurelogin.
CREATE OR REPLACE FUNCTION dbo.staff_ensurelogin(Email varchar(256))
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Email" varchar(256), "StudentId" varchar(64), "Phone" varchar(32),
    "PasswordHash" varchar(512), "IsPlatform" boolean, "Status" varchar(20), "Name" varchar(200),
    "MustSetPassword" boolean, "CreatedAt" timestamptz, "PhotoUrl" text
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_norm varchar(256) := lower(trim(staff_ensurelogin.Email));
    v_staff_id uuid;
    v_tenant_id uuid;
    v_name varchar(200);
    v_status varchar(20);
    v_existing_user_id uuid;
    v_user_id uuid;
BEGIN
    IF v_norm IS NULL OR v_norm = '' THEN RETURN; END IF;

    SELECT s."Id", s."TenantId", s."Name", s."Status", s."UserId"
    INTO v_staff_id, v_tenant_id, v_name, v_status, v_existing_user_id
    FROM "dbo"."Staff" s
    WHERE s."Email" IS NOT NULL AND lower(trim(s."Email")) = v_norm
    ORDER BY s."CreatedAt"
    LIMIT 1
    FOR UPDATE OF s;

    IF v_staff_id IS NULL THEN RETURN; END IF;
    IF v_status = 'inactive' THEN RETURN; END IF;

    IF v_existing_user_id IS NOT NULL THEN
        v_user_id := v_existing_user_id;
    ELSE
        -- The email may already belong to a login in this tenant (e.g. a CRM account) - link
        -- that one instead of creating a duplicate.
        SELECT u."Id" INTO v_user_id
        FROM "dbo"."Users" u
        WHERE u."TenantId" = v_tenant_id AND u."Email" IS NOT NULL AND lower(trim(u."Email")) = v_norm
        ORDER BY u."CreatedAt"
        LIMIT 1;

        IF v_user_id IS NULL THEN
            v_user_id := gen_random_uuid();
            INSERT INTO "dbo"."Users" ("Id", "TenantId", "Email", "Phone", "IsPlatform", "Status", "StudentId", "MustSetPassword", "Name")
            VALUES (v_user_id, v_tenant_id, v_norm, NULL, false, 'active', NULL, true, v_name);
        END IF;

        IF NOT EXISTS (SELECT 1 FROM "dbo"."UserRoles" WHERE "UserId" = v_user_id AND "Role" = 'staff') THEN
            INSERT INTO "dbo"."UserRoles" ("UserId", "Role") VALUES (v_user_id, 'staff');
        END IF;

        UPDATE "dbo"."Staff" SET "UserId" = v_user_id WHERE "Id" = v_staff_id;
    END IF;

    RETURN QUERY
    SELECT u."Id", u."TenantId", u."Email", u."StudentId", u."Phone",
           u."PasswordHash", u."IsPlatform", u."Status", u."Name", u."MustSetPassword", u."CreatedAt", u."PhotoUrl"
    FROM "dbo"."Users" u WHERE u."Id" = v_user_id
    LIMIT 1;
END;
$$;

-- ============================================================
-- Teacher
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.teacher_create(
    TenantId uuid, Name varchar(200), Gender varchar(1), Department varchar(80),
    Designation varchar(80), SubjectsCsv varchar(400), ClassTeacher varchar(40),
    Phone varchar(40), Email varchar(256), Exp int, Rating numeric(4,2), Result numeric(5,2),
    Load int, AvatarHue int, Top boolean, EmployeeCode varchar(64) DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Name" varchar(200), "Gender" varchar(1), "Department" varchar(80),
    "Designation" varchar(80), "SubjectsCsv" varchar(400), "ClassTeacher" varchar(40), "Phone" varchar(40),
    "Email" varchar(256), "Exp" int, "Rating" numeric(4,2), "AttendancePct" numeric(5,2), "Result" numeric(5,2),
    "Load" int, "Status" varchar(20), "AvatarHue" int, "Top" boolean, "EmployeeCode" varchar(64),
    "PhotoUrl" varchar(512), "UserId" uuid
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_id uuid := gen_random_uuid();
    v_code varchar(64) := teacher_create.EmployeeCode;
    v_slug varchar(48);
    v_prefix varchar(80);
    v_next int;
BEGIN
    IF v_code IS NULL OR trim(v_code) = '' THEN
        SELECT lower("Slug") INTO v_slug FROM "dbo"."Tenants" WHERE "Id" = teacher_create.TenantId;
        IF v_slug IS NULL OR v_slug = '' THEN v_slug := 'sch'; END IF;

        v_prefix := v_slug || '-TCH-';
        SELECT COALESCE(max((substring("EmployeeCode" FROM length(v_prefix) + 1))::int), 0) + 1
        INTO v_next
        FROM "dbo"."Teachers"
        WHERE "TenantId" = teacher_create.TenantId
          AND "EmployeeCode" LIKE v_prefix || '%'
          AND substring("EmployeeCode" FROM length(v_prefix) + 1) ~ '^\d+$';

        v_code := v_prefix || lpad(v_next::text, 4, '0');
    ELSE
        v_code := lower(trim(v_code));
    END IF;

    INSERT INTO "dbo"."Teachers"
        ("Id", "TenantId", "Name", "Gender", "Department", "Designation", "SubjectsCsv", "ClassTeacher",
         "Phone", "Email", "Exp", "Rating", "Result", "Load", "AvatarHue", "Top", "EmployeeCode")
    VALUES (v_id, teacher_create.TenantId, teacher_create.Name, teacher_create.Gender, teacher_create.Department,
            teacher_create.Designation, teacher_create.SubjectsCsv, teacher_create.ClassTeacher,
            teacher_create.Phone, teacher_create.Email, COALESCE(teacher_create.Exp, 0),
            COALESCE(teacher_create.Rating, 0), COALESCE(teacher_create.Result, 0), COALESCE(teacher_create.Load, 0),
            COALESCE(teacher_create.AvatarHue, 0), COALESCE(teacher_create.Top, false), v_code);

    UPDATE "dbo"."Tenants" SET "StaffCount" = (
        (SELECT count(*) FROM "dbo"."Teachers" te WHERE te."TenantId" = teacher_create.TenantId AND te."Status" = 'active')
      + (SELECT count(*) FROM "dbo"."Staff" st WHERE st."TenantId" = teacher_create.TenantId AND st."Status" = 'active')
    ) WHERE "Id" = teacher_create.TenantId;

    RETURN QUERY
    SELECT "Id", "TenantId", "Name", "Gender", "Department", "Designation", "SubjectsCsv", "ClassTeacher", "Phone", "Email",
           "Exp", "Rating", "AttendancePct", "Result", "Load", "Status", "AvatarHue", "Top", "EmployeeCode",
           CAST(NULL AS varchar(512)), "UserId"
    FROM "dbo"."Teachers" WHERE "Id" = v_id;
END;
$$;

-- RETURNS int (GET DIAGNOSTICS ROW_COUNT), not RETURNS TABLE: same ExecuteProcAsync convention
-- as staff_update above (TeacherRepository.UpdateAsync also discards the row and re-fetches).
CREATE OR REPLACE FUNCTION dbo.teacher_update(
    Id uuid, Name varchar(200), Department varchar(80), Designation varchar(80),
    SubjectsCsv varchar(400), ClassTeacher varchar(40), Phone varchar(40), Email varchar(256),
    Status varchar(20), Gender varchar(1) DEFAULT NULL, Exp int DEFAULT NULL, EmployeeCode varchar(64) DEFAULT NULL
)
RETURNS int
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_tenant_id uuid; v_count int;
BEGIN
    UPDATE "dbo"."Teachers" SET
        "Name" = COALESCE(teacher_update.Name, "Name"), "Department" = COALESCE(teacher_update.Department, "Department"),
        "Designation" = COALESCE(teacher_update.Designation, "Designation"),
        "SubjectsCsv" = COALESCE(teacher_update.SubjectsCsv, "SubjectsCsv"),
        "ClassTeacher" = COALESCE(teacher_update.ClassTeacher, "ClassTeacher"),
        "Phone" = COALESCE(teacher_update.Phone, "Phone"), "Email" = COALESCE(teacher_update.Email, "Email"),
        "Status" = COALESCE(teacher_update.Status, "Status"), "Gender" = COALESCE(teacher_update.Gender, "Gender"),
        "Exp" = COALESCE(teacher_update.Exp, "Exp"),
        "EmployeeCode" = COALESCE(teacher_update.EmployeeCode, "EmployeeCode")
    WHERE "Id" = teacher_update.Id;
    GET DIAGNOSTICS v_count = ROW_COUNT;

    SELECT "TenantId" INTO v_tenant_id FROM "dbo"."Teachers" WHERE "Id" = teacher_update.Id;
    IF v_tenant_id IS NOT NULL THEN
        UPDATE "dbo"."Tenants" SET "StaffCount" = (
            (SELECT count(*) FROM "dbo"."Teachers" te WHERE te."TenantId" = v_tenant_id AND te."Status" = 'active')
          + (SELECT count(*) FROM "dbo"."Staff" st WHERE st."TenantId" = v_tenant_id AND st."Status" = 'active')
        ) WHERE "Id" = v_tenant_id;
    END IF;

    RETURN v_count;
END;
$$;
