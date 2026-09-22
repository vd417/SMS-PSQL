-- Hostel module procs (unquoted identifiers so Postgres case-folding matches existing
-- call sites with zero C# changes, per convention established in 09_auth_procs.sql).

CREATE OR REPLACE FUNCTION dbo.hostelblock_create(
    tenantid uuid, name varchar(80), warden varchar(120) DEFAULT NULL
)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "Name" varchar(80), "Warden" varchar(120))
LANGUAGE plpgsql
AS $$
DECLARE
    new_id uuid;
BEGIN
    INSERT INTO "dbo"."HostelBlocks" ("TenantId", "Name", "Warden")
    VALUES (tenantid, name, warden)
    RETURNING "dbo"."HostelBlocks"."Id" INTO new_id;

    RETURN QUERY
    SELECT b."Id", b."TenantId", b."Name", b."Warden"
    FROM "dbo"."HostelBlocks" b
    WHERE b."Id" = new_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.hostelroom_create(
    tenantid uuid, blockid uuid, roomno varchar(40), capacity int DEFAULT 1
)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "BlockId" uuid, "BlockName" varchar(80), "RoomNo" varchar(40), "Capacity" int, "Residents" int)
LANGUAGE plpgsql
AS $$
DECLARE
    new_id uuid;
BEGIN
    INSERT INTO "dbo"."HostelRooms" ("TenantId", "BlockId", "RoomNo", "Capacity")
    VALUES (tenantid, blockid, roomno, capacity)
    RETURNING "dbo"."HostelRooms"."Id" INTO new_id;

    RETURN QUERY
    SELECT r."Id", r."TenantId", r."BlockId",
           (SELECT b."Name" FROM "dbo"."HostelBlocks" b WHERE b."Id" = r."BlockId"),
           r."RoomNo", r."Capacity",
           CAST((SELECT COUNT(*) FROM "dbo"."HostelResidents" res WHERE res."RoomId" = r."Id") AS int)
    FROM "dbo"."HostelRooms" r
    WHERE r."Id" = new_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.hostelresident_create(
    tenantid uuid, roomid uuid, studentname varchar(120), studentid uuid DEFAULT NULL
)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "RoomId" uuid, "RoomNo" varchar(40), "StudentName" varchar(120), "StudentId" uuid)
LANGUAGE plpgsql
AS $$
DECLARE
    new_id uuid;
BEGIN
    INSERT INTO "dbo"."HostelResidents" ("TenantId", "RoomId", "StudentName", "StudentId")
    VALUES (tenantid, roomid, studentname, studentid)
    RETURNING "dbo"."HostelResidents"."Id" INTO new_id;

    RETURN QUERY
    SELECT res."Id", res."TenantId", res."RoomId",
           (SELECT r."RoomNo" FROM "dbo"."HostelRooms" r WHERE r."Id" = res."RoomId"),
           res."StudentName", res."StudentId"
    FROM "dbo"."HostelResidents" res
    WHERE res."Id" = new_id;
END;
$$;
