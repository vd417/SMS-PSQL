-- School outbound mail identity on dbo.Tenants.
-- EmailEnabled + EmailFromAddress means OTP, invites, reports and notices
-- are sent from the school. Otherwise the platform (Catre) From is used.
-- Additive: existing rows get EmailEnabled = false and NULL address columns.

ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "EmailEnabled" boolean NOT NULL DEFAULT false;
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "EmailFromName" varchar(120) NULL;
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "EmailFromAddress" varchar(200) NULL;
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "EmailReplyTo" varchar(200) NULL;
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "EmailReceiptTemplate" text NULL;
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "EmailReminderTemplate" text NULL;
