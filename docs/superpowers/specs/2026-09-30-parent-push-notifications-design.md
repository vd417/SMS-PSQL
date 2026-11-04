# Parent Push Notifications — Backend Infra (Increment #2) — Design

**Date:** 2026-09-30
**Status:** Design (awaiting approval before implementation plan)
**Scope:** Backend only (`sms-api`, worktree `sms-api-e2e-wt`, branch `feat/sms-api-e2e-impl`). The parent app work (registration UI, handlers, deep-link) is Increment #3; full test matrix is Increment #4.

## 1. Goal

The `BusParentAlertService` already creates in-app notification rows for a child's parent when a trip starts and when the bus approaches the child's stop (hardened in Increment #1). This increment adds **real device push** (Expo) so those same notices reach the parent's phone, not just the in-app inbox.

A parent's device registers an Expo push token; when the alert service fans a notice out to a `parentId`, it also best-effort pushes to that parent's registered devices — for **both** alert kinds (trip-started and approaching).

## 2. Scope

**In scope**
- A `ParentDevices` table (Postgres forward migration `0005`), tenant-scoped with the house RLS pattern.
- A `DeviceTokenRepository` (upsert + per-user token lookup).
- `POST /v1/me/devices` — idempotent upsert of the caller's device token.
- An `IExpoPushSender` typed HTTP client (best-effort, never throws).
- Wiring push into `BusParentAlertService` for both alert kinds.
- DI registration; unit + integration tests.

**Out of scope (later increments / future)**
- Expo push **receipt polling** and pruning of dead/`DeviceNotRegistered` tokens (deferred — best-effort send only).
- `DELETE /v1/me/devices` (unregister on logout).
- Staff/teacher push; `web` platform.
- Per-user notification-preference gating (no "bus alerts" toggle exists in `UserAppSettings`).
- The parent app changes (Increment #3) and real on-device delivery verification.

## 3. Verified codebase facts this design is built on

Independently confirmed by reading the repo (2026-09-30):

- **Migrations:** `db/Sms.Migrations/MigrateCli.cs` no longer exists — FluentMigrator (`M####`, SQL Server) is dead at runtime. The live path is `Sms.PgMigrator`: frozen baseline `db/postgres/00–99_*.sql` + forward migrations `db/postgres/migrations/NNNN_*.sql`. `PostgresFixture` builds baseline + all forwards. **New schema ships as a forward migration only; no baseline edit, no FluentMigrator file.** Next free number is **`0005`**. Every new table/function must `GRANT` to `sms_app` in the same file (forward `0001` ends with `GRANT ... TO sms_app;`).
- **RLS:** helpers `rls.current_tenant_id()` and `rls.is_platform()` exist; there is **no `rls.current_user_id()`**, and no policy reads `app.user_id`. Every table (incl. `BusParentAlerts`, `07_rls_policies.sql:125–130`) uses the same 4-policy tenant pattern. `app.user_id` *is* stamped on the connection (`NpgsqlConnectionFactory.cs:28`) — usable in query `WHERE` clauses, not in policies.
- **No push/device table exists** anywhere in `db/postgres/**`.
- **HTTP client template:** `ISmsSender`/`LoggingSmsSender` is a logging stub (not HTTP). The real typed-HTTP pattern is `GoogleRoutesClient` + `GoogleRoutesOptions`, registered as `Configure<Options>` + `AddHttpClient("name")` + `AddSingleton<IClient, Client>` (`ServiceCollectionExtensions.cs:179–181`). Contract: named client, `IOptions`, per-call linked-CTS timeout, **return-null/log-warning/never-throw** on provider failure.
- **Controllers are thin** (`ApiControllerBase` → `FromResult`/`DataEnvelope`/`ErrorEnvelope`); current user/tenant is read **in the service** via `ITenantContext` (`TenantId`,`UserId`,`IsPlatform`) with a null-guard → 403 (`ProfileService.cs:21`). `/v1/me/*` routes already exist. JSON is snake_case globally; request DTOs are positional `sealed record`s.
- **Per-user table analog:** `UserAppSettings` (`UserId`,`TenantId`,…,`UpdatedAt`) with tenant RLS and an upsert repo — the pattern to mirror (but its PK is `UserId`; devices need their own `Id` PK since one user has many devices).
- **Alert hook:** `BusParentAlertService.NotifyAsync` loops on `parentId` right after `comms.CreateNotificationAsync`; the whole method is try/catch-logged; `TryInsertParentAlertAsync` already dedupes per trip/student/parent/kind. Its current ctor deps are `StudentBusRepository riders, CommsRepository comms, ILiveBroadcaster live, ILogger<> logger`.

## 4. Data model

New table `dbo."ParentDevices"` (delivered in `db/postgres/migrations/0005_parent_devices.sql`):

| Column | Type | Notes |
|---|---|---|
| `Id` | `uuid` `DEFAULT gen_random_uuid()` NOT NULL | PK (one user → many devices) |
| `TenantId` | `uuid` NOT NULL | RLS scope |
| `UserId` | `uuid` NOT NULL | the owning app user (parent) |
| `ExpoPushToken` | `text` NOT NULL | e.g. `ExponentPushToken[xxx]` |
| `Platform` | `text` NOT NULL | `ios` or `android` (validated in app) |
| `CreatedAt` | `timestamptz` `DEFAULT now()` NOT NULL | |
| `UpdatedAt` | `timestamptz` `DEFAULT now()` NOT NULL | bumped on upsert |

**Constraints / indexes**
- `PK_ParentDevices PRIMARY KEY ("Id")`
- `UX_ParentDevices_Tenant_Token UNIQUE ("TenantId","ExpoPushToken")` — **not** globally unique (see §4.1)
- `IX_ParentDevices_Tenant_User ("TenantId","UserId")` — the fan-out lookup

**RLS** — copy the `BusParentAlerts` block verbatim, retargeted:
```sql
ALTER TABLE "dbo"."ParentDevices" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."ParentDevices" FORCE ROW LEVEL SECURITY;
CREATE POLICY "ParentDevicesTenantPolicy_select" ON "dbo"."ParentDevices" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ParentDevicesTenantPolicy_update" ON "dbo"."ParentDevices" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ParentDevicesTenantPolicy_delete" ON "dbo"."ParentDevices" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ParentDevicesTenantPolicy_insert" ON "dbo"."ParentDevices" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
```
**Grant** (mandatory, same file):
```sql
GRANT SELECT, INSERT, UPDATE, DELETE ON "dbo"."ParentDevices" TO sms_app;
```

### 4.1 Uniqueness — `(TenantId, ExpoPushToken)`, not global (approved)
- A parent with children in **multiple schools** uses one device; the same Expo token must be reachable from **each tenant** → global-unique would bind it to one tenant only.
- Cross-tenant `ON CONFLICT DO UPDATE` **fails under FORCE RLS** (the existing row's `TenantId` isn't visible to the other tenant). Scoping the conflict target to `(TenantId, ExpoPushToken)` keeps every upsert within the current tenant → RLS-safe.
- Within a tenant, the same token re-registering (device handed to a different parent, or re-login) updates `UserId`/`Platform`/`UpdatedAt`.

## 5. Repository — `DeviceTokenRepository`

`sealed class DeviceTokenRepository(IDbConnectionFactory factory) : BaseRepository(factory)` in the **Comms module** (`src/Sms.Modules.Comms/CommsModule.cs`), registered in `AddCommsModule`.

- `Task UpsertAsync(Guid tenantId, Guid userId, string expoPushToken, string platform, CancellationToken ct)`
  ```sql
  INSERT INTO "dbo"."ParentDevices" ("TenantId","UserId","ExpoPushToken","Platform")
  VALUES (@tenantId,@userId,@token,@platform)
  ON CONFLICT ("TenantId","ExpoPushToken")
  DO UPDATE SET "UserId" = EXCLUDED."UserId", "Platform" = EXCLUDED."Platform", "UpdatedAt" = now();
  ```
- `Task<IReadOnlyList<string>> ListTokensForUserAsync(Guid tenantId, Guid userId, CancellationToken ct)`
  → `SELECT "ExpoPushToken" FROM "dbo"."ParentDevices" WHERE "TenantId"=@tenantId AND "UserId"=@userId`
  (tenant also enforced by RLS; `TenantId` in the predicate keeps it index-friendly and explicit).

Mirrors the `UserAppSettingsRepository` upsert idiom.

## 6. Expo sender — `IExpoPushSender`

Interface (new, `Sms.Application` or `Sms.Shared.Kernel` beside the other clients — final home to match `GoogleRoutesClient`'s project):
```csharp
public interface IExpoPushSender
{
    Task SendAsync(IReadOnlyList<string> expoPushTokens, string title, string body,
        IReadOnlyDictionary<string, object?>? data = null, CancellationToken ct = default);
}
```
`ExpoPushSender` — models `GoogleRoutesClient`:
- ctor: `IHttpClientFactory httpClientFactory, IOptions<ExpoPushOptions> options, ILogger<ExpoPushSender> log`.
- `httpClientFactory.CreateClient("expo")`; `POST {BaseUrl}/--/api/v2/push/send`.
- **Chunk** tokens into batches of ≤`MaxBatchSize` (100). One request per batch; body is one message object per batch with `"to": [tokens…]`, `title`, `body`, `data`, `"sound":"default"`.
- Optional `Authorization: Bearer {AccessToken}` when configured.
- Per-call timeout via a linked `CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds))` linked with `ct`.
- **Best-effort:** empty token list → no-op; non-2xx → `log.LogWarning` and continue; `catch (HttpRequestException or TaskCanceledException or JsonException)` → log, swallow. **Never throws.**

`ExpoPushOptions` (mirrors `GoogleRoutesOptions`):
```csharp
public sealed class ExpoPushOptions
{
    public const string SectionName = "Expo";
    public string BaseUrl { get; set; } = "https://exp.host";
    public string? AccessToken { get; set; }          // optional; Expo push works without it
    public int TimeoutSeconds { get; set; } = 10;
    public int MaxBatchSize { get; set; } = 100;
}
```

## 7. Device-registration API

- **Route:** `POST /v1/me/devices` via a new thin `DevicesController : ApiControllerBase`, `[Route("v1/me")]`, `[HttpPost("devices")]`. (Consistent with existing `/v1/me/*` routes.)
- **Auth:** `[Authorize]` — any authenticated app user may register a device (see open question §12.1).
- **Request DTO:** `sealed record RegisterDeviceRequest(string ExpoPushToken, string Platform);` (JSON `expo_push_token`, `platform`).
- **Service:** `IDeviceService.RegisterAsync(RegisterDeviceRequest req, CancellationToken ct)` (registered in `Sms.Application/DependencyInjection.cs`):
  - read `ITenantContext`; `TenantId`/`UserId` null → `403 forbidden`.
  - validate: blank token → `422 invalid_token`; `platform ∉ {ios, android}` → `422 invalid_platform`.
  - `await devices.UpsertAsync(tid, uid, token, platform, ct)` → `ApiResult.Ok()`.
- **Response:** **200 OK** on both create and update (idempotent upsert), empty body via `FromResult(ApiResult)`.

## 8. Alert integration

Add `IExpoPushSender push` and `DeviceTokenRepository devices` to `BusParentAlertService`'s primary ctor. In `NotifyAsync`, inside the existing `parentId` loop, **after** `comms.CreateNotificationAsync(...)`:
```csharp
var tokens = await devices.ListTokensForUserAsync(tenantId, parentId, ct);
if (tokens.Count > 0)
    await push.SendAsync(tokens, title, body,
        data: new Dictionary<string, object?> { ["kind"] = kind, ["trip_id"] = tripId }, ct);
```
- Fires for **both** kinds (the method serves trip-started and approaching).
- Rides on the existing per-trip/student/parent/kind dedupe (only runs when `TryInsertParentAlertAsync` succeeded) and the method-level try/catch. Combined with the sender's own never-throw contract, **a push failure cannot break the in-app notice or the ping/trip flow.**
- `data` carries `kind` + `trip_id` for the app's tap→deep-link (finalized in Increment #3).

## 9. DI wiring

- `src/Sms.Modules.Comms/CommsModule.cs` → `AddCommsModule`: `services.AddScoped<DeviceTokenRepository>();`
- `src/Sms.Api/Extensions/ServiceCollectionExtensions.cs` (beside l.179–181):
  ```csharp
  builder.Services.Configure<ExpoPushOptions>(builder.Configuration.GetSection(ExpoPushOptions.SectionName));
  builder.Services.AddHttpClient("expo");
  builder.Services.AddSingleton<IExpoPushSender, ExpoPushSender>();
  ```
- `src/Sms.Application/DependencyInjection.cs`: `services.AddScoped<IDeviceService, DeviceService>();`

## 10. Configuration

`appsettings` section `Expo` (all optional; defaults in `ExpoPushOptions`):
```json
"Expo": { "BaseUrl": "https://exp.host", "AccessToken": null, "TimeoutSeconds": 10, "MaxBatchSize": 100 }
```
No secret required for basic Expo push; `AccessToken` only if the project later enables enhanced security.

## 11. Testing plan (TDD — write failing tests first)

**Unit (`Sms.Tests.Unit`)**
- `ExpoPushSender`: with a stub `HttpMessageHandler` injected via a test `IHttpClientFactory` —
  - payload shape: one `POST /--/api/v2/push/send`, body has `to`/`title`/`body`/`data`.
  - chunking: 150 tokens → 2 requests (100 + 50).
  - best-effort: handler returns 500 / throws → `SendAsync` completes without throwing.
  - empty token list → no HTTP call.
- Platform/token validation as a small pure check (boundary: `ios`/`android` ok, `web`/`""` rejected).

**Integration (`Sms.Tests.Integration`, `PostgresFixture`)**
- **Migration/RLS:** `ParentDevices` exists; a row inserted under tenant A is invisible/non-updatable under tenant B (model `ClassesScopingTests`/`StudentAttendanceScopeTests`).
- **`POST /v1/me/devices`:** register → 200 + one row; re-register same token → still one row, `UserId`/`Platform`/`UpdatedAt` updated (upsert); `platform:"web"` → 422; blank token → 422; no auth → 401.
- **Multi-tenant token:** same token registered under tenant A and tenant B → two rows (one per tenant).
- **Push fan-out** (spy `IExpoPushSender` via `ConfigureTestServices`, reuse Increment #1's approach-alert seeding + a registered device): parent with a token → sender invoked with that token on **approaching** and on **trip-started**; parent with no token → sender not invoked; the in-app `Notifications` row is created either way.
- **Best-effort isolation:** spy sender throws → ping ingest still returns 204 and the in-app notice still exists.

**Gate:** `dotnet test` (Unit + Comms/Transport integration) green. Real Expo delivery needs a real device token (Increment #3 / manual).

## 12. Resolved decisions (confirmed 2026-09-30)

1. **Authorization for `POST /v1/me/devices`** — **`[Authorize]`, any authenticated user** (device registration is harmless and reusable by future staff push).
2. **Preference gating** — **send unconditionally** this increment; a `UserAppSettings` "bus alerts" toggle is future work.
3. **`data` payload** — **defer to Increment #3**; send `data:{ kind, trip_id }` now, add deep-link fields (`student_id`/stop/route) when the app tap-handling is built.

## 13. Sub-increment order (for the implementation plan)

1. Migration `0005_parent_devices.sql` + migration/RLS test.
2. `DeviceTokenRepository` + repo/upsert tests.
3. `IExpoPushSender`/`ExpoPushSender`/`ExpoPushOptions` + sender unit tests + DI.
4. `IDeviceService` + `DevicesController` + endpoint integration tests + DI.
5. Wire push into `BusParentAlertService` + fan-out/best-effort integration tests.
6. Full `dotnet test`; no commit/push/deploy without approval.
