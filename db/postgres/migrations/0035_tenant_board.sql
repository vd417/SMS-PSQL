-- School education board (CBSE / ICSE / State Board / or a custom name) captured at
-- school create / onboarding and editable on the school profile. One board per school.
-- Idempotent: safe to apply by hand while the 0021 checksum blocks PgMigrator.

ALTER TABLE "dbo"."Tenants" ADD COLUMN IF NOT EXISTS "Board" varchar(60) NULL;
