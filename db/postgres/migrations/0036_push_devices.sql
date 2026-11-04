-- Device tokens for phone and browser banners. One row per FCM token.
-- Rollback: DROP TABLE "dbo"."PushDevices";

CREATE TABLE IF NOT EXISTS "dbo"."PushDevices" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "Token" varchar(4096) NOT NULL,
    "Platform" varchar(16) NOT NULL,
    "App" varchar(16) NOT NULL,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS "UX_PushDevices_Token" ON "dbo"."PushDevices" ("Token");
CREATE INDEX IF NOT EXISTS "IX_PushDevices_User" ON "dbo"."PushDevices" ("UserId");

ALTER TABLE "dbo"."PushDevices" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."PushDevices" FORCE ROW LEVEL SECURITY;
CREATE POLICY "PushDevicesTenantPolicy_select" ON "dbo"."PushDevices" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PushDevicesTenantPolicy_update" ON "dbo"."PushDevices" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PushDevicesTenantPolicy_delete" ON "dbo"."PushDevices" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "PushDevicesTenantPolicy_insert" ON "dbo"."PushDevices" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

GRANT SELECT, INSERT, UPDATE, DELETE ON "dbo"."PushDevices" TO sms_app;
