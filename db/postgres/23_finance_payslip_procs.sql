-- Finance/Payslip proc (unquoted identifiers so Postgres case-folding matches existing
-- call sites with zero C# changes, per convention established in 09_auth_procs.sql).

CREATE OR REPLACE FUNCTION dbo.payslip_create(
    tenantid uuid, userid uuid, month varchar(20), year int,
    gross numeric(18,2), deductions numeric(18,2), net numeric(18,2)
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "UserId" uuid, "Month" varchar(20), "Year" int,
    "Gross" numeric(18,2), "Deductions" numeric(18,2), "Net" numeric(18,2), "Status" varchar(20),
    "Basic" numeric(18,2), "Hra" numeric(18,2), "Allowances" numeric(18,2),
    "Epf" numeric(18,2), "ProfTax" numeric(18,2), "OtherDeductions" numeric(18,2)
)
LANGUAGE plpgsql
AS $$
DECLARE
    new_id uuid := gen_random_uuid();
BEGIN
    INSERT INTO "dbo"."Payslips" ("Id", "TenantId", "UserId", "Month", "Year", "Gross", "Deductions", "Net")
    VALUES (
        new_id, tenantid, userid, month, COALESCE(year, 0),
        COALESCE(gross, 0), COALESCE(deductions, 0), COALESCE(net, 0)
    );

    RETURN QUERY
    SELECT p."Id", p."TenantId", p."UserId", p."Month", p."Year", p."Gross", p."Deductions", p."Net", p."Status",
           p."Basic", p."Hra", p."Allowances", p."Epf", p."ProfTax", p."OtherDeductions"
    FROM "dbo"."Payslips" p
    WHERE p."Id" = new_id;
END;
$$;
