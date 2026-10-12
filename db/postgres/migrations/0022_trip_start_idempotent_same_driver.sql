-- 0022: trip_start is now idempotent for the SAME driver. trip_start receives the caller's
-- user id as DriverId (TripService.StartAsync passes uid), so when the same person re-taps
-- Start on a bus that already has their own live/arrived trip -- e.g. the CRM/driver UI lost
-- local trip state, or the user tapped twice -- the active-bus guard returned no row and the
-- API surfaced 409 "This bus already has an active trip" on the user's OWN running trip. Now
-- trip_start returns that existing trip instead (the API treats it as a normal 201 and the
-- client resumes tracking it), so a re-tap resumes rather than erroring. A DIFFERENT driver
-- is still blocked (no row -> 409): we never hand one user another user's trip, and never
-- create a second live trip on one bus. Builds on 0021's never-pinged supersede, which still
-- runs first (an abandoned zero-ping trip is cleared before this resume check).
-- Additive, idempotent CREATE OR REPLACE of one existing function; grants are preserved.
-- Rollback: CREATE OR REPLACE the dbo.trip_start body from migration 0021 (no resume block).

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
    v_existing_id uuid;
BEGIN
    -- Bind the trip to a concrete BusId resolved WITHIN THIS TENANT (belt-and-suspenders on top
    -- of RLS) -- see the source .sql's own comment on why BusNo alone is never trusted.
    SELECT "Id" INTO v_bus_id FROM "dbo"."Buses"
    WHERE "TenantId" = trip_start.TenantId AND "BusNo" = trip_start.BusNo ORDER BY "Id" LIMIT 1;

    -- Supersede an ABANDONED never-pinged trip before anything else (0021). Both *LastPingAt
    -- NULL means the trip was started but never tracked; older than a 2-minute grace means it
    -- is not a brand-new trip still acquiring GPS. End it so it never wedges the bus.
    IF v_bus_id IS NOT NULL THEN
        UPDATE "dbo"."Trips"
           SET "Status" = 'ended', "EndedAt" = now()
         WHERE "BusId" = v_bus_id
           AND "Status" IN ('live', 'arrived')
           AND "DriverLastPingAt" IS NULL
           AND "ConductorLastPingAt" IS NULL
           AND "StartedAt" < now() - interval '2 minutes';
    END IF;

    -- Idempotent resume: if THIS caller already owns a live/arrived trip on the bus, return it
    -- instead of blocking. A re-tap of Start then resumes the user's own running trip rather
    -- than hitting 409. Scoped to DriverId = the caller, so a different driver is never handed
    -- this trip and still falls through to the guard below (-> 409).
    IF v_bus_id IS NOT NULL THEN
        SELECT "Id" INTO v_existing_id FROM "dbo"."Trips"
        WHERE "BusId" = v_bus_id AND "Status" IN ('live', 'arrived') AND "DriverId" = trip_start.DriverId
        ORDER BY "StartedAt" DESC LIMIT 1;
        IF v_existing_id IS NOT NULL THEN
            RETURN QUERY
            SELECT "Id", "TenantId", "RouteId", "BusNo", "DriverId", "ConductorId", "Direction", "Status", "StartedAt", "EndedAt",
                   "DriverLastPingAt", "ConductorLastPingAt"
            FROM "dbo"."Trips" WHERE "Id" = v_existing_id;
            RETURN;
        END IF;
    END IF;

    -- Reject a second trip on a bus that already has one live/arrived under a DIFFERENT driver
    -- -- returns no row rather than inserting; QuerySingleProcAsync yields null and TripService
    -- translates that into 409.
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
