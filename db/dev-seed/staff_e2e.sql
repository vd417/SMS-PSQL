-- Dev-only, idempotent seed for the staff-app end-to-end check. Run as the schema owner
-- ("sms"), which bypasses RLS. Fixed UUIDs + ON CONFLICT make re-runs no-ops/updates.
-- Never part of db/postgres/migrations.
BEGIN;

-- Tenant (must be 'active' or BillingStateMiddleware 403s every staff route).
INSERT INTO "dbo"."Tenants" ("Id", "Name", "Slug", "Status", "Tier", "Lat", "Lng", "GeofenceRadiusMeters")
VALUES ('a0000000-0000-4000-8000-000000000001', 'Greenfield E2E School', 'greenfield-e2e', 'active', 'platinum', 28.4595, 77.0266, 150)
ON CONFLICT ("Id") DO UPDATE SET "Status" = 'active', "Name" = EXCLUDED."Name", "Tier" = EXCLUDED."Tier",
  "Lat" = EXCLUDED."Lat", "Lng" = EXCLUDED."Lng", "GeofenceRadiusMeters" = EXCLUDED."GeofenceRadiusMeters";

INSERT INTO "dbo"."SchoolLocations" ("Id", "TenantId", "Lat", "Lng", "RadiusMeters", "Name")
VALUES ('a0000000-0000-4000-8000-000000000002', 'a0000000-0000-4000-8000-000000000001', 28.4595, 77.0266, 150, 'Greenfield E2E Main Gate')
ON CONFLICT ("Id") DO UPDATE SET "Lat" = EXCLUDED."Lat", "Lng" = EXCLUDED."Lng", "RadiusMeters" = EXCLUDED."RadiusMeters", "Name" = EXCLUDED."Name";

-- Logins: no password yet (MustSetPassword) -> first sign-in goes password_not_set -> OTP -> set password.
INSERT INTO "dbo"."Users" ("Id", "TenantId", "Email", "Status", "MustSetPassword", "Name") VALUES
  ('a0000000-0000-4000-8000-000000000101', 'a0000000-0000-4000-8000-000000000001', 'driver@e2e.test',    'active', true, 'Ramesh Driver'),
  ('a0000000-0000-4000-8000-000000000102', 'a0000000-0000-4000-8000-000000000001', 'conductor@e2e.test', 'active', true, 'Sita Conductor'),
  ('a0000000-0000-4000-8000-000000000103', 'a0000000-0000-4000-8000-000000000001', 'sweeper@e2e.test',   'active', true, 'Mohan Sweeper'),
  ('a0000000-0000-4000-8000-000000000104', 'a0000000-0000-4000-8000-000000000001', 'gardener@e2e.test',  'active', true, 'Lata Gardener'),
  ('a0000000-0000-4000-8000-000000000105', 'a0000000-0000-4000-8000-000000000001', 'guard@e2e.test',     'active', true, 'Vikram Guard'),
  ('a0000000-0000-4000-8000-000000000106', 'a0000000-0000-4000-8000-000000000001', 'peon@e2e.test',      'active', true, 'Anil Peon')
ON CONFLICT ("Id") DO UPDATE SET "Email" = EXCLUDED."Email", "Status" = 'active', "Name" = EXCLUDED."Name";

INSERT INTO "dbo"."UserRoles" ("UserId", "Role")
SELECT u, 'staff' FROM unnest(ARRAY[
  'a0000000-0000-4000-8000-000000000101', 'a0000000-0000-4000-8000-000000000102', 'a0000000-0000-4000-8000-000000000103',
  'a0000000-0000-4000-8000-000000000104', 'a0000000-0000-4000-8000-000000000105', 'a0000000-0000-4000-8000-000000000106']::uuid[]) AS u
ON CONFLICT ("UserId", "Role") DO NOTHING;

