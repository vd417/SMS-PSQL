-- Per-school SMTP and notification switches. Existing From/template columns stay.
-- SMTP password is stored only as ciphertext written by the API. Delivery rows are
-- tenant-scoped so one school cannot read another's mail log.
-- Rollback:
--   DROP TABLE IF EXISTS "dbo"."EmailDeliveryLogs";
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "EmailLastError";
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "EmailLastTestAt";
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "EmailStatus";
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "EmailOverdueTemplate";
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "NotifyGeneral";
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "NotifyFeeOverdue";
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "NotifyFeeDue";
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "NotifyFeeReceipt";
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "NotifyForgotOtp";
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "NotifyLoginOtp";
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "SmtpPasswordEncrypted";
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "SmtpUsername";
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "SmtpEncryption";
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "SmtpPort";
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "SmtpHost";
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "EmailProvider";

ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "EmailProvider" varchar(32) NOT NULL DEFAULT 'custom';
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "SmtpHost" varchar(200) NULL;
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "SmtpPort" int NOT NULL DEFAULT 587;
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "SmtpEncryption" varchar(16) NOT NULL DEFAULT 'starttls';
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "SmtpUsername" varchar(200) NULL;
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "SmtpPasswordEncrypted" text NULL;
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "NotifyLoginOtp" boolean NOT NULL DEFAULT true;
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "NotifyForgotOtp" boolean NOT NULL DEFAULT true;
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "NotifyFeeReceipt" boolean NOT NULL DEFAULT true;
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "NotifyFeeDue" boolean NOT NULL DEFAULT true;
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "NotifyFeeOverdue" boolean NOT NULL DEFAULT true;
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "NotifyGeneral" boolean NOT NULL DEFAULT true;
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "EmailOverdueTemplate" text NULL;
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "EmailStatus" varchar(20) NOT NULL DEFAULT 'not_configured';
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "EmailLastTestAt" timestamptz NULL;
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "EmailLastError" varchar(500) NULL;

CREATE TABLE IF NOT EXISTS "dbo"."EmailDeliveryLogs" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId" uuid NOT NULL,
    "Recipient" varchar(256) NOT NULL,
    "Sender" varchar(256) NOT NULL,
    "EmailType" varchar(40) NOT NULL,
    "Subject" varchar(300) NOT NULL,
    "SentAt" timestamptz NOT NULL DEFAULT now(),
    "Status" varchar(20) NOT NULL,
    "RetryCount" int NOT NULL DEFAULT 0,
    "ErrorMessage" varchar(500) NULL
);

CREATE INDEX IF NOT EXISTS "IX_EmailDeliveryLogs_Tenant_Sent" ON "dbo"."EmailDeliveryLogs" ("TenantId", "SentAt" DESC);

ALTER TABLE "dbo"."EmailDeliveryLogs" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."EmailDeliveryLogs" FORCE ROW LEVEL SECURITY;
CREATE POLICY "EmailDeliveryLogs_select" ON "dbo"."EmailDeliveryLogs" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "EmailDeliveryLogs_insert" ON "dbo"."EmailDeliveryLogs" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

GRANT SELECT, INSERT ON "dbo"."EmailDeliveryLogs" TO sms_app;
