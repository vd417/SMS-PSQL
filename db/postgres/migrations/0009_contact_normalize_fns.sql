-- 0009: contact normalization helper functions (additive).
-- dbo.normalize_email / dbo.normalize_phone canonicalise contact values for the later
-- contact-uniqueness work. Nothing calls them yet: no behavior change, no table touched.
-- Rollback: DROP FUNCTION dbo.normalize_email(text); DROP FUNCTION dbo.normalize_phone(text);
CREATE OR REPLACE FUNCTION dbo.normalize_email(p text) RETURNS text
  LANGUAGE sql IMMUTABLE AS $$ SELECT nullif(lower(btrim(p)), '') $$;

CREATE OR REPLACE FUNCTION dbo.normalize_phone(p text) RETURNS text
  LANGUAGE sql IMMUTABLE AS $$
  SELECT nullif(
    (WITH d AS (SELECT regexp_replace(coalesce(p,''),'\D','','g') AS x)
     SELECT CASE
       WHEN length(x)=12 AND left(x,2)='91' THEN right(x,10)
       WHEN length(x)=11 AND left(x,1)='0'  THEN right(x,10)
       WHEN length(x)>=10 THEN right(x,10)
       ELSE x END
     FROM d), '') $$;