-- Staff rows. Role (designation) drives /auth/me role_key via StaffRoleMapper; Category drives the dashboard role_card.
INSERT INTO "dbo"."Staff" ("Id", "TenantId", "Name", "Role", "Category", "Department", "Shift", "Route", "Status", "UserId", "Email", "EmployeeCode") VALUES
  ('a0000000-0000-4000-8000-000000000201', 'a0000000-0000-4000-8000-000000000001', 'Ramesh Driver',  'Driver',         'driver',    'Transport',    '6:30 AM - 3:30 PM', 'E2E Route 1', 'active', 'a0000000-0000-4000-8000-000000000101', 'driver@e2e.test',    'E2E-001'),
  ('a0000000-0000-4000-8000-000000000202', 'a0000000-0000-4000-8000-000000000001', 'Sita Conductor', 'Conductor',      'conductor', 'Transport',    '6:30 AM - 3:30 PM', 'E2E Route 1', 'active', 'a0000000-0000-4000-8000-000000000102', 'conductor@e2e.test', 'E2E-002'),
  ('a0000000-0000-4000-8000-000000000203', 'a0000000-0000-4000-8000-000000000001', 'Mohan Sweeper',  'Sweeper',        'support',   'Housekeeping', '7:00 AM - 4:00 PM', NULL,          'active', 'a0000000-0000-4000-8000-000000000103', 'sweeper@e2e.test',   'E2E-003'),
  ('a0000000-0000-4000-8000-000000000204', 'a0000000-0000-4000-8000-000000000001', 'Lata Gardener',  'Gardener',       'support',   'Grounds',      '7:00 AM - 4:00 PM', NULL,          'active', 'a0000000-0000-4000-8000-000000000104', 'gardener@e2e.test',  'E2E-004'),
  ('a0000000-0000-4000-8000-000000000205', 'a0000000-0000-4000-8000-000000000001', 'Vikram Guard',   'Security Guard', 'support',   'Security',     '6:00 AM - 6:00 PM', NULL,          'active', 'a0000000-0000-4000-8000-000000000105', 'guard@e2e.test',     'E2E-005'),
  ('a0000000-0000-4000-8000-000000000206', 'a0000000-0000-4000-8000-000000000001', 'Anil Peon',      'Peon',           'support',   'Office',       '8:00 AM - 5:00 PM', NULL,          'active', 'a0000000-0000-4000-8000-000000000106', 'peon@e2e.test',      'E2E-006')
ON CONFLICT ("Id") DO UPDATE SET "Name" = EXCLUDED."Name", "Role" = EXCLUDED."Role", "Category" = EXCLUDED."Category",
  "Department" = EXCLUDED."Department", "Shift" = EXCLUDED."Shift", "Route" = EXCLUDED."Route", "Status" = 'active',
  "UserId" = EXCLUDED."UserId", "Email" = EXCLUDED."Email";

-- Route with 4 stops (~1 km apart, north of the school).
INSERT INTO "dbo"."TransportRoutes" ("Id", "TenantId", "Name")
VALUES ('a0000000-0000-4000-8000-000000000301', 'a0000000-0000-4000-8000-000000000001', 'E2E Route 1')
ON CONFLICT ("Id") DO UPDATE SET "Name" = EXCLUDED."Name";

INSERT INTO "dbo"."RouteStops" ("Id", "TenantId", "RouteId", "Name", "Seq", "Lat", "Lng") VALUES
  ('a0000000-0000-4000-8000-000000000311', 'a0000000-0000-4000-8000-000000000001', 'a0000000-0000-4000-8000-000000000301', 'Sector 14 Market',  1, 28.4680, 77.0300),
  ('a0000000-0000-4000-8000-000000000312', 'a0000000-0000-4000-8000-000000000001', 'a0000000-0000-4000-8000-000000000301', 'Sector 15 Park',    2, 28.4655, 77.0290),
  ('a0000000-0000-4000-8000-000000000313', 'a0000000-0000-4000-8000-000000000001', 'a0000000-0000-4000-8000-000000000301', 'Old Railway Road',  3, 28.4630, 77.0280),
  ('a0000000-0000-4000-8000-000000000314', 'a0000000-0000-4000-8000-000000000001', 'a0000000-0000-4000-8000-000000000301', 'Civil Lines Chowk', 4, 28.4610, 77.0272)
ON CONFLICT ("Id") DO UPDATE SET "Name" = EXCLUDED."Name", "Seq" = EXCLUDED."Seq", "Lat" = EXCLUDED."Lat", "Lng" = EXCLUDED."Lng";

INSERT INTO "dbo"."Buses" ("Id", "TenantId", "BusNo", "RouteName", "RouteId", "DriverStaffId", "ConductorStaffId", "Capacity")
VALUES ('a0000000-0000-4000-8000-000000000401', 'a0000000-0000-4000-8000-000000000001', 'E2E-BUS-01', 'E2E Route 1',
        'a0000000-0000-4000-8000-000000000301', 'a0000000-0000-4000-8000-000000000201', 'a0000000-0000-4000-8000-000000000202', 40)
ON CONFLICT ("Id") DO UPDATE SET "BusNo" = EXCLUDED."BusNo", "RouteId" = EXCLUDED."RouteId",
  "DriverStaffId" = EXCLUDED."DriverStaffId", "ConductorStaffId" = EXCLUDED."ConductorStaffId", "Capacity" = EXCLUDED."Capacity";

