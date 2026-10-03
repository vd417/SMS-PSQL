-- 0014: backfill dbo."ContactClaims" from the contacts of existing student/teacher/staff profiles.
--
-- Rows created before contact-uniqueness enforcement (B5) existed hold no claim, so their emails /
-- phones are not yet protected: a new person could grab a contact a pre-feature profile already
-- uses. This migration registers those contacts.
--
-- Design note (why replay the sync rather than a bulk INSERT, and why profiles only):
--   * At runtime ONLY student/teacher/staff create+update claim contacts, always with PersonId
--     NULL (see dbo.student_create / teacher_create / staff_create). There is no 'user' owner claim
--     and PersonId is unused in the claim path. The sole "this is my own claim" escape in
--     dbo.contact_claims_sync is matching (OwnerType, OwnerId); the person-reuse branch only fires
--     when BOTH PersonIds are non-null, which never happens at runtime. So a backfilled claim MUST
--     carry the same profile owner identity runtime uses. Backfilling 'user' OWNER rows (as an
--     earlier draft of the plan suggested) would be incorrect: a later legitimate profile update
--     (owner 'teacher'/'staff'/'student', PersonId NULL) would find the claim under a different
--     owner, fail the owner match, and raise a bogus SMSDC conflict. (Carrying a PersonId on a
--     profile claim would be harmless on its own — an own-owner match ignores PersonId — but is
--     pointless since runtime never sets it; this backfill passes NULL to mirror runtime exactly.)
--   * Replaying dbo.contact_claims_sync per owner reuses the audited insert/reuse/conflict logic and
--     reproduces exactly the claims runtime would have made — no duplicated SQL to keep in sync.
--   * Own contacts only: a student's own Email (students have no own-phone column); a teacher's /
--     staff's Email + Phone. Guardian email/phone are denormalized and are never claimed.
--
-- Idempotent: re-running syncs each owner's own claim (same OwnerType+OwnerId) = no-op, no dup.
-- Self-guarding: two DIFFERENT profiles sharing a normalized contact in one tenant make the second
-- sync RAISE SQLSTATE 'SMSDC', which aborts this migration. That is the intended gated STOP against
-- pre-existing conflicts (the unique index 0012 already exists), surfacing the clash for human
-- reconciliation instead of silently picking a winner. Run scripts/contact_claims_conflict_scan.sql
-- first to find any such conflict before deploying.
--
-- Privilege note: this migration runs as the schema owner (which bypasses the FORCE'd RLS on
-- ContactClaims and the source tables), so its cross-tenant SELECTs and INSERTs work — the same
-- supported path the cross-tenant 0008 PersonId backfill already uses. A MANUAL later re-run of
-- `SELECT dbo.contact_claims_backfill();` MUST therefore be done as a BYPASSRLS/superuser role (or
-- after `SET app.is_platform='1';`); run as an ordinary app role it would silently see zero source
-- rows (RLS filters them) and claim nothing. The function is deliberately NOT self-elevating:
-- adding an in-body `SET app.is_platform` to a SECURITY INVOKER function callable by PUBLIC would be
-- a cross-tenant escalation foot-gun.
--
-- Rollback: DROP FUNCTION dbo.contact_claims_backfill(); the backfilled rows stay (they are
-- indistinguishable from runtime-created claims and dropping them would un-protect live contacts).

CREATE OR REPLACE FUNCTION dbo.contact_claims_backfill()
RETURNS integer
LANGUAGE plpgsql
AS $$
DECLARE
    r record;
    n integer := 0;
BEGIN
    FOR r IN SELECT "TenantId" AS tenant, "Id"::text AS owner_id, "Email" AS email
               FROM dbo."Students" LOOP
        PERFORM dbo.contact_claims_sync(r.tenant, 'student', r.owner_id, NULL, r.email, NULL);
        n := n + 1;
    END LOOP;

    FOR r IN SELECT "TenantId" AS tenant, "Id"::text AS owner_id, "Email" AS email, "Phone" AS phone
               FROM dbo."Teachers" LOOP
        PERFORM dbo.contact_claims_sync(r.tenant, 'teacher', r.owner_id, NULL, r.email, r.phone);
        n := n + 1;
    END LOOP;

    FOR r IN SELECT "TenantId" AS tenant, "Id"::text AS owner_id, "Email" AS email, "Phone" AS phone
               FROM dbo."Staff" LOOP
        PERFORM dbo.contact_claims_sync(r.tenant, 'staff', r.owner_id, NULL, r.email, r.phone);
        n := n + 1;
    END LOOP;

    RETURN n;
END;
$$;

-- Run it once now against the existing data. On a fresh database this processes zero rows.
SELECT dbo.contact_claims_backfill();
