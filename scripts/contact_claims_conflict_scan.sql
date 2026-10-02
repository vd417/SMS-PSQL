-- contact_claims_conflict_scan.sql — READ-ONLY pre-deploy gate for 0014_contact_claims_backfill.sql.
--
-- The backfill (and the already-present unique index 0012) require that, within a tenant, no
-- normalized email/phone is used as an OWN contact by two DIFFERENT student/teacher/staff profiles.
-- This query reports every such genuine conflict. If it returns ANY row, STOP: do NOT run the
-- backfill / deploy. Reconcile the duplicated contact with the school first (one of the people must
-- change theirs). Never auto-resolve by picking a winner.
--
-- Mirrors exactly what dbo.contact_claims_backfill() would claim: own contacts only — student Email
-- (no own phone), teacher/staff Email + Phone. Guardian email/phone are denormalized and never
-- claimed, so they are intentionally excluded here too.
--
-- Run against the target database with platform visibility so RLS does not hide other tenants' rows:
--   psql "<conn>" -v ON_ERROR_STOP=1 -c "SET app.is_platform='1';" -f scripts/contact_claims_conflict_scan.sql
-- (requires dbo.normalize_email / dbo.normalize_phone from migration 0009).

SET app.is_platform = '1';

WITH owned AS (
    SELECT "TenantId" AS tenant, 'email'::text AS kind, dbo.normalize_email("Email") AS val,
           'student'::text AS owner_type, "Id"::text AS owner_id
      FROM dbo."Students"
    UNION ALL
    SELECT "TenantId", 'email', dbo.normalize_email("Email"), 'teacher', "Id"::text
      FROM dbo."Teachers"
    UNION ALL
    SELECT "TenantId", 'phone', dbo.normalize_phone("Phone"), 'teacher', "Id"::text
      FROM dbo."Teachers"
    UNION ALL
    SELECT "TenantId", 'email', dbo.normalize_email("Email"), 'staff', "Id"::text
      FROM dbo."Staff"
    UNION ALL
    SELECT "TenantId", 'phone', dbo.normalize_phone("Phone"), 'staff', "Id"::text
      FROM dbo."Staff"
)
SELECT tenant,
       kind,
       val                                           AS normalized_value,
       count(DISTINCT owner_type || ':' || owner_id) AS distinct_owners,
       string_agg(DISTINCT owner_type || ':' || owner_id, ', ' ORDER BY owner_type || ':' || owner_id) AS owners
  FROM owned
 WHERE val IS NOT NULL
 GROUP BY tenant, kind, val
HAVING count(DISTINCT owner_type || ':' || owner_id) > 1
 ORDER BY tenant, kind, val;
