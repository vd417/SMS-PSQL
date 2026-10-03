# Teacher Bus Push Notifications — Design

**Date:** 2026-10-02
**Status:** Design (awaiting approval before implementation plans)
**Scope:** Three repos — backend (`sms-api`, worktree `sms-api-e2e-wt`, branch `feat/sms-api-e2e-impl`), the teacher app (`sms-teacher-app`), and the admin app (`sms-admin`). Builds on the parent/student bus push already shipped (Increments #1–#3 of the parent-push feature).

## 1. Goal

Today the bus alert service pushes **trip-started** and **approaching-stop** notices only to a child's parent/student accounts. This feature extends bus push to **teachers tied to a bus**:

- **Duty teacher** (`BusAssignments`) and **traveling teacher** (`BusTravelingTeachers`) → get **trip started** and **trip ended**.
- **Stoppage-mapped teacher** (a traveling teacher with a mapped stop) → *also* gets **"bus ~1 km away"** from that stop.

And it gives the teacher app the device-push stack it currently lacks, plus an admin screen to assign a traveling teacher + their stop to a bus.

## 2. Scope

**In scope**
- Backend: nullable `StopId` on `BusTravelingTeachers`; a `BusTeacherAlerts` dedupe table; teacher recipient resolution; teacher notifications for **trip started**, **trip ended** (new event), and **approaching (1 km)** for stop-mapped teachers; teacher-worded messages; best-effort Expo push reusing the existing sender + `/v1/me/devices`; API to set a traveling teacher's stop; DI; unit + integration tests.
- Teacher app (`sms-teacher-app`): full push stack — `expo-notifications`, permission + token registration, `devices.register` → `POST /v1/me/devices`, a `<PushNotifications/>` mounted in `RootNavigator`, deep-link of a bus-push tap to the existing `BusScreen`, jest tests.
- Admin app (`sms-admin`): UI + API wiring to assign a traveling teacher to a bus **with an optional stop**.

**Out of scope (deferred / future)**
- Approaching alerts for **duty** teachers (they ride the whole route; no single stop). Only stop-mapped traveling teachers get approaching.
- **Trip-ended for parents/students** (this feature adds trip-ended for teachers only; parent behavior is unchanged).
- Expo receipt polling / dead-token pruning; `DELETE /v1/me/devices` on logout (a pre-existing deferred item, tracked separately).
- Multiple stops per teacher; per-teacher notification-preference toggles.

## 3. Verified codebase facts (confirmed 2026-10-02)

- **Recipient tables already key on a user id** — no staff→user join needed:
  - `BusAssignments ("TenantId","TeacherUserId","BusId")` — the duty teacher.
  - `BusTravelingTeachers ("Id","TenantId","BusId","TeacherUserId")` — teachers who ride. **No `StopId` today.**
- **No teacher→stop mapping exists anywhere.** → this feature adds nullable `StopId` to `BusTravelingTeachers` (approved option).
- **Approach distance is already 1 km:** `BusParentAlertRules.DefaultApproachMeters = 1000` (`src/Sms.Modules.Transport/BusParentAlertRules.cs`); freshness `MaxStalenessSeconds = 60`, accuracy `MaxAccuracyMeters = 200`. The teacher 1 km rule reuses this as-is.
- **Alert hooks in `TripService`:** started at `:98` `parentAlerts.NotifyTripStartedAsync(tid, busId, trip.Id, ct)`; approaching at `:178` `NotifyApproachingStopsAsync(...)`; `EndAsync` at `:184` (ends trip, broadcasts `BroadcastTripEndedAsync`) — **no alert fired on end today**, so trip-ended is a new path.
- **Dedupe pattern:** `BusParentAlerts ("Id","TenantId","TripId","StudentId","ParentUserId","Kind","CreatedAt")` + `TryInsertParentAlertAsync` (insert-if-not-exists per trip/student/parent/kind). Teachers mirror this with `BusTeacherAlerts`.
- **Push infra reused:** `IExpoPushSender` (best-effort, never throws), `DeviceTokenRepository.ListTokensForUserAsync(tenantId, userId)`, `POST /v1/me/devices` (any authenticated user). No new push infra.
- **Migrations:** forward-only `db/postgres/migrations/NNNN_*.sql`; next free number is **0006**; every new table GRANTs to `sms_app` and uses the 4-policy tenant RLS pattern.
- **Admin app** `sms-admin` owns transport UI (`src/api/transport.ts`, `transport.test.ts`, `api/hooks/useOperations.ts`). **Teacher app** `sms-teacher-app` has `RootNavigator.tsx`, `AppProviders.tsx`, a `BusScreen.tsx`, and **no `src/services/` dir** (its data layer differs from the parent app — adapt, don't copy) and **no `expo-notifications`**.
- **Backend API already manages teachers↔bus:** `TransportController` has `AssignBusTeacher` (duty) and `AddTravelingTeacher`/`RemoveTravelingTeacher`; `AddTravelingTeacherAsync(busId, teacherUserId)` is the method to extend with an optional stop.

## 4. Data model

### 4.1 `BusTravelingTeachers` — add `StopId` (migration 0006)
```sql
ALTER TABLE "dbo"."BusTravelingTeachers" ADD COLUMN "StopId" uuid NULL;
```
- Nullable. Set → teacher is "stoppage-mapped" and receives the 1 km approaching alert for that stop. Null → started/ended only.
- No FK enforced (matches the loose `StudentBusAssignments.StopId`, which resolves against either `RouteStops` or `BusStops` via COALESCE). The stop name for the message is resolved the same COALESCE way at send time.

### 4.2 New `BusTeacherAlerts` — dedupe (migration 0006, same file)
Mirror of `BusParentAlerts`, student column dropped:
```sql
CREATE TABLE "dbo"."BusTeacherAlerts" (
    "Id" uuid DEFAULT gen_random_uuid() NOT NULL,
    "TenantId" uuid NOT NULL,
    "TripId" uuid NOT NULL,
    "TeacherUserId" uuid NOT NULL,
    "Kind" varchar(40) NOT NULL,
    "CreatedAt" timestamptz DEFAULT now() NOT NULL
);
ALTER TABLE "dbo"."BusTeacherAlerts" ADD CONSTRAINT "PK_BusTeacherAlerts" PRIMARY KEY ("Id");
CREATE UNIQUE INDEX "UX_BusTeacherAlerts_Trip_Teacher_Kind"
    ON "dbo"."BusTeacherAlerts" ("TenantId","TripId","TeacherUserId","Kind");
-- + ENABLE/FORCE RLS, 4 tenant policies (is_platform() OR "TenantId" = rls.current_tenant_id())
-- + GRANT SELECT, INSERT, UPDATE, DELETE ON "dbo"."BusTeacherAlerts" TO sms_app;
```
- Kinds: `trip_started`, `trip_ended`, `approaching_stop` (reuse `BusParentAlertRules.*` constants; add `TripEnded = "trip_ended"`).

## 5. Backend behavior

### 5.1 New service: `BusTeacherAlertService`
A sibling to `BusParentAlertService` (keeps parent logic untouched, teacher wording/recipients separate). Interface:
```csharp
public interface IBusTeacherAlertService
{
    Task NotifyTripStartedAsync(Guid tenantId, Guid busId, Guid tripId, CancellationToken ct = default);
    Task NotifyTripEndedAsync(Guid tenantId, Guid busId, Guid tripId, CancellationToken ct = default);
    Task NotifyApproachingStopsAsync(Guid tenantId, Guid busId, Guid tripId,
        double busLat, double busLng, DateTime? lastPingAtUtc, double? accuracyMeters,
        CancellationToken ct = default);
}
```
- Same ctor-dep style as `BusParentAlertService`: a transport repo (teacher recipients + stops), `CommsRepository`, `DeviceTokenRepository`, `IExpoPushSender`, `ILogger`. Whole body try/catch-logged; push is best-effort (never breaks trip/ping flow).

### 5.2 Recipient resolution (new repo methods on the Transport module)
- `ListBusTeacherUserIdsAsync(busId)` → `SELECT "TeacherUserId" FROM "BusAssignments" WHERE "BusId"=@busId UNION SELECT "TeacherUserId" FROM "BusTravelingTeachers" WHERE "BusId"=@busId` (distinct). Used for **started/ended**.
- `ListStopMappedTeachersAsync(busId)` → traveling teachers with a non-null `StopId`, joined to the stop's lat/lng/name (COALESCE RouteStops/BusStops). Used for **approaching**.

### 5.3 Events
- **Trip started** — in `TripService` (`:98`), after the existing parent call add `await teacherAlerts.NotifyTripStartedAsync(tid, busId, trip.Id, ct)`. Fan out to `ListBusTeacherUserIdsAsync`; per teacher, dedupe on `(trip, teacher, trip_started)` → in-app `Notification` + best-effort push.
- **Trip ended** — in `TripService.EndAsync` (`:184`, after `repo.EndAsync`/broadcast), add `await teacherAlerts.NotifyTripEndedAsync(tid, busId, tripId, ct)`. Same fan-out, kind `trip_ended`. (No parent equivalent.)
- **Approaching (1 km)** — in `TripService` (`:178`), beside the parent approaching call, add `teacherAlerts.NotifyApproachingStopsAsync(...)` with the same ping lat/lng/freshness/accuracy args. For each stop-mapped teacher within `IsWithinApproach` (1 km) of their stop, dedupe `(trip, teacher, approaching_stop)` → in-app + push. Same freshness/accuracy gating as parents.

### 5.4 Messages (teacher-worded; no "your child")
- started: title `"Bus started"`, body `"Bus {BusNo} started its {direction} trip."`
- ended: title `"Bus trip ended"`, body `"Bus {BusNo} has ended its {direction} trip."`
- approaching: title `"Bus near your stop"`, body `"Bus {BusNo} is about 1 km from {StopName}."`
- In-app notice uses icon/tone `"bus"` (same as parent), `UserId = teacherUserId`.
- Push `data`: `{ kind, trip_id }` (deep-links to the teacher app's `BusScreen`).

### 5.5 API — set a traveling teacher's stop
- Extend the traveling-teacher endpoint to carry an optional stop. Preferred: `PUT /v1/transport/buses/{busId}/traveling-teachers/{teacherUserId}` body `{ stop_id?: uuid|null }` → upsert the row with `StopId`. `RemoveTravelingTeacher` unchanged. `AddTravelingTeacherAsync(busId, teacherUserId, stopId?)` + repo upsert of `StopId`.

### 5.6 DI
- Register `IBusTeacherAlertService` in `Sms.Application/DependencyInjection.cs`; new repo methods live on the existing Transport module repo (already DI-registered). No new push/options registration (reuses Expo infra).

## 6. Teacher app (`sms-teacher-app`)

Mirror the parent-app push stack, **adapted to this app's architecture** (study `RootNavigator.tsx`, `AppProviders.tsx`, its API/data layer, and `BusScreen` routing before coding):
- `expo-notifications` via `npx expo install` (SDK-matched — confirm the app's Expo SDK and read that version's `expo-notifications` docs before writing native code).
- Pure helpers (platform + payload→destination), best-effort `registerForPushNotificationsAsync`, a `devices.register` call (`POST /me/devices { expo_push_token, platform }`) in this app's data layer, and a `<PushNotifications/>` component mounted authed-only in `RootNavigator` that: registers on login, routes a tapped bus push to `BusScreen` (guard `navigationRef.isReady()`), and is **platform-guarded** (no listener wiring on web; `.catch` on the cold-start lookup) — carrying forward the parent-app review fix.
- Jest tests for the pure routing + a platform-guard render test.

## 7. Admin app (`sms-admin`)

- Extend `src/api/transport.ts` (+ `transport.test.ts`) with the stop in the traveling-teacher assign call.
- A screen/flow to pick a traveling teacher for a bus **and optionally their stop** (from the bus's route stops), calling the extended API. Follow the app's existing transport UI patterns; tests per the app's convention.

## 8. Testing plan (TDD)

**Backend — Unit:** teacher message wording; recipient-resolution SQL shape is covered by integration.
**Backend — Integration (`PostgresFixture`, `SMS_TEST_PG_PASSWORD=12345678`):**
- migration 0006: `BusTravelingTeachers.StopId` exists; `BusTeacherAlerts` exists, tenant-isolated (RLS), unique on `(TenantId,TripId,TeacherUserId,Kind)`.
- started/ended: duty + traveling teachers each get one in-app notice + one push (with a registered device); dedupe → re-trigger makes no second row.
- approaching: a stop-mapped teacher within 1 km gets the alert; one not mapped (null StopId) does not; freshness/accuracy gating holds.
- best-effort isolation: spy sender throws → ping/trip still succeeds (204/200) and the in-app notice persists.
- API: assigning a traveling teacher with a stop stores `StopId`; without → null.
**Teacher app / admin app:** jest + typecheck + lint green (mirror the parent-app gates; real on-device delivery is manual).

**Gate:** `dotnet test` (Unit + Comms/Transport integration) green; teacher-app + admin-app suites green. No commit/push/deploy without approval.

## 9. Increment order (one plan each)
1. **Backend** — migration 0006, repo methods, `BusTeacherAlertService`, `TripService` hooks (started/ended/approaching), API stop extension, DI, tests. *(Contract source for 2 & 3.)*
2. **Teacher app** — push stack + deep-link to `BusScreen` + tests.
3. **Admin app** — traveling-teacher + stop assignment UI/API + tests.

## 10. Risks / open points
- **Duty teachers have no stop** → intentionally no approaching alert for them (only started/ended). Confirmed.
- **Loose `StopId`** (no FK, resolves against RouteStops *or* BusStops) mirrors the existing student-stop handling; a stale/deleted stop simply yields no approaching match.
- **Teacher app SDK/architecture unknowns** resolved at the start of Increment 2 (its own plan).
- **Shared-device logout gap** (pre-existing): a signed-out teacher's device keeps receiving until re-register — same deferred `DELETE /v1/me/devices` follow-up as the parent app.
