-- 0005: dbo.ParentDevices — Expo push tokens for parent-app device push (Increment #2).
-- Tenant-scoped RLS (same 4-policy pattern as every other table). User isolation is enforced in
-- repo queries (WHERE "UserId" = ...), matching the codebase (there is no user-level RLS).
-- UNIQUE ("TenantId","ExpoPushToken") is deliberately NOT global: a device shared across schools
-- registers once per tenant, and upsert stays within the current tenant so it is RLS-safe.

CREATE TABLE "dbo"."ParentDevices" (
    "Id" uuid DEFAULT gen_random_uuid() NOT NULL,
    "TenantId" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "ExpoPushToken" text NOT NULL,
    "Platform" text NOT NULL,
    "CreatedAt" timestamptz DEFAULT now() NOT NULL,
    "UpdatedAt" timestamptz DEFAULT now() NOT NULL
);

ALTER TABLE "dbo"."ParentDevices" ADD CONSTRAINT "PK_ParentDevices" PRIMARY KEY ("Id");
CREATE UNIQUE INDEX "UX_ParentDevices_Tenant_Token" ON "dbo"."ParentDevices" ("TenantId", "ExpoPushToken");
CREATE INDEX "IX_ParentDevices_Tenant_User" ON "dbo"."ParentDevices" ("TenantId", "UserId");

ALTER TABLE "dbo"."ParentDevices" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."ParentDevices" FORCE ROW LEVEL SECURITY;
CREATE POLICY "ParentDevicesTenantPolicy_select" ON "dbo"."ParentDevices" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ParentDevicesTenantPolicy_update" ON "dbo"."ParentDevices" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ParentDevicesTenantPolicy_delete" ON "dbo"."ParentDevices" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ParentDevicesTenantPolicy_insert" ON "dbo"."ParentDevices" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

GRANT SELECT, INSERT, UPDATE, DELETE ON "dbo"."ParentDevices" TO sms_app;
