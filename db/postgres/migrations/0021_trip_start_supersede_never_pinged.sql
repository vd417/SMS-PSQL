-- 0021: trip_start auto-supersedes an abandoned never-pinged trip instead of blocking a
-- new Start with a 409. A trip whose driver AND conductor have both never pinged
-- ("DriverLastPingAt"/"ConductorLastPingAt" still NULL) was created by a Start tap but
-- never actually tracked -- the device never acquired GPS, or the app was closed right
-- away. Such a trip otherwise wedges the bus until the 30-minute TransportOfflineSweepWorker
-- reaps it, and drivers re-tapping Start just hit "This bus already has an active trip" in a
-- loop (prod incident: bus e59e77c1 on 2026-10-12, trips b5342c09/aa8751e5, both zero-ping).
-- trip_start now ends such a trip up front, once it is older than a 2-minute grace, so the
-- new Start succeeds. The grace keeps a genuinely fresh trip (started seconds ago, still
-- acquiring GPS, no ping yet) safe from being killed out from under its own driver, and a
-- trip that HAS pinged (a real, moving trip) is never superseded -- it still blocks.
-- Additive, idempotent CREATE OR REPLACE of one existing function; grants are preserved.
-- Rollback: CREATE OR REPLACE the dbo.trip_start body from db/postgres/14_transport_procs.sql
-- (the version without the supersede UPDATE block).

CREATE OR REPLACE FUNCTION dbo.trip_start(TenantId uuid, RouteId uuid, BusNo varchar(40), DriverId uuid, Direction varchar(10))
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "RouteId" uuid, "BusNo" varchar(40), "DriverId" uuid, "ConductorId" uuid,
    "Direction" varchar(10), "Status" varchar(10), "StartedAt" timestamptz, "EndedAt" timestamptz,
    "DriverLastPingAt" timestamptz, "ConductorLastPingAt" timestamptz
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_id uuid := gen_random_uuid();
    v_bus_id uuid;
    v_conductor_id uuid;
BEGIN
    -- Bind the trip to a concrete BusId resolved WITHIN THIS TENANT (belt-and-suspenders on top
    -- of RLS) -- see the source .sql's own comment on why BusNo alone is never trusted.
    SELECT "Id" INTO v_bus_id FROM "dbo"."Buses"
    WHERE "TenantId" = trip_start.TenantId AND "BusNo" = trip_start.BusNo ORDER BY "Id" LIMIT 1;

    -- Supersede an ABANDONED never-pinged trip before the guard below. Both *LastPingAt NULL
    -- means the trip was started but never tracked; older than a 2-minute grace means it is not
    -- a brand-new trip still acquiring GPS. End it so this Start isn't blocked for up to 30 min.
    -- A trip that has pinged is untouched (not NULL) and still blocks; a <2-min trip is spared.
    IF v_bus_id IS NOT NULL THEN
        UPDATE "dbo"."Trips"
           SET "Status" = 'ended', "EndedAt" = now()
         WHERE "BusId" = v_bus_id
           AND "Status" IN ('live', 'arrived')
           AND "DriverLastPingAt" IS NULL
           AND "ConductorLastPingAt" IS NULL
           AND "StartedAt" < now() - interval '2 minutes';
    END IF;

    -- Reject a second trip on a bus that already has one live/arrived -- returns no row rather
    -- than inserting; QuerySingleProcAsync yields null and TripService translates that into 409.
    IF v_bus_id IS NOT NULL AND EXISTS (
        SELECT 1 FROM "dbo"."Trips" WHERE "BusId" = v_bus_id AND "Status" IN ('live', 'arrived')
    ) THEN
        RETURN;
    END IF;

    -- Auto-assign the trip's conductor from the bus's ConductorStaffId, resolved to their login
    -- identity (Staff.UserId) -- ConductorId is a user id, same as DriverId.
    SELECT s."UserId" INTO v_conductor_id
    FROM "dbo"."Buses" b JOIN "dbo"."Staff" s ON s."Id" = b."ConductorStaffId"
    WHERE b."Id" = v_bus_id;

    INSERT INTO "dbo"."Trips"
        ("Id", "TenantId", "RouteId", "BusId", "BusNo", "DriverId", "ConductorId", "Direction", "Status", "StartedAt")
    VALUES (v_id, trip_start.TenantId, trip_start.RouteId, v_bus_id, trip_start.BusNo, trip_start.DriverId, v_conductor_id,
            COALESCE(trip_start.Direction, 'pickup'), 'live', now());

    RETURN QUERY
    SELECT "Id", "TenantId", "RouteId", "BusNo", "DriverId", "ConductorId", "Direction", "Status", "StartedAt", "EndedAt",
           "DriverLastPingAt", "ConductorLastPingAt"
    FROM "dbo"."Trips" WHERE "Id" = v_id;
END;
$$;
