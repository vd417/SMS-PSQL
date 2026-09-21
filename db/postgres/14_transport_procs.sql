-- Transport module: PL/pgSQL conversions of 16 of the 16 remaining stored procedures in the
-- Boarding/BusAssignment/BusDriverAssignments/BusTravelingTeacher/Bus/FuelLog/TransportRoute/
-- TripStopProgress/Trip/VehicleInspection family.
-- Source: full OBJECT_DEFINITION() extracted read-only from the live SQL Server Sms database on
-- 2026-09-21, cross-checked against sqlserver-object-inventory.csv. Bus_Update's live extraction
-- was truncated at 4000 chars by sqlcmd's PRINT-based capture (a `SELECT` capture with a wide -y
-- was needed to get the full body) -- worth reusing that approach if a body looks cut off again.
--
-- See 09_auth_procs.sql's header for the naming convention, 10_tenancy_procs.sql's for the
-- `#variable_conflict use_column` note, and 13_staffing_procs.sql's for the RETURNS int (+
-- GET DIAGNOSTICS ROW_COUNT) vs RETURNS TABLE distinction driven by which BaseRepository method
-- each call site uses (ExecuteProcAsync always does ExecuteScalarAsync<int>).

-- ============================================================
-- Boarding
-- UQ_Boardings_Trip_Student (TenantId, TripId, StudentId) backs the MERGE -> ON CONFLICT.
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.boarding_upsert(
    TenantId uuid, TripId uuid, StudentId uuid, StopId uuid, State varchar(10), At timestamptz
)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    INSERT INTO "dbo"."Boardings" ("Id", "TenantId", "TripId", "StudentId", "StopId", "State", "At")
    VALUES (gen_random_uuid(), TenantId, TripId, StudentId, StopId, COALESCE(State, 'pending'), At)
    ON CONFLICT ("TenantId", "TripId", "StudentId") DO UPDATE
    SET "State" = boarding_upsert.State, "StopId" = boarding_upsert.StopId, "At" = boarding_upsert.At;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

-- ============================================================
-- BusAssignment (duty teacher, one active bus per teacher)
-- IX_BusAssignments_Teacher (TenantId, TeacherUserId) backs the MERGE -> ON CONFLICT.
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.busassignment_assign(TenantId uuid, BusId uuid, TeacherUserId uuid)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    DELETE FROM "dbo"."BusAssignments"
    WHERE "TenantId" = TenantId AND "BusId" = BusId AND "TeacherUserId" <> TeacherUserId;

    INSERT INTO "dbo"."BusAssignments" ("TenantId", "TeacherUserId", "BusId")
    VALUES (TenantId, TeacherUserId, BusId)
    ON CONFLICT ("TenantId", "TeacherUserId") DO UPDATE SET "BusId" = busassignment_assign.BusId;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.busassignment_unassign(TenantId uuid, BusId uuid)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    DELETE FROM "dbo"."BusAssignments" WHERE "TenantId" = TenantId AND "BusId" = BusId;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

-- ============================================================
-- BusDriverAssignments (history)
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.busdriverassignments_listforbus(TenantId uuid, BusId uuid)
RETURNS TABLE ("Id" uuid, "StaffId" uuid, "StaffName" varchar(200), "Role" varchar(10),
               "AssignedAt" timestamptz, "UnassignedAt" timestamptz)
LANGUAGE sql
AS $$
    SELECT a."Id", a."StaffId", s."Name", a."Role", a."AssignedAt", a."UnassignedAt"
    FROM "dbo"."BusDriverAssignments" a
    JOIN "dbo"."Staff" s ON s."Id" = a."StaffId"
    WHERE a."TenantId" = TenantId AND a."BusId" = BusId
    ORDER BY a."AssignedAt" DESC;
$$;

