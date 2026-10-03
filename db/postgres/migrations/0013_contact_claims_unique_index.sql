-- 0012: the gated UNIQUE index on dbo."ContactClaims" ("TenantId", "Kind", "NormalizedValue").
-- This is the "later, gated" index that 0010 and 0011 were written for. dbo.contact_claims_sync
-- already tolerates unique_violation on its INSERT, so this index adds the DB-level guarantee that
-- a (tenant, kind, normalized value) contact is claimed at most once, closing the concurrent-writer
-- race where two syncs could both insert a claim before either sees the other's row.
-- Additive: a new unique index only; no table, RLS, grant or function change. It requires the
-- existing ContactClaims rows to be free of (TenantId, Kind, NormalizedValue) duplicates, which they
-- are: every write goes through contact_claims_sync, which reuses or rejects rather than duplicating.
-- Rollback: DROP INDEX "dbo"."UX_ContactClaims_Tenant_Kind_Value";

CREATE UNIQUE INDEX IF NOT EXISTS "UX_ContactClaims_Tenant_Kind_Value"
    ON "dbo"."ContactClaims" ("TenantId", "Kind", "NormalizedValue");
