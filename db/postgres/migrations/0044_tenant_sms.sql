-- Per-school SMS sender. SmsEnabled + a 6-character sender id means
-- announcements, bus notices and invite texts use that school's sender.
-- Otherwise the platform (Catre) sender is used.
-- Additive: existing rows stay off with a NULL sender id.

ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "SmsEnabled" boolean NOT NULL DEFAULT false;
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "SmsSenderId" varchar(6) NULL;
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "SmsReceiptTemplate" text NULL;
ALTER TABLE dbo."Tenants" ADD COLUMN IF NOT EXISTS "SmsReminderTemplate" text NULL;

-- Rollback:
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "SmsReminderTemplate";
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "SmsReceiptTemplate";
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "SmsSenderId";
--   ALTER TABLE dbo."Tenants" DROP COLUMN IF EXISTS "SmsEnabled";
