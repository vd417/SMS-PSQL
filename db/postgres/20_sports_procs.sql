-- Sports module procs (unquoted identifiers so Postgres case-folding matches existing
-- call sites with zero C# changes, per convention established in 09_auth_procs.sql).

CREATE OR REPLACE FUNCTION dbo.sportsteam_create(
    tenantid uuid, name varchar(80), sport varchar(60),
    coach varchar(120) DEFAULT NULL, athletes int DEFAULT 0
)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "Name" varchar(80), "Sport" varchar(60), "Coach" varchar(120), "Athletes" int)
LANGUAGE plpgsql
AS $$
DECLARE
    new_id uuid;
BEGIN
    INSERT INTO "dbo"."SportsTeams" ("TenantId", "Name", "Sport", "Coach", "Athletes")
    VALUES (tenantid, name, sport, coach, athletes)
    RETURNING "dbo"."SportsTeams"."Id" INTO new_id;

    RETURN QUERY
    SELECT t."Id", t."TenantId", t."Name", t."Sport", t."Coach", t."Athletes"
    FROM "dbo"."SportsTeams" t
    WHERE t."Id" = new_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.sportsevent_create(
    tenantid uuid, name varchar(120), eventdate timestamptz, venue varchar(120) DEFAULT NULL
)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "Name" varchar(120), "EventDate" date, "Venue" varchar(120))
LANGUAGE plpgsql
AS $$
DECLARE
    new_id uuid;
BEGIN
    INSERT INTO "dbo"."SportsEvents" ("TenantId", "Name", "EventDate", "Venue")
    VALUES (tenantid, name, eventdate::date, venue)
    RETURNING "dbo"."SportsEvents"."Id" INTO new_id;

    RETURN QUERY
    SELECT e."Id", e."TenantId", e."Name", e."EventDate", e."Venue"
    FROM "dbo"."SportsEvents" e
    WHERE e."Id" = new_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.sportsmedal_create(
    tenantid uuid, kind varchar(10), title varchar(120) DEFAULT NULL, year int DEFAULT NULL
)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "Kind" varchar(10), "Title" varchar(120), "Year" int)
LANGUAGE plpgsql
AS $$
DECLARE
    new_id uuid;
BEGIN
    INSERT INTO "dbo"."SportsMedals" ("TenantId", "Kind", "Title", "Year")
    VALUES (tenantid, kind, title, year)
    RETURNING "dbo"."SportsMedals"."Id" INTO new_id;

    RETURN QUERY
    SELECT m."Id", m."TenantId", m."Kind", m."Title", m."Year"
    FROM "dbo"."SportsMedals" m
    WHERE m."Id" = new_id;
END;
$$;
