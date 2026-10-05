-- 0018: hard backstop against duplicate per-contact threads.
--
-- dbo.thread_create (0017) already prevents duplicates via an advisory lock + identity adoption,
-- but a unique index guarantees it even against a future code path that inserts directly. One
-- 1:1 conversation per (tenant, owner, contact); group threads (IsGroup = true) are exempt and
-- NULL-ContactUserId legacy threads are not constrained (NULLs are distinct anyway).
--
-- ORDERING REQUIREMENT: this index creation FAILS if any (tenant, owner, contact) currently has
-- more than one thread. Run the duplicate-thread reconciliation (see the ops merge script /
-- global dedup) on every tenant BEFORE applying this migration. Pre-check:
--
--   SELECT "TenantId","OwnerUserId","ContactUserId", count(*)
--   FROM "dbo"."ChatThreads"
--   WHERE "ContactUserId" IS NOT NULL AND "IsGroup" = false
--   GROUP BY 1,2,3 HAVING count(*) > 1;
--
-- Rollback: DROP INDEX IF EXISTS "dbo"."UQ_ChatThreads_OwnerContact";

CREATE UNIQUE INDEX IF NOT EXISTS "UQ_ChatThreads_OwnerContact"
ON "dbo"."ChatThreads" ("TenantId", "OwnerUserId", "ContactUserId")
WHERE "ContactUserId" IS NOT NULL AND "IsGroup" = false;