-- Students: 2 at stop 1, 1 at stop 2, none at stop 3 (vacuous stop), 1 at stop 4.
INSERT INTO "dbo"."Students" ("Id", "TenantId", "AdmissionNo", "Name", "Grade", "Section", "Status") VALUES
  ('a0000000-0000-4000-8000-000000000501', 'a0000000-0000-4000-8000-000000000001', 'E2E-S1', 'Aarav Sharma', '5', 'A', 'active'),
  ('a0000000-0000-4000-8000-000000000502', 'a0000000-0000-4000-8000-000000000001', 'E2E-S2', 'Diya Verma',   '3', 'B', 'active'),
  ('a0000000-0000-4000-8000-000000000503', 'a0000000-0000-4000-8000-000000000001', 'E2E-S3', 'Kabir Singh',  '7', 'A', 'active'),
  ('a0000000-0000-4000-8000-000000000504', 'a0000000-0000-4000-8000-000000000001', 'E2E-S4', 'Meera Iyer',   '2', 'C', 'active')
ON CONFLICT ("Id") DO UPDATE SET "Name" = EXCLUDED."Name", "Status" = 'active';

INSERT INTO "dbo"."StudentBusAssignments" ("Id", "TenantId", "StudentId", "BusId", "StopId", "RouteId") VALUES
  ('a0000000-0000-4000-8000-000000000601', 'a0000000-0000-4000-8000-000000000001', 'a0000000-0000-4000-8000-000000000501', 'a0000000-0000-4000-8000-000000000401', 'a0000000-0000-4000-8000-000000000311', 'a0000000-0000-4000-8000-000000000301'),
  ('a0000000-0000-4000-8000-000000000602', 'a0000000-0000-4000-8000-000000000001', 'a0000000-0000-4000-8000-000000000502', 'a0000000-0000-4000-8000-000000000401', 'a0000000-0000-4000-8000-000000000311', 'a0000000-0000-4000-8000-000000000301'),
  ('a0000000-0000-4000-8000-000000000603', 'a0000000-0000-4000-8000-000000000001', 'a0000000-0000-4000-8000-000000000503', 'a0000000-0000-4000-8000-000000000401', 'a0000000-0000-4000-8000-000000000312', 'a0000000-0000-4000-8000-000000000301'),
  ('a0000000-0000-4000-8000-000000000604', 'a0000000-0000-4000-8000-000000000001', 'a0000000-0000-4000-8000-000000000504', 'a0000000-0000-4000-8000-000000000401', 'a0000000-0000-4000-8000-000000000314', 'a0000000-0000-4000-8000-000000000301')
ON CONFLICT ("Id") DO UPDATE SET "BusId" = EXCLUDED."BusId", "StopId" = EXCLUDED."StopId", "RouteId" = EXCLUDED."RouteId";

-- One open task per staff member, in dbo."Tasks" (what TaskService reads; dbo."StaffTasks" is a
-- legacy table no endpoint reads). Assigned directly to each login; self-created (no FK on
-- CreatedByUserId). Re-runs reset the task to pending.
INSERT INTO "dbo"."Tasks" ("Id", "TenantId", "Title", "Detail", "AssignedToUserId", "Priority", "Status", "DueDate", "CreatedByUserId")
SELECT ('a0000000-0000-4000-8000-0000000007' || lpad(n::text, 2, '0'))::uuid, 'a0000000-0000-4000-8000-000000000001',
       'E2E task for staff ' || n, 'Seeded for the end-to-end check.',
       ('a0000000-0000-4000-8000-0000000001' || lpad(n::text, 2, '0'))::uuid,
       CASE WHEN n = 1 THEN 'urgent' ELSE 'normal' END, 'pending', CURRENT_DATE,
       ('a0000000-0000-4000-8000-0000000001' || lpad(n::text, 2, '0'))::uuid
FROM generate_series(1, 6) AS n
ON CONFLICT ("Id") DO UPDATE SET "Title" = EXCLUDED."Title", "AssignedToUserId" = EXCLUDED."AssignedToUserId",
  "Status" = 'pending', "DueDate" = EXCLUDED."DueDate", "PhotoUrl" = NULL, "CompletedByUserId" = NULL, "CompletedAt" = NULL;

-- Leave entitlements for the current year (casual 12, sick 10, earned 15) for every staff login.
INSERT INTO "dbo"."LeaveEntitlements" ("Id", "TenantId", "RequesterId", "Type", "Year", "TotalDays")
SELECT gen_random_uuid(), 'a0000000-0000-4000-8000-000000000001', ('a0000000-0000-4000-8000-0000000001' || lpad(n::text, 2, '0'))::uuid,
       t.type, EXTRACT(year FROM now())::int, t.days
FROM generate_series(1, 6) AS n
CROSS JOIN (VALUES ('casual', 12), ('sick', 10), ('earned', 15)) AS t(type, days)
ON CONFLICT ("TenantId", "RequesterId", "Type", "Year") DO UPDATE SET "TotalDays" = EXCLUDED."TotalDays";

COMMIT;