-- ============================================================
-- BusTravelingTeacher (many-to-many live-view grant, distinct from BusAssignments)
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.bustravelingteacher_add(TenantId uuid, BusId uuid, TeacherUserId uuid)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    INSERT INTO "dbo"."BusTravelingTeachers" ("TenantId", "BusId", "TeacherUserId")
    SELECT TenantId, BusId, TeacherUserId
    WHERE NOT EXISTS (
        SELECT 1 FROM "dbo"."BusTravelingTeachers"
        WHERE "TenantId" = TenantId AND "BusId" = BusId AND "TeacherUserId" = TeacherUserId);
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.bustravelingteacher_remove(TenantId uuid, BusId uuid, TeacherUserId uuid)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    DELETE FROM "dbo"."BusTravelingTeachers"
    WHERE "TenantId" = TenantId AND "BusId" = BusId AND "TeacherUserId" = TeacherUserId;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

-- ============================================================
-- Bus
-- TRY_CAST-free here (no employee-code generation like Staff/Teacher), but shares the same
-- "steal the driver/conductor from whichever other bus currently holds them" logic in both
-- bus_create and bus_update -- kept faithful to the source rather than factored out, since the
-- source itself duplicates it (a future dedup is out of scope for this port).
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.bus_create(
    TenantId uuid, BusNo varchar(40),
    RouteName varchar(80) DEFAULT NULL, RouteId uuid DEFAULT NULL,
    Driver varchar(120) DEFAULT NULL, DriverPhone varchar(32) DEFAULT NULL,
    DriverStaffId uuid DEFAULT NULL, ConductorStaffId uuid DEFAULT NULL,
    Capacity int DEFAULT NULL, AssignedByUserId uuid DEFAULT NULL
)
RETURNS TABLE (
    "BusId" uuid, "BusNo" varchar(40), "RouteId" uuid, "RouteName" varchar(80), "Driver" varchar(120),
    "DriverPhone" varchar(32), "StopCount" int, "StudentsRiding" int, "Status" varchar(20),
    "ConductorStaffId" uuid, "Capacity" int
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_id uuid := gen_random_uuid();
    v_route_id uuid := bus_create.RouteId;
    v_driver varchar(120) := bus_create.Driver;
    v_driver_phone varchar(32) := bus_create.DriverPhone;
BEGIN
    IF v_route_id IS NULL AND bus_create.RouteName IS NOT NULL AND trim(bus_create.RouteName) <> '' THEN
        SELECT "Id" INTO v_route_id FROM "dbo"."TransportRoutes"
        WHERE "TenantId" = bus_create.TenantId AND "Name" = bus_create.RouteName
        ORDER BY "CreatedAt" LIMIT 1;
    END IF;

    IF bus_create.DriverStaffId IS NOT NULL THEN
        UPDATE "dbo"."BusDriverAssignments" SET "UnassignedAt" = now()
        WHERE "TenantId" = bus_create.TenantId AND "Role" = 'driver' AND "UnassignedAt" IS NULL
          AND "BusId" IN (SELECT "Id" FROM "dbo"."Buses" WHERE "TenantId" = bus_create.TenantId AND "DriverStaffId" = bus_create.DriverStaffId);
        UPDATE "dbo"."Buses" SET "DriverStaffId" = NULL
        WHERE "TenantId" = bus_create.TenantId AND "DriverStaffId" = bus_create.DriverStaffId;

        SELECT s."Name", s."Phone" INTO v_driver, v_driver_phone
        FROM "dbo"."Staff" s WHERE s."Id" = bus_create.DriverStaffId AND s."TenantId" = bus_create.TenantId;
    END IF;

    IF bus_create.ConductorStaffId IS NOT NULL THEN
        UPDATE "dbo"."BusDriverAssignments" SET "UnassignedAt" = now()
        WHERE "TenantId" = bus_create.TenantId AND "Role" = 'conductor' AND "UnassignedAt" IS NULL
          AND "BusId" IN (SELECT "Id" FROM "dbo"."Buses" WHERE "TenantId" = bus_create.TenantId AND "ConductorStaffId" = bus_create.ConductorStaffId);
        UPDATE "dbo"."Buses" SET "ConductorStaffId" = NULL
        WHERE "TenantId" = bus_create.TenantId AND "ConductorStaffId" = bus_create.ConductorStaffId;
    END IF;

    INSERT INTO "dbo"."Buses"
        ("Id", "TenantId", "BusNo", "RouteName", "RouteId", "Driver", "DriverPhone", "DriverStaffId", "ConductorStaffId", "Capacity")
    VALUES (v_id, bus_create.TenantId, bus_create.BusNo, bus_create.RouteName, v_route_id, v_driver, v_driver_phone,
            bus_create.DriverStaffId, bus_create.ConductorStaffId, bus_create.Capacity);

    IF bus_create.DriverStaffId IS NOT NULL THEN
        INSERT INTO "dbo"."BusDriverAssignments" ("Id", "TenantId", "BusId", "StaffId", "Role", "AssignedAt", "AssignedByUserId")
        VALUES (gen_random_uuid(), bus_create.TenantId, v_id, bus_create.DriverStaffId, 'driver', now(), bus_create.AssignedByUserId);
    END IF;
    IF bus_create.ConductorStaffId IS NOT NULL THEN
        INSERT INTO "dbo"."BusDriverAssignments" ("Id", "TenantId", "BusId", "StaffId", "Role", "AssignedAt", "AssignedByUserId")
        VALUES (gen_random_uuid(), bus_create.TenantId, v_id, bus_create.ConductorStaffId, 'conductor', now(), bus_create.AssignedByUserId);
    END IF;

    RETURN QUERY
    SELECT b."Id", b."BusNo", b."RouteId", b."RouteName", b."Driver", b."DriverPhone",
        CAST(COALESCE(
            (SELECT count(*) FROM "dbo"."RouteStops" s WHERE s."RouteId" = b."RouteId"),
            (SELECT count(*) FROM "dbo"."BusStops" bs WHERE bs."BusId" = b."Id")
        ) AS int),
        0, 'idle'::varchar(20), b."ConductorStaffId", b."Capacity"
    FROM "dbo"."Buses" b WHERE b."Id" = v_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.bus_update(
    TenantId uuid, BusId uuid, BusNo varchar(40) DEFAULT NULL, RouteId uuid DEFAULT NULL,
    DriverStaffId uuid DEFAULT NULL, ClearDriver boolean DEFAULT false,
    ConductorStaffId uuid DEFAULT NULL, ClearConductor boolean DEFAULT false,
    Capacity int DEFAULT NULL, ClearCapacity boolean DEFAULT false,
    AssignedByUserId uuid DEFAULT NULL
)
RETURNS TABLE (
    "BusId" uuid, "BusNo" varchar(40), "RouteId" uuid, "RouteName" varchar(80), "DriverStaffId" uuid,
    "Driver" varchar(120), "DriverPhone" varchar(32), "StopCount" int, "StudentsAssigned" int,
    "ConductorStaffId" uuid, "Capacity" int
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_old_driver_staff_id uuid;
    v_old_conductor_staff_id uuid;
    v_driver_staff_id uuid := bus_update.DriverStaffId;
    v_conductor_staff_id uuid := bus_update.ConductorStaffId;
    v_stolen_from_bus_id uuid;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM "dbo"."Buses" WHERE "Id" = bus_update.BusId AND "TenantId" = bus_update.TenantId) THEN
        RETURN;
    END IF;

    SELECT "DriverStaffId", "ConductorStaffId" INTO v_old_driver_staff_id, v_old_conductor_staff_id
    FROM "dbo"."Buses" WHERE "Id" = bus_update.BusId;

    IF bus_update.ClearDriver THEN v_driver_staff_id := NULL; END IF;
    IF bus_update.ClearConductor THEN v_conductor_staff_id := NULL; END IF;

    IF v_driver_staff_id IS NOT NULL THEN
        SELECT "Id" INTO v_stolen_from_bus_id FROM "dbo"."Buses"
        WHERE "TenantId" = bus_update.TenantId AND "DriverStaffId" = v_driver_staff_id AND "Id" <> bus_update.BusId
        LIMIT 1;

        IF v_stolen_from_bus_id IS NOT NULL THEN
            UPDATE "dbo"."BusDriverAssignments" SET "UnassignedAt" = now()
            WHERE "TenantId" = bus_update.TenantId AND "BusId" = v_stolen_from_bus_id AND "StaffId" = v_driver_staff_id
                AND "Role" = 'driver' AND "UnassignedAt" IS NULL;
            UPDATE "dbo"."Buses" SET "DriverStaffId" = NULL WHERE "Id" = v_stolen_from_bus_id;
        END IF;

        UPDATE "dbo"."Buses" b SET
            "DriverStaffId" = v_driver_staff_id,
            "Driver" = s."Name",
            "DriverPhone" = s."Phone"
        FROM "dbo"."Staff" s
        WHERE s."Id" = v_driver_staff_id AND s."TenantId" = bus_update.TenantId AND b."Id" = bus_update.BusId;

        IF v_old_driver_staff_id IS NULL OR v_old_driver_staff_id <> v_driver_staff_id THEN
            IF v_old_driver_staff_id IS NOT NULL THEN
                UPDATE "dbo"."BusDriverAssignments" SET "UnassignedAt" = now()
                WHERE "TenantId" = bus_update.TenantId AND "BusId" = bus_update.BusId AND "StaffId" = v_old_driver_staff_id
                    AND "Role" = 'driver' AND "UnassignedAt" IS NULL;
            END IF;
            INSERT INTO "dbo"."BusDriverAssignments" ("Id", "TenantId", "BusId", "StaffId", "Role", "AssignedAt", "AssignedByUserId")
            VALUES (gen_random_uuid(), bus_update.TenantId, bus_update.BusId, v_driver_staff_id, 'driver', now(), bus_update.AssignedByUserId);
        END IF;
    ELSIF bus_update.ClearDriver THEN
        UPDATE "dbo"."Buses" SET "DriverStaffId" = NULL, "Driver" = NULL, "DriverPhone" = NULL WHERE "Id" = bus_update.BusId;
        IF v_old_driver_staff_id IS NOT NULL THEN
            UPDATE "dbo"."BusDriverAssignments" SET "UnassignedAt" = now()
            WHERE "TenantId" = bus_update.TenantId AND "BusId" = bus_update.BusId AND "StaffId" = v_old_driver_staff_id
                AND "Role" = 'driver' AND "UnassignedAt" IS NULL;
        END IF;
    END IF;

    IF v_conductor_staff_id IS NOT NULL THEN
        v_stolen_from_bus_id := NULL;
        SELECT "Id" INTO v_stolen_from_bus_id FROM "dbo"."Buses"
        WHERE "TenantId" = bus_update.TenantId AND "ConductorStaffId" = v_conductor_staff_id AND "Id" <> bus_update.BusId
        LIMIT 1;

        IF v_stolen_from_bus_id IS NOT NULL THEN
            UPDATE "dbo"."BusDriverAssignments" SET "UnassignedAt" = now()
            WHERE "TenantId" = bus_update.TenantId AND "BusId" = v_stolen_from_bus_id AND "StaffId" = v_conductor_staff_id
                AND "Role" = 'conductor' AND "UnassignedAt" IS NULL;
            UPDATE "dbo"."Buses" SET "ConductorStaffId" = NULL WHERE "Id" = v_stolen_from_bus_id;
        END IF;

        UPDATE "dbo"."Buses" SET "ConductorStaffId" = v_conductor_staff_id WHERE "Id" = bus_update.BusId AND "TenantId" = bus_update.TenantId;

        IF v_old_conductor_staff_id IS NULL OR v_old_conductor_staff_id <> v_conductor_staff_id THEN
            IF v_old_conductor_staff_id IS NOT NULL THEN
                UPDATE "dbo"."BusDriverAssignments" SET "UnassignedAt" = now()
                WHERE "TenantId" = bus_update.TenantId AND "BusId" = bus_update.BusId AND "StaffId" = v_old_conductor_staff_id
                    AND "Role" = 'conductor' AND "UnassignedAt" IS NULL;
            END IF;
            INSERT INTO "dbo"."BusDriverAssignments" ("Id", "TenantId", "BusId", "StaffId", "Role", "AssignedAt", "AssignedByUserId")
            VALUES (gen_random_uuid(), bus_update.TenantId, bus_update.BusId, v_conductor_staff_id, 'conductor', now(), bus_update.AssignedByUserId);
        END IF;
    ELSIF bus_update.ClearConductor THEN
        UPDATE "dbo"."Buses" SET "ConductorStaffId" = NULL WHERE "Id" = bus_update.BusId;
        IF v_old_conductor_staff_id IS NOT NULL THEN
            UPDATE "dbo"."BusDriverAssignments" SET "UnassignedAt" = now()
            WHERE "TenantId" = bus_update.TenantId AND "BusId" = bus_update.BusId AND "StaffId" = v_old_conductor_staff_id
                AND "Role" = 'conductor' AND "UnassignedAt" IS NULL;
        END IF;
    END IF;

    UPDATE "dbo"."Buses" b SET
        "BusNo" = COALESCE(bus_update.BusNo, b."BusNo"),
        "RouteId" = CASE WHEN bus_update.RouteId IS NOT NULL THEN bus_update.RouteId ELSE b."RouteId" END,
        "RouteName" = CASE WHEN bus_update.RouteId IS NOT NULL
            THEN (SELECT r."Name" FROM "dbo"."TransportRoutes" r WHERE r."Id" = bus_update.RouteId AND r."TenantId" = bus_update.TenantId)
            ELSE b."RouteName" END,
        "Capacity" = CASE WHEN bus_update.ClearCapacity THEN NULL WHEN bus_update.Capacity IS NOT NULL THEN bus_update.Capacity ELSE b."Capacity" END
    WHERE b."Id" = bus_update.BusId AND b."TenantId" = bus_update.TenantId;

    -- Column order here must match UpdatedBusRow's constructor parameter order exactly (see
    -- 13_staffing_procs.sql's staff_create for the same Dapper fast-path constraint).
    RETURN QUERY
    SELECT b."Id", b."BusNo", b."RouteId", b."RouteName", b."DriverStaffId", b."Driver", b."DriverPhone",
        CASE WHEN b."RouteId" IS NOT NULL
            THEN (SELECT CAST(count(*) AS int) FROM "dbo"."RouteStops" rs WHERE rs."RouteId" = b."RouteId")
            ELSE (SELECT CAST(count(*) AS int) FROM "dbo"."BusStops" bs WHERE bs."BusId" = b."Id") END,
        (SELECT CAST(count(*) AS int) FROM "dbo"."StudentBusAssignments" sba WHERE sba."BusId" = b."Id"),
        b."ConductorStaffId", b."Capacity"
    FROM "dbo"."Buses" b WHERE b."Id" = bus_update.BusId;
END;
$$;

-- ============================================================
-- FuelLog
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.fuellog_create(
    TenantId uuid, BusId uuid, RecordedByUserId uuid, OdometerKm int, FuelAddedLiters numeric(10,2)
)
RETURNS TABLE ("Id" uuid, "TenantId" uuid, "BusId" uuid, "RecordedByUserId" uuid,
               "OdometerKm" int, "FuelAddedLiters" numeric(10,2), "RecordedAt" timestamptz)
LANGUAGE sql
AS $$
    INSERT INTO "dbo"."FuelLogs" ("Id", "TenantId", "BusId", "RecordedByUserId", "OdometerKm", "FuelAddedLiters")
    VALUES (gen_random_uuid(), TenantId, BusId, RecordedByUserId, OdometerKm, FuelAddedLiters)
    RETURNING "Id", "TenantId", "BusId", "RecordedByUserId", "OdometerKm", "FuelAddedLiters", "RecordedAt";
$$;

-- ============================================================
-- TransportRoute
-- WHILE loop generating N placeholder stops -> generate_series().
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.transportroute_create(TenantId uuid, Name varchar(80), Stops int)
RETURNS TABLE ("Id" uuid, "Name" varchar(80), "Stops" int)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_route_id uuid := gen_random_uuid();
    v_n int := LEAST(GREATEST(transportroute_create.Stops, 1), 50);
BEGIN
    INSERT INTO "dbo"."TransportRoutes" ("Id", "TenantId", "Name")
    VALUES (v_route_id, transportroute_create.TenantId, transportroute_create.Name);

    INSERT INTO "dbo"."RouteStops" ("Id", "TenantId", "RouteId", "Name", "Seq")
    SELECT gen_random_uuid(), transportroute_create.TenantId, v_route_id, 'Stop ' || i, i
    FROM generate_series(1, v_n) AS i;

    RETURN QUERY
    SELECT r."Id", r."Name", CAST((SELECT count(*) FROM "dbo"."RouteStops" s WHERE s."RouteId" = r."Id") AS int)
    FROM "dbo"."TransportRoutes" r WHERE r."Id" = v_route_id;
END;
$$;

-- ============================================================
-- TripStopProgress
-- IX_TripStopProgress_Trip_Stop (TripId, StopId) -- NOT TenantId-scoped -- backs the MERGE ->
-- ON CONFLICT target; the TenantId filter in the original MERGE's ON clause is preserved as an
-- ordinary WHERE-style guard inside the INSERT's own scope (it doesn't change the conflict key).
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.tripstopprogress_complete(TenantId uuid, TripId uuid, StopId uuid, DepartedAt timestamptz)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    UPDATE "dbo"."TripStopProgress" SET "DepartedAt" = DepartedAt
    WHERE "TenantId" = TenantId AND "TripId" = TripId AND "StopId" = StopId;
    GET DIAGNOSTICS v_count = ROW_COUNT;

    UPDATE "dbo"."Trips" SET "CurrentStopId" = NULL WHERE "Id" = TripId AND "TenantId" = TenantId;
    RETURN v_count;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.tripstopprogress_confirmarrival(
    TenantId uuid, TripId uuid, StopId uuid, Seq int, ArrivedAt timestamptz, ConfirmedAt timestamptz
)
RETURNS int
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_count int;
BEGIN
    INSERT INTO "dbo"."TripStopProgress" AS tsp ("Id", "TenantId", "TripId", "StopId", "Seq", "ArrivedAt", "ConfirmedAt")
    VALUES (gen_random_uuid(), TenantId, TripId, StopId, Seq, ArrivedAt, ConfirmedAt)
    ON CONFLICT ("TripId", "StopId") DO UPDATE
    SET "ArrivedAt" = COALESCE(tsp."ArrivedAt", tripstopprogress_confirmarrival.ArrivedAt),
        "ConfirmedAt" = tripstopprogress_confirmarrival.ConfirmedAt;
    GET DIAGNOSTICS v_count = ROW_COUNT;

    UPDATE "dbo"."Trips" SET "CurrentStopId" = StopId WHERE "Id" = TripId AND "TenantId" = TenantId;
    RETURN v_count;
