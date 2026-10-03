-- 0008: PersonId backfill (corrected rule; StudentId is NOT a merge key).
-- Groups Users rows by normalized email (else normalized phone) ONLY when corroborated by an
-- identical non-blank Name (blank name compatible). Ambiguous (same key, >1 distinct non-blank name)
-- => left NULL for manual review. No-contact rows => own PersonId. Idempotent: fills NULLs only.
-- Data-only; touches no other table; guardian fields never used.
-- Rollback (data-only, non-destructive): UPDATE dbo."Users" SET "PersonId"=NULL WHERE "PersonId" IS NOT NULL;
UPDATE dbo."Users" u
SET "PersonId" = a.pid
FROM (
  WITH keyed AS (
    SELECT "Id" AS uid, nullif(lower(btrim("Name")),'') AS nname,
           coalesce(nullif(lower(btrim("Email")),''),
             CASE WHEN nullif(right(regexp_replace(coalesce("Phone",''),'\D','','g'),10),'') IS NOT NULL
                  THEN 'phone:'||right(regexp_replace(coalesce("Phone",''),'\D','','g'),10) END) AS gkey
    FROM dbo."Users" WHERE "IsPlatform"=false AND "PersonId" IS NULL
  ),
  grp AS (
    SELECT gkey, count(DISTINCT nname) FILTER (WHERE nname IS NOT NULL) AS dn
    FROM keyed WHERE gkey IS NOT NULL GROUP BY gkey
  ),
  gmap AS ( SELECT gkey, gen_random_uuid() AS pid FROM grp WHERE dn <= 1 )
  SELECT k.uid,
         CASE WHEN k.gkey IS NULL THEN gen_random_uuid()
              WHEN gm.pid IS NOT NULL THEN gm.pid
              ELSE NULL END AS pid
  FROM keyed k LEFT JOIN gmap gm ON gm.gkey = k.gkey
) a
WHERE u."Id" = a.uid AND a.pid IS NOT NULL;
