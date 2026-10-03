-- 0010: dbo.ContactClaims, the per-tenant contact-uniqueness ledger (one row per normalized
-- email/phone claimed by a user/student/teacher/staff owner). Additive only: a new table, an
-- ordinary index, RLS policies and grants.
-- NO unique index yet (UX_ContactClaims_Tenant_Kind_Value is a later, gated migration) and
-- nothing writes this table yet.
-- Rollback: DROP TABLE "dbo"."ContactClaims";

CREATE TABLE IF NOT EXISTS "dbo"."ContactClaims" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId" uuid NOT NULL,
    "Kind" varchar(10) NOT NULL CHECK ("Kind" IN ('email', 'phone')),
    "NormalizedValue" varchar(320) NOT NULL,
    "OwnerType" varchar(16) NOT NULL,   -- user | student | teacher | staff
    "OwnerId" varchar(64) NOT NULL,
    "PersonId" uuid NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS "IX_ContactClaims_Person" ON "dbo"."ContactClaims" ("PersonId");

ALTER TABLE "dbo"."ContactClaims" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."ContactClaims" FORCE ROW LEVEL SECURITY;
CREATE POLICY "ContactClaimsTenantPolicy_select" ON "dbo"."ContactClaims" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ContactClaimsTenantPolicy_update" ON "dbo"."ContactClaims" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ContactClaimsTenantPolicy_delete" ON "dbo"."ContactClaims" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ContactClaimsTenantPolicy_insert" ON "dbo"."ContactClaims" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

-- 99_app_role_grants.sql ran before this table existed.
GRANT SELECT, INSERT, UPDATE, DELETE ON "dbo"."ContactClaims" TO sms_app;