END;
$$;

-- ============================================================
-- Trip
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.trip_end(Id uuid, TenantId uuid)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "RouteId" uuid, "BusNo" varchar(40), "DriverId" uuid, "ConductorId" uuid,
    "Direction" varchar(10), "Status" varchar(10), "StartedAt" timestamptz, "EndedAt" timestamptz,
    "DriverLastPingAt" timestamptz, "ConductorLastPingAt" timestamptz
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE "dbo"."Trips" SET "Status" = 'ended', "EndedAt" = now()
    WHERE "Id" = trip_end.Id AND "TenantId" = trip_end.TenantId AND "Status" IN ('live', 'arrived');

    RETURN QUERY
    SELECT "Id", "TenantId", "RouteId", "BusNo", "DriverId", "ConductorId", "Direction", "Status", "StartedAt", "EndedAt",
           "DriverLastPingAt", "ConductorLastPingAt"
    FROM "dbo"."Trips" WHERE "Id" = trip_end.Id AND "TenantId" = trip_end.TenantId;
END;
$$;

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

-- ============================================================
-- VehicleInspection
-- UX_VehicleInspections_Tenant_Bus_Date (TenantId, BusId, InspectionDate) backs the MERGE ->
-- ON CONFLICT. Remarks (the only source parameter with a default) is declared last, after
-- InspectionDate (no default in the source) -- Postgres requires defaulted parameters to be
-- trailing regardless of the source proc's own declaration order; every call site here uses
-- named notation so this reordering is invisible to callers.
-- ============================================================

