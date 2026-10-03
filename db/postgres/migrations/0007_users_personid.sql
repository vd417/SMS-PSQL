-- 0007: identity groundwork. Adds a nullable dbo.Users.PersonId (uuid) plus supporting indexes so a
-- single real-world person can later be linked across several Users rows. Additive only: no
-- backfill, no constraint, no behavior change; nothing reads or writes the column yet. Existing
-- Users indexes (UX_Users_Tenant_Email, UX_Users_Tenant_Phone, UX_Users_PlatformAdmin) are untouched.
-- Rollback: DROP INDEX IF EXISTS dbo."IX_Users_Tenant_PersonId"; DROP INDEX IF EXISTS dbo."IX_Users_PersonId";
-- ALTER TABLE dbo."Users" DROP COLUMN "PersonId";

ALTER TABLE dbo."Users" ADD COLUMN IF NOT EXISTS "PersonId" uuid NULL;
CREATE INDEX IF NOT EXISTS "IX_Users_PersonId" ON dbo."Users" ("PersonId");
CREATE INDEX IF NOT EXISTS "IX_Users_Tenant_PersonId" ON dbo."Users" ("TenantId","PersonId");