-- InspectionDate is timestamp (not date): the C# call site sends SchoolClock.ToSchoolLocal(...).Date
-- (DateTime.Kind=Unspecified with the time zeroed), which Npgsql binds as timestamp, not date --
-- same function-overload-resolution reasoning as 13_staffing_procs.sql's leave_create.
CREATE OR REPLACE FUNCTION dbo.vehicleinspection_upsert(
    TenantId uuid, BusId uuid, SubmittedByUserId uuid,
    Brakes boolean, Tyres boolean, Lights boolean, Horn boolean,
    FirstAidKit boolean, FireExtinguisher boolean, EmergencyExit boolean, FuelLevel boolean,
    AllOk boolean, InspectionDate timestamp, Remarks varchar(2000) DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "BusId" uuid, "SubmittedByUserId" uuid,
    "Brakes" boolean, "Tyres" boolean, "Lights" boolean, "Horn" boolean,
    "FirstAidKit" boolean, "FireExtinguisher" boolean, "EmergencyExit" boolean, "FuelLevel" boolean,
    "AllOk" boolean, "Remarks" varchar(2000), "InspectionDate" date, "CreatedAt" timestamptz
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    INSERT INTO "dbo"."VehicleInspections"
        ("Id", "TenantId", "BusId", "SubmittedByUserId", "Brakes", "Tyres", "Lights", "Horn",
         "FirstAidKit", "FireExtinguisher", "EmergencyExit", "FuelLevel", "AllOk", "Remarks", "InspectionDate")
    VALUES (gen_random_uuid(), TenantId, BusId, SubmittedByUserId, Brakes, Tyres, Lights, Horn,
            FirstAidKit, FireExtinguisher, EmergencyExit, FuelLevel, AllOk, Remarks, InspectionDate::date)
    ON CONFLICT ("TenantId", "BusId", "InspectionDate") DO UPDATE SET
        "SubmittedByUserId" = vehicleinspection_upsert.SubmittedByUserId,
        "Brakes" = vehicleinspection_upsert.Brakes, "Tyres" = vehicleinspection_upsert.Tyres,
        "Lights" = vehicleinspection_upsert.Lights, "Horn" = vehicleinspection_upsert.Horn,
        "FirstAidKit" = vehicleinspection_upsert.FirstAidKit, "FireExtinguisher" = vehicleinspection_upsert.FireExtinguisher,
        "EmergencyExit" = vehicleinspection_upsert.EmergencyExit, "FuelLevel" = vehicleinspection_upsert.FuelLevel,
        "AllOk" = vehicleinspection_upsert.AllOk, "Remarks" = vehicleinspection_upsert.Remarks;

    RETURN QUERY
    SELECT "Id", "TenantId", "BusId", "SubmittedByUserId", "Brakes", "Tyres", "Lights", "Horn",
           "FirstAidKit", "FireExtinguisher", "EmergencyExit", "FuelLevel", "AllOk", "Remarks", "InspectionDate", "CreatedAt"
    FROM "dbo"."VehicleInspections"
    WHERE "TenantId" = TenantId AND "BusId" = BusId AND "InspectionDate" = InspectionDate::date;
END;
$$;

-- ============================================================
-- TripPing (TVP bulk insert)
-- dbo.TripPingTvp has no 1:1 Postgres CREATE TYPE mapping -- Rows arrives as a jsonb-serialized
-- array, decomposed with jsonb_to_recordset(). Declared `text` (not `jsonb`) for the same
-- function-overload-resolution reason as 09_auth_procs.sql's users_bulkcreate and
-- 12_finance_procs.sql's fee_summarybytenants -- cast to ::jsonb inside the body instead.
-- Named to match TripPing_BulkInsert's unquoted-lowercase fold (tripping_bulkinsert), NOT the
-- differently-punctuated trip_ping_bulk_insert from 08_sample_procedure_conversions.sql's worked
-- example -- that example was never wired to a real call site and its name wouldn't resolve from
-- BaseRepository.FunctionCallSql's "dbo.TripPing_BulkInsert" call text.
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.tripping_bulkinsert(TenantId uuid, TripId uuid, Rows text)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    INSERT INTO "dbo"."TripPings" ("Id", "TenantId", "TripId", "Lat", "Lng", "SpeedKmh", "Heading", "At", "Accuracy")
    SELECT gen_random_uuid(), TenantId, TripId, r."Lat", r."Lng", r."SpeedKmh", r."Heading", r."At", r."Accuracy"
    FROM jsonb_to_recordset(Rows::jsonb) AS r(
        "Lat" double precision, "Lng" double precision, "SpeedKmh" double precision,
        "Heading" double precision, "At" timestamptz, "Accuracy" double precision
    );
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

-- ============================================================
-- Notification_Create -- NOT a Transport proc (it's dbo.Notifications, owned by Comms, which
-- remains otherwise unconverted). Converted here as a narrow single-proc dependency because
-- VehicleChecks' "failed inspection notifies managers" feature calls it directly -- same
-- "convert only the one proc a different module's feature reaches into" precedent as
-- 13_staffing_procs.sql's cross-module Attendance/Transport one-liners.
-- Title has no default in the source but comes after two defaulted params (Icon, Tone) --
-- reordered so Postgres's "defaults must trail" rule holds; every call site uses named notation.
-- ============================================================

CREATE OR REPLACE FUNCTION dbo.notification_create(
    TenantId uuid, Title varchar(200), Icon varchar(40) DEFAULT NULL, Tone varchar(20) DEFAULT NULL,
    Body varchar(1000) DEFAULT NULL, UserId uuid DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Icon" varchar(40), "Tone" varchar(20), "Title" varchar(200),
    "Body" varchar(1000), "Time" varchar(40), "Unread" boolean
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_id uuid := gen_random_uuid();
BEGIN
    INSERT INTO "dbo"."Notifications" ("Id", "TenantId", "Icon", "Tone", "Title", "Body", "Time", "Unread", "UserId")
    VALUES (v_id, TenantId, Icon, Tone, Title, Body, to_char(now() AT TIME ZONE 'UTC', 'HH24:MI:SS'), true, UserId);

    RETURN QUERY
    SELECT "Id", "TenantId", "Icon", "Tone", "Title", "Body", "Time", "Unread"
    FROM "dbo"."Notifications" WHERE "Id" = v_id;
END;
$$;
