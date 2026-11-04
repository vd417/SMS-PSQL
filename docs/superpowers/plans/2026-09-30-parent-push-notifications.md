# Parent Push Notifications (Increment #2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver the existing parent bus notices (trip-started + approaching) as real Expo device push, driven by a new device-token table and a `POST /v1/me/devices` registration endpoint.

**Architecture:** A tenant-scoped `ParentDevices` table (Postgres forward migration) stores Expo push tokens per app user. A best-effort `IExpoPushSender` (typed HTTP client over `https://exp.host`) is invoked from `BusParentAlertService` right after each in-app notification is created, keyed on the same `parentId`, reusing the existing per-trip dedupe. Push failures never touch the in-app/trip flow.

**Tech Stack:** .NET 10 / C#, Dapper + Npgsql (PostgreSQL), `Sms.PgMigrator` forward migrations, xUnit + FluentAssertions, `WebApplicationFactory<Program>` integration tests against a real disposable Postgres (`PostgresFixture`).

**Spec:** `docs/superpowers/specs/2026-09-30-parent-push-notifications-design.md`

## Global Constraints

- Work **only** in the `sms-api-e2e-wt` worktree (branch `feat/sms-api-e2e-impl`). Never touch the main `sms-api` folder or the `feat/users-personid` branch.
- Schema ships as a **Postgres forward migration only**: `db/postgres/migrations/0005_parent_devices.sql`. No baseline edit; **no FluentMigrator `M####` file** (dead at runtime). No `CREATE INDEX CONCURRENTLY` (migrations run in one transaction each).
- Every new table **must** `GRANT SELECT, INSERT, UPDATE, DELETE ... TO sms_app` in the same migration file (integration tests connect as `sms_app`).
- RLS: the **tenant-only 4-policy pattern** (`ENABLE`+`FORCE`, select/update/delete/insert, `rls.is_platform() OR "TenantId" = rls.current_tenant_id()`). No user-level RLS exists — enforce user isolation in repo `WHERE "UserId" = @userId`.
- Uniqueness is **`UNIQUE ("TenantId","ExpoPushToken")`**, NOT global. PK is a dedicated `Id uuid DEFAULT gen_random_uuid()`.
- `Platform` accepted values: exactly **`ios`** or **`android`**; anything else → `422 invalid_platform`. Blank token → `422 invalid_token`.
- `POST /v1/me/devices` → **`200 OK`** for both create and update (idempotent upsert). `[Authorize]` = any authenticated user.
- Push is **best-effort**: `IExpoPushSender` never throws; it batches ≤100 tokens/request; `data` payload is `{ kind, trip_id }` this increment; **no receipt polling / token pruning**.
- JSON is snake_case project-wide; request DTOs are positional `sealed record`s; responses go through `ApiResult`/`ApiResult<T>` → `DataEnvelope`/`ErrorEnvelope`.
- Run integration tests with `SMS_TEST_PG_PASSWORD=12345678` (local superuser `postgres`). Unit tests need no DB.
- Do not commit, push, or deploy unless the executing method/user says so.

## Review Focus

- **Cross-tenant token reuse:** the same Expo token registered under tenant A and then tenant B must create a *second* row (one per tenant) and never error under FORCE RLS — pinned in Task 2 (repo) and Task 4 (endpoint) multi-tenant tests.
- **Expo outage / dead token mid-trip:** a failing push must not break ping ingest or the in-app notice — pinned in Task 5 best-effort test (spy sender throws → ingest still `204`, `Notifications` row still present).
- **Large fan-out (>100 tokens):** must chunk into multiple requests — pinned in Task 3 chunking test (150 tokens → 2 requests).
- **Unauthenticated / missing tenant-user context:** registration must return `401` (no JWT) / `403` (no tenant/user) — pinned in Task 4.
- **Idempotent re-registration:** re-posting the same token must leave exactly one row (updating user/platform/updatedAt), not accumulate duplicates — pinned in Task 2 and Task 4.

---

### Task 1: `ParentDevices` migration + RLS/uniqueness test

**Files:**
- Create: `db/postgres/migrations/0005_parent_devices.sql`
- Test: `tests/Sms.Tests.Integration/Comms/ParentDevicesSchemaTests.cs`

**Interfaces:**
- Consumes: the migration runner (`PostgresFixture` builds baseline + all forward migrations) and RLS helpers `rls.current_tenant_id()`, `rls.is_platform()`.
- Produces: table `"dbo"."ParentDevices"` with columns `Id, TenantId, UserId, ExpoPushToken, Platform, CreatedAt, UpdatedAt`; `UNIQUE ("TenantId","ExpoPushToken")`; tenant RLS; granted to `sms_app`.

- [ ] **Step 1: Write the failing test**

`tests/Sms.Tests.Integration/Comms/ParentDevicesSchemaTests.cs`:
```csharp
using Dapper;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Sms.Tests.Integration.Comms;

[Collection("sql")]
public class ParentDevicesSchemaTests(PostgresFixture fx)
{
    private static async Task<NpgsqlConnection> Open(string cs, Guid tenantId)
    {
        var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        return conn;
    }

    [Fact]
    public async Task Rows_are_tenant_isolated_by_rls()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var token = $"ExponentPushToken[{Guid.NewGuid():N}]";

        await using (var a = await Open(fx.ConnectionString, tenantA))
            await a.ExecuteAsync(
                "INSERT INTO \"dbo\".\"ParentDevices\" (\"TenantId\",\"UserId\",\"ExpoPushToken\",\"Platform\") VALUES (@t,@u,@k,'ios')",
                new { t = tenantA, u = Guid.NewGuid(), k = token });

        await using var b = await Open(fx.ConnectionString, tenantB);
        var visibleToB = await b.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM \"dbo\".\"ParentDevices\" WHERE \"ExpoPushToken\" = @k", new { k = token });
        visibleToB.Should().Be(0, "tenant B must not see tenant A's device rows");
    }

    [Fact]
    public async Task Same_token_twice_in_one_tenant_violates_the_unique_index()
    {
        var tenantId = Guid.NewGuid();
        var token = $"ExponentPushToken[{Guid.NewGuid():N}]";
        await using var conn = await Open(fx.ConnectionString, tenantId);
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"ParentDevices\" (\"TenantId\",\"UserId\",\"ExpoPushToken\",\"Platform\") VALUES (@t,@u,@k,'ios')",
            new { t = tenantId, u = Guid.NewGuid(), k = token });

        var act = () => conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"ParentDevices\" (\"TenantId\",\"UserId\",\"ExpoPushToken\",\"Platform\") VALUES (@t,@u,@k,'android')",
            new { t = tenantId, u = Guid.NewGuid(), k = token });

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `SMS_TEST_PG_PASSWORD=12345678 dotnet test tests/Sms.Tests.Integration --filter "FullyQualifiedName~ParentDevicesSchemaTests"`
Expected: FAIL — migration missing, so `"dbo"."ParentDevices"` does not exist (`42P01 relation ... does not exist`).

- [ ] **Step 3: Write the migration**

`db/postgres/migrations/0005_parent_devices.sql`:
```sql
-- 0005: dbo.ParentDevices — Expo push tokens for parent-app device push (Increment #2).
-- Tenant-scoped RLS (same 4-policy pattern as every other table). User isolation is enforced in
-- repo queries (WHERE "UserId" = ...), matching the codebase (there is no user-level RLS).
-- UNIQUE ("TenantId","ExpoPushToken") is deliberately NOT global: a device shared across schools
-- registers once per tenant, and upsert stays within the current tenant so it is RLS-safe.

CREATE TABLE "dbo"."ParentDevices" (
    "Id" uuid DEFAULT gen_random_uuid() NOT NULL,
    "TenantId" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "ExpoPushToken" text NOT NULL,
    "Platform" text NOT NULL,
    "CreatedAt" timestamptz DEFAULT now() NOT NULL,
    "UpdatedAt" timestamptz DEFAULT now() NOT NULL
);

ALTER TABLE "dbo"."ParentDevices" ADD CONSTRAINT "PK_ParentDevices" PRIMARY KEY ("Id");
CREATE UNIQUE INDEX "UX_ParentDevices_Tenant_Token" ON "dbo"."ParentDevices" ("TenantId", "ExpoPushToken");
CREATE INDEX "IX_ParentDevices_Tenant_User" ON "dbo"."ParentDevices" ("TenantId", "UserId");

ALTER TABLE "dbo"."ParentDevices" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "dbo"."ParentDevices" FORCE ROW LEVEL SECURITY;
CREATE POLICY "ParentDevicesTenantPolicy_select" ON "dbo"."ParentDevices" FOR SELECT USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ParentDevicesTenantPolicy_update" ON "dbo"."ParentDevices" FOR UPDATE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id()) WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ParentDevicesTenantPolicy_delete" ON "dbo"."ParentDevices" FOR DELETE USING (rls.is_platform() OR "TenantId" = rls.current_tenant_id());
CREATE POLICY "ParentDevicesTenantPolicy_insert" ON "dbo"."ParentDevices" FOR INSERT WITH CHECK (rls.is_platform() OR "TenantId" = rls.current_tenant_id());

GRANT SELECT, INSERT, UPDATE, DELETE ON "dbo"."ParentDevices" TO sms_app;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `SMS_TEST_PG_PASSWORD=12345678 dotnet test tests/Sms.Tests.Integration --filter "FullyQualifiedName~ParentDevicesSchemaTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add db/postgres/migrations/0005_parent_devices.sql tests/Sms.Tests.Integration/Comms/ParentDevicesSchemaTests.cs
git commit -m "feat(push): add ParentDevices table (forward migration 0005) with tenant RLS"
```

---

### Task 2: `DeviceTokenRepository`

**Files:**
- Create: `src/Sms.Modules.Comms/DeviceTokenRepository.cs`
- Modify: `src/Sms.Modules.Comms/CommsModule.cs` (register in `AddCommsModule`)
- Test: `tests/Sms.Tests.Integration/Comms/DeviceTokenRepositoryTests.cs`

**Interfaces:**
- Consumes: `BaseRepository` (`ExecuteInlineAsync(sql, args, ct)`, `QueryInlineAsync<T>(sql, args, ct)`), `IDbConnectionFactory`, the `ParentDevices` table from Task 1.
- Produces:
  - `DeviceTokenRepository.UpsertAsync(Guid tenantId, Guid userId, string expoPushToken, string platform, CancellationToken ct = default) : Task<int>`
  - `DeviceTokenRepository.ListTokensForUserAsync(Guid tenantId, Guid userId, CancellationToken ct = default) : Task<IReadOnlyList<string>>`

- [ ] **Step 1: Write the failing test**

`tests/Sms.Tests.Integration/Comms/DeviceTokenRepositoryTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sms.Modules.Comms;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Comms;

[Collection("sql")]
public class DeviceTokenRepositoryTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static DeviceTokenRepository RepoFor(WebApplicationFactory<Program> app, Guid tenantId, Guid userId, out IServiceScope scope)
    {
        scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, userId, isPlatform: false);
        return scope.ServiceProvider.GetRequiredService<DeviceTokenRepository>();
    }

    [Fact]
    public async Task Upsert_then_list_round_trips_the_token()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var token = $"ExponentPushToken[{Guid.NewGuid():N}]";
        var repo = RepoFor(app, tenantId, userId, out var scope);
        using (scope)
        {
            await repo.UpsertAsync(tenantId, userId, token, "ios");
            (await repo.ListTokensForUserAsync(tenantId, userId)).Should().ContainSingle().Which.Should().Be(token);
        }
    }

    [Fact]
    public async Task Re_registering_a_token_moves_it_to_the_new_user_without_duplicating()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var oldUser = Guid.NewGuid();
        var newUser = Guid.NewGuid();
        var token = $"ExponentPushToken[{Guid.NewGuid():N}]";

        var repo1 = RepoFor(app, tenantId, oldUser, out var s1);
        using (s1) await repo1.UpsertAsync(tenantId, oldUser, token, "ios");

        var repo2 = RepoFor(app, tenantId, newUser, out var s2);
        using (s2)
        {
            await repo2.UpsertAsync(tenantId, newUser, token, "android");
            (await repo2.ListTokensForUserAsync(tenantId, newUser)).Should().ContainSingle();
            (await repo2.ListTokensForUserAsync(tenantId, oldUser)).Should().BeEmpty("the token moved to the new user");
        }
    }

    [Fact]
    public async Task A_token_registered_in_two_tenants_is_one_row_per_tenant()
    {
        await using var app = App();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var user = Guid.NewGuid();
        var token = $"ExponentPushToken[{Guid.NewGuid():N}]";

        var repoA = RepoFor(app, tenantA, user, out var sa);
        using (sa) await repoA.UpsertAsync(tenantA, user, token, "ios");
        var repoB = RepoFor(app, tenantB, user, out var sb);
        using (sb)
        {
            await repoB.UpsertAsync(tenantB, user, token, "ios");
            (await repoB.ListTokensForUserAsync(tenantB, user)).Should().ContainSingle();
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `SMS_TEST_PG_PASSWORD=12345678 dotnet test tests/Sms.Tests.Integration --filter "FullyQualifiedName~DeviceTokenRepositoryTests"`
Expected: FAIL to build — `DeviceTokenRepository` does not exist.

- [ ] **Step 3: Write the repository**

`src/Sms.Modules.Comms/DeviceTokenRepository.cs`:
```csharp
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Comms;

/// Expo push-notification device tokens for the parent app. Tenant-scoped (RLS by TenantId);
/// user isolation is applied in the WHERE clause. UNIQUE ("TenantId","ExpoPushToken") — upsert
/// re-homes a token to the latest user within the same tenant.
public sealed class DeviceTokenRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public Task<int> UpsertAsync(
        Guid tenantId, Guid userId, string expoPushToken, string platform, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            """
            INSERT INTO "dbo"."ParentDevices" ("TenantId", "UserId", "ExpoPushToken", "Platform")
            VALUES (@tenantId, @userId, @token, @platform)
            ON CONFLICT ("TenantId", "ExpoPushToken")
            DO UPDATE SET "UserId" = EXCLUDED."UserId", "Platform" = EXCLUDED."Platform", "UpdatedAt" = now()
            """,
            new { tenantId, userId, token = expoPushToken, platform }, ct);

    public Task<IReadOnlyList<string>> ListTokensForUserAsync(
        Guid tenantId, Guid userId, CancellationToken ct = default) =>
        QueryInlineAsync<string>(
            """SELECT "ExpoPushToken" FROM "dbo"."ParentDevices" WHERE "TenantId" = @tenantId AND "UserId" = @userId""",
            new { tenantId, userId }, ct);
}
```

- [ ] **Step 4: Register the repository**

In `src/Sms.Modules.Comms/CommsModule.cs`, inside `AddCommsModule`, after `services.AddScoped<UserAppSettingsRepository>();`:
```csharp
        services.AddScoped<DeviceTokenRepository>();
```

- [ ] **Step 5: Run test to verify it passes**

Run: `SMS_TEST_PG_PASSWORD=12345678 dotnet test tests/Sms.Tests.Integration --filter "FullyQualifiedName~DeviceTokenRepositoryTests"`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Sms.Modules.Comms/DeviceTokenRepository.cs src/Sms.Modules.Comms/CommsModule.cs tests/Sms.Tests.Integration/Comms/DeviceTokenRepositoryTests.cs
git commit -m "feat(push): DeviceTokenRepository (upsert + per-user token lookup)"
```

---

### Task 3: `IExpoPushSender` + options + DI

**Files:**
- Create: `src/Sms.Shared.Kernel/Push/ExpoPushOptions.cs`
- Create: `src/Sms.Shared.Kernel/Push/IExpoPushSender.cs`
- Create: `src/Sms.Shared.Kernel/Push/ExpoPushSender.cs`
- Modify: `src/Sms.Api/Extensions/ServiceCollectionExtensions.cs` (DI, beside the `google-routes` block)
- Test: `tests/Sms.Tests.Unit/Push/ExpoPushSenderTests.cs`

**Interfaces:**
- Consumes: `IHttpClientFactory` (named client `"expo"`), `IOptions<ExpoPushOptions>`, `ILogger<ExpoPushSender>`. Reuses test doubles `StubHttpMessageHandler` and `SingleClientHttpClientFactory` from `Sms.Tests.Unit.Routing` (same test assembly).
- Produces: `IExpoPushSender.SendAsync(IReadOnlyList<string> expoPushTokens, string title, string body, IReadOnlyDictionary<string, object?>? data = null, CancellationToken ct = default) : Task` — best-effort, never throws.

- [ ] **Step 1: Write the failing test**

`tests/Sms.Tests.Unit/Push/ExpoPushSenderTests.cs`:
```csharp
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sms.Shared.Kernel.Push;
using Sms.Tests.Unit.Routing; // reuse StubHttpMessageHandler + SingleClientHttpClientFactory
using Xunit;

namespace Sms.Tests.Unit.Push;

public class ExpoPushSenderTests
{
    private static ExpoPushSender Sender(StubHttpMessageHandler handler, ExpoPushOptions? opts = null) =>
        new(new SingleClientHttpClientFactory("expo", new HttpClient(handler)),
            Options.Create(opts ?? new ExpoPushOptions()), NullLogger<ExpoPushSender>.Instance);

    [Fact]
    public async Task Posts_one_message_to_the_expo_endpoint_with_tokens_title_body()
    {
        string? path = null, body = null;
        var handler = new StubHttpMessageHandler((req, b) =>
        {
            path = req.RequestUri!.AbsolutePath; body = b;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        await Sender(handler).SendAsync(["ExponentPushToken[a]"], "Bus near stop", "Bus #7 is close.");

        path.Should().Be("/--/api/v2/push/send");
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("to").EnumerateArray().Should().ContainSingle();
        doc.RootElement.GetProperty("title").GetString().Should().Be("Bus near stop");
        doc.RootElement.GetProperty("body").GetString().Should().Be("Bus #7 is close.");
    }

    [Fact]
    public async Task Chunks_more_than_the_batch_size_into_multiple_requests()
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        var tokens = Enumerable.Range(0, 150).Select(i => $"ExponentPushToken[{i}]").ToList();

        await Sender(handler, new ExpoPushOptions { MaxBatchSize = 100 }).SendAsync(tokens, "t", "b");

        requestCount.Should().Be(2); // 100 + 50
    }

    [Fact]
    public async Task A_non_success_response_does_not_throw()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") });

        var act = () => Sender(handler).SendAsync(["ExponentPushToken[a]"], "t", "b");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_transport_exception_does_not_throw()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("network down"));

        var act = () => Sender(handler).SendAsync(["ExponentPushToken[a]"], "t", "b");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task An_empty_token_list_makes_no_http_call()
    {
        var called = false;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            called = true;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        await Sender(handler).SendAsync([], "t", "b");

        called.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Sms.Tests.Unit --filter "FullyQualifiedName~ExpoPushSenderTests"`
Expected: FAIL to build — `ExpoPushSender`/`ExpoPushOptions` do not exist.

- [ ] **Step 3: Write options, interface, and sender**

`src/Sms.Shared.Kernel/Push/ExpoPushOptions.cs`:
```csharp
namespace Sms.Shared.Kernel.Push;

public sealed class ExpoPushOptions
{
    public const string SectionName = "Expo";
    public string BaseUrl { get; set; } = "https://exp.host";
    public string? AccessToken { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
    public int MaxBatchSize { get; set; } = 100;
}
```

`src/Sms.Shared.Kernel/Push/IExpoPushSender.cs`:
```csharp
namespace Sms.Shared.Kernel.Push;

public interface IExpoPushSender
{
    /// Best-effort: batches tokens and never throws for provider failures.
    Task SendAsync(IReadOnlyList<string> expoPushTokens, string title, string body,
        IReadOnlyDictionary<string, object?>? data = null, CancellationToken ct = default);
}
```

`src/Sms.Shared.Kernel/Push/ExpoPushSender.cs`:
```csharp
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sms.Shared.Kernel.Push;

/// Server-side Expo push client (https://docs.expo.dev/push-notifications/sending-notifications/).
/// Batches tokens (<=MaxBatchSize per request, Expo's limit is 100) and NEVER throws for provider
/// failures — a dead token or Expo outage must not break the in-app notification flow.
public sealed class ExpoPushSender(
    IHttpClientFactory httpClientFactory, IOptions<ExpoPushOptions> options, ILogger<ExpoPushSender> log)
    : IExpoPushSender
{
    public async Task SendAsync(IReadOnlyList<string> expoPushTokens, string title, string body,
        IReadOnlyDictionary<string, object?>? data = null, CancellationToken ct = default)
    {
        if (expoPushTokens.Count == 0) return;
        var opts = options.Value;
        var batchSize = opts.MaxBatchSize > 0 ? opts.MaxBatchSize : 100;

        for (var i = 0; i < expoPushTokens.Count; i += batchSize)
        {
            var batch = expoPushTokens.Skip(i).Take(batchSize).ToList();
            await SendBatchAsync(batch, title, body, data, opts, ct);
        }
    }

    async Task SendBatchAsync(IReadOnlyList<string> tokens, string title, string body,
        IReadOnlyDictionary<string, object?>? data, ExpoPushOptions opts, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient("expo");
            var payload = new { to = tokens, title, body, data, sound = "default" };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{opts.BaseUrl}/--/api/v2/push/send");
            req.Headers.Add("Accept", "application/json");
            if (!string.IsNullOrWhiteSpace(opts.AccessToken))
                req.Headers.Add("Authorization", $"Bearer {opts.AccessToken}");
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            // Per-call timeout via a linked CTS rather than mutating a shared named client's Timeout.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(opts.TimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            using var res = await client.SendAsync(req, linkedCts.Token);
            if (!res.IsSuccessStatusCode)
            {
                var respBody = await res.Content.ReadAsStringAsync(ct);
                log.LogWarning("Expo push failed: {Status} {Body}", (int)res.StatusCode, respBody);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning(ex, "Expo push call threw");
        }
    }
}
```

- [ ] **Step 4: Register in DI**

In `src/Sms.Api/Extensions/ServiceCollectionExtensions.cs`, immediately after the three `google-routes` lines (~l.181), add (with `using Sms.Shared.Kernel.Push;` at the top if not present):
```csharp
        builder.Services.Configure<ExpoPushOptions>(builder.Configuration.GetSection(ExpoPushOptions.SectionName));
        builder.Services.AddHttpClient("expo");
        builder.Services.AddSingleton<IExpoPushSender, ExpoPushSender>();
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Sms.Tests.Unit --filter "FullyQualifiedName~ExpoPushSenderTests"`
Expected: PASS (5 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Sms.Shared.Kernel/Push/ src/Sms.Api/Extensions/ServiceCollectionExtensions.cs tests/Sms.Tests.Unit/Push/ExpoPushSenderTests.cs
git commit -m "feat(push): best-effort ExpoPushSender typed HTTP client + DI"
```

---

### Task 4: `POST /v1/me/devices` (service + controller)

**Files:**
- Create: `src/Sms.Application/Services/Devices/DeviceService.cs` (DTO + `IDeviceService` + `DeviceService`)
- Create: `src/Sms.Api/Controllers/DevicesController.cs`
- Modify: `src/Sms.Application/DependencyInjection.cs` (register `IDeviceService`)
- Test: `tests/Sms.Tests.Integration/Comms/DeviceRegistrationTests.cs`

**Interfaces:**
- Consumes: `DeviceTokenRepository.UpsertAsync` (Task 2), `ITenantContext` (`TenantId`,`UserId`), `ApiResult`, `ApiControllerBase.FromResult(ApiResult)`.
- Produces:
  - `RegisterDeviceRequest(string? ExpoPushToken, string? Platform)` (JSON `expo_push_token`, `platform`)
  - `IDeviceService.RegisterAsync(RegisterDeviceRequest req, CancellationToken ct = default) : Task<ApiResult>`
  - Route `POST /v1/me/devices` → `200` on success, `422 invalid_token`/`invalid_platform`, `403` no context, `401` unauthenticated.

- [ ] **Step 1: Write the failing test**

`tests/Sms.Tests.Integration/Comms/DeviceRegistrationTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Comms;

[Collection("sql")]
public class DeviceRegistrationTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient Client(WebApplicationFactory<Program> app, Guid tenantId, Guid userId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, ["driver"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private async Task<int> RowCount(Guid tenantId, Guid userId, string pushToken)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM \"dbo\".\"ParentDevices\" WHERE \"UserId\" = @u AND \"ExpoPushToken\" = @k",
            new { u = userId, k = pushToken });
    }

    [Fact]
    public async Task Registering_a_device_returns_200_and_stores_one_row()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var pushToken = $"ExponentPushToken[{Guid.NewGuid():N}]";

        var res = await Client(app, tenantId, userId).PostAsJsonAsync("/v1/me/devices",
            new { expo_push_token = pushToken, platform = "ios" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RowCount(tenantId, userId, pushToken)).Should().Be(1);
    }

    [Fact]
    public async Task Re_registering_the_same_token_is_idempotent()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var pushToken = $"ExponentPushToken[{Guid.NewGuid():N}]";
        var client = Client(app, tenantId, userId);

        await client.PostAsJsonAsync("/v1/me/devices", new { expo_push_token = pushToken, platform = "ios" });
        var second = await client.PostAsJsonAsync("/v1/me/devices", new { expo_push_token = pushToken, platform = "android" });

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RowCount(tenantId, userId, pushToken)).Should().Be(1, "upsert must not create a duplicate");
    }

    [Fact]
    public async Task Rejects_an_unsupported_platform_and_a_blank_token()
    {
        await using var app = App();
        var client = Client(app, Guid.NewGuid(), Guid.NewGuid());

        var badPlatform = await client.PostAsJsonAsync("/v1/me/devices",
            new { expo_push_token = "ExponentPushToken[x]", platform = "web" });
        badPlatform.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var blankToken = await client.PostAsJsonAsync("/v1/me/devices",
            new { expo_push_token = "", platform = "ios" });
        blankToken.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Unauthenticated_request_is_401()
    {
        await using var app = App();
        var res = await app.CreateClient().PostAsJsonAsync("/v1/me/devices",
            new { expo_push_token = "ExponentPushToken[x]", platform = "ios" });
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `SMS_TEST_PG_PASSWORD=12345678 dotnet test tests/Sms.Tests.Integration --filter "FullyQualifiedName~DeviceRegistrationTests"`
Expected: FAIL — `/v1/me/devices` returns `404` (no controller) / build fails if DTO referenced.

- [ ] **Step 3: Write the service**

`src/Sms.Application/Services/Devices/DeviceService.cs`:
```csharp
using Sms.Application.Common;
using Sms.Modules.Comms;
using Sms.Shared.Kernel.Results;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Application.Services.Devices;

public sealed record RegisterDeviceRequest(string? ExpoPushToken, string? Platform);

public interface IDeviceService
{
    Task<ApiResult> RegisterAsync(RegisterDeviceRequest req, CancellationToken ct = default);
}

/// Idempotent upsert of the current app user's Expo push token. User/tenant come from the
/// authenticated context, never from the request body.
public sealed class DeviceService(DeviceTokenRepository devices, ITenantContext tenant) : IDeviceService
{
    private static readonly HashSet<string> Platforms = ["ios", "android"];

    public async Task<ApiResult> RegisterAsync(RegisterDeviceRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid || tenant.UserId is not { } uid)
            return ApiResult.Fail(new Error("forbidden", "no tenant/user context"), 403);

        var token = req.ExpoPushToken?.Trim() ?? "";
        if (token.Length == 0)
            return ApiResult.Fail(new Error("invalid_token", "expo_push_token is required"), 422);
        var platform = req.Platform?.Trim().ToLowerInvariant() ?? "";
        if (!Platforms.Contains(platform))
            return ApiResult.Fail(new Error("invalid_platform", "platform must be ios or android"), 422);

        await devices.UpsertAsync(tid, uid, token, platform, ct);
        return ApiResult.Ok();
    }
}
```

- [ ] **Step 4: Write the controller**

`src/Sms.Api/Controllers/DevicesController.cs`:
```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sms.Application.Services.Devices;

namespace Sms.Api.Controllers;

/// Device push-token registration for the current app user.
[Route("v1/me")]
[Authorize]
public sealed class DevicesController(IDeviceService devices) : ApiControllerBase
{
    [HttpPost("devices")]
    public async Task<IActionResult> Register([FromBody] RegisterDeviceRequest req, CancellationToken ct) =>
        FromResult(await devices.RegisterAsync(req, ct));
}
```

- [ ] **Step 5: Register the service**

In `src/Sms.Application/DependencyInjection.cs`, alongside the other `AddScoped<IXxxService, XxxService>()` lines (with `using Sms.Application.Services.Devices;` if needed):
```csharp
        services.AddScoped<IDeviceService, DeviceService>();
```

- [ ] **Step 6: Run test to verify it passes**

Run: `SMS_TEST_PG_PASSWORD=12345678 dotnet test tests/Sms.Tests.Integration --filter "FullyQualifiedName~DeviceRegistrationTests"`
Expected: PASS (4 tests).

- [ ] **Step 7: Commit**

```bash
git add src/Sms.Application/Services/Devices/DeviceService.cs src/Sms.Api/Controllers/DevicesController.cs src/Sms.Application/DependencyInjection.cs tests/Sms.Tests.Integration/Comms/DeviceRegistrationTests.cs
git commit -m "feat(push): POST /v1/me/devices idempotent device registration"
```

---

### Task 5: Fire push from `BusParentAlertService`

**Files:**
- Modify: `src/Sms.Application/Services/Transport/BusParentAlertService.cs` (ctor deps + push call in the fan-out loop)
- Test: `tests/Sms.Tests.Integration/Transport/BusParentPushFanoutTests.cs`

**Interfaces:**
- Consumes: `IExpoPushSender.SendAsync` (Task 3), `DeviceTokenRepository.ListTokensForUserAsync` (Task 2). Reuses the `BusParentApproachAlertTests` seeding style (bus/route/rider/parent + driver) from Increment #1.
- Produces: no new public API — push is fired for each `parentId` right after `comms.CreateNotificationAsync`, for both alert kinds.

- [ ] **Step 1: Write the failing test**

`tests/Sms.Tests.Integration/Transport/BusParentPushFanoutTests.cs`:
```csharp
using System.Net.Http.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Push;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class BusParentPushFanoutTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";
    private const double StopLat = 12.9000, StopLng = 77.6000;

    private sealed class SpyExpoPushSender : IExpoPushSender
    {
        public List<(IReadOnlyList<string> Tokens, string Title, string Body)> Calls { get; } = [];
        public bool Throw { get; set; }
        public Task SendAsync(IReadOnlyList<string> tokens, string title, string body,
            IReadOnlyDictionary<string, object?>? data = null, CancellationToken ct = default)
        {
            Calls.Add((tokens, title, body));
            if (Throw) throw new InvalidOperationException("expo down");
            return Task.CompletedTask;
        }
    }

    private (WebApplicationFactory<Program> App, SpyExpoPushSender Push) App()
    {
        var push = new SpyExpoPushSender();
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
            b.ConfigureTestServices(s => s.AddSingleton<IExpoPushSender>(push));
        });
        return (app, push);
    }

    private static HttpClient Driver(WebApplicationFactory<Program> app, Guid tenantId, Guid userId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, ["driver"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    // Seeds a bus on a one-stop route with one rider + parent at that stop, plus (optionally) a
    // registered device token for that parent. Returns the busNo, routeId and parentId.
    private async Task<(string BusNo, Guid RouteId, Guid ParentId)> Seed(Guid tenantId, bool withDevice)
    {
        var busNo = $"KA-{Guid.NewGuid():N}"[..12];
        var busId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var stopId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var adm = $"ADM-{Guid.NewGuid():N}"[..14];

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"Buses\" (\"Id\",\"TenantId\",\"BusNo\",\"RouteId\",\"RouteName\") VALUES (@Id,@T,@B,@R,'North')",
            new { Id = busId, T = tenantId, B = busNo, R = routeId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"TransportRoutes\" (\"Id\",\"TenantId\",\"Name\") VALUES (@R,@T,'North')",
            new { R = routeId, T = tenantId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"RouteStops\" (\"Id\",\"TenantId\",\"RouteId\",\"Name\",\"Seq\",\"Lat\",\"Lng\") VALUES (@Id,@T,@R,'Gate A',1,@La,@Ln)",
            new { Id = stopId, T = tenantId, R = routeId, La = StopLat, Ln = StopLng });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"Students\" (\"Id\",\"TenantId\",\"AdmissionNo\",\"Name\",\"GuardianPhone\") VALUES (@Id,@T,@A,'Asha','9876543210')",
            new { Id = studentId, T = tenantId, A = adm });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"StudentBusAssignments\" (\"Id\",\"TenantId\",\"StudentId\",\"BusId\",\"RouteId\",\"StopId\") VALUES (@Id,@T,@S,@B,@R,@Stop)",
            new { Id = Guid.NewGuid(), T = tenantId, S = studentId, B = busId, R = routeId, Stop = stopId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"Users\" (\"Id\",\"TenantId\",\"StudentId\",\"IsPlatform\",\"Status\") VALUES (@Id,@T,@Adm,false,'active')",
            new { Id = parentId, T = tenantId, Adm = adm });
        if (withDevice)
            await conn.ExecuteAsync("INSERT INTO \"dbo\".\"ParentDevices\" (\"TenantId\",\"UserId\",\"ExpoPushToken\",\"Platform\") VALUES (@T,@U,@K,'ios')",
                new { T = tenantId, U = parentId, K = $"ExponentPushToken[{Guid.NewGuid():N}]" });
        return (busNo, routeId, parentId);
    }

    private static async Task<Guid> StartTrip(HttpClient driver, string busNo, Guid routeId)
    {
        var start = await driver.PostAsJsonAsync("/v1/staff/trips", new { direction = "pickup", bus_no = busNo, route_id = routeId });
        start.EnsureSuccessStatusCode();
        return (await start.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("data").GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> Ping(HttpClient driver, Guid tripId) =>
        driver.PostAsJsonAsync($"/v1/staff/trips/{tripId}/pings", new
        {
            pings = new[] { new { lat = StopLat, lng = StopLng, speed_kmh = 20, heading = 10, at = DateTime.UtcNow } },
        });

    [Fact]
    public async Task A_registered_parent_gets_a_push_on_trip_start_and_on_approach()
    {
        var (app, push) = App();
        await using var _ = app;
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var (busNo, routeId, _) = await Seed(tenantId, withDevice: true);
        await TripTestSeed.AssignDriverAsync(fx.ConnectionString, tenantId, busNo, driverId);
        var driver = Driver(app, tenantId, driverId);

        var tripId = await StartTrip(driver, busNo, routeId); // fires trip-started push
        push.Calls.Should().Contain(c => c.Title == "Bus started");

        (await Ping(driver, tripId)).EnsureSuccessStatusCode(); // fires approaching push
        push.Calls.Should().Contain(c => c.Title == "Bus near stop");
        push.Calls.Where(c => c.Title == "Bus near stop").Should().OnlyContain(c => c.Tokens.Count == 1);
    }

    [Fact]
    public async Task A_parent_with_no_registered_device_triggers_no_push()
    {
        var (app, push) = App();
        await using var _ = app;
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var (busNo, routeId, _) = await Seed(tenantId, withDevice: false);
        await TripTestSeed.AssignDriverAsync(fx.ConnectionString, tenantId, busNo, driverId);
        var driver = Driver(app, tenantId, driverId);

        var tripId = await StartTrip(driver, busNo, routeId);
        (await Ping(driver, tripId)).EnsureSuccessStatusCode();

        push.Calls.Should().BeEmpty("no device token means no push");
    }

    [Fact]
    public async Task A_failing_push_does_not_break_ingest_or_the_in_app_notice()
    {
        var (app, push) = App();
        push.Throw = true;
        await using var _ = app;
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var (busNo, routeId, parentId) = await Seed(tenantId, withDevice: true);
        await TripTestSeed.AssignDriverAsync(fx.ConnectionString, tenantId, busNo, driverId);
        var driver = Driver(app, tenantId, driverId);

        var tripId = await StartTrip(driver, busNo, routeId);
        (await Ping(driver, tripId)).StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent);

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        var notices = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM \"dbo\".\"Notifications\" WHERE \"UserId\" = @u AND \"Icon\" = 'bus'", new { u = parentId });
        notices.Should().BeGreaterThan(0, "the in-app notice must persist even when push throws");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `SMS_TEST_PG_PASSWORD=12345678 dotnet test tests/Sms.Tests.Integration --filter "FullyQualifiedName~BusParentPushFanoutTests"`
Expected: FAIL — `push.Calls` stays empty (the service does not send push yet). (The `withDevice:false` test may pass vacuously; the other two fail.)

- [ ] **Step 3: Add the dependencies and the push call**

In `src/Sms.Application/Services/Transport/BusParentAlertService.cs`, add `using Sms.Shared.Kernel.Push;` and extend the primary ctor:
```csharp
public sealed class BusParentAlertService(
    StudentBusRepository riders,
    CommsRepository comms,
    DeviceTokenRepository devices,
    IExpoPushSender push,
    ILiveBroadcaster live,
    ILogger<BusParentAlertService> logger) : IBusParentAlertService
```
Then in `NotifyAsync`, inside the `foreach (var parentId in parents)` loop, **after** the `comms.CreateNotificationAsync(...)` line and before `sent = true;`:
```csharp
                    var tokens = await devices.ListTokensForUserAsync(tenantId, parentId, ct);
                    if (tokens.Count > 0)
                        await push.SendAsync(tokens, title, body,
                            new Dictionary<string, object?> { ["kind"] = kind, ["trip_id"] = tripId }, ct);
```
(`DeviceTokenRepository` lives in `Sms.Modules.Comms`, already imported via `using Sms.Modules.Comms;` at the top of the file.)

- [ ] **Step 4: Run test to verify it passes**

Run: `SMS_TEST_PG_PASSWORD=12345678 dotnet test tests/Sms.Tests.Integration --filter "FullyQualifiedName~BusParentPushFanoutTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Run the full regression suites**

Run: `dotnet test tests/Sms.Tests.Unit` then `SMS_TEST_PG_PASSWORD=12345678 dotnet test tests/Sms.Tests.Integration --filter "FullyQualifiedName~Transport|FullyQualifiedName~Comms"`
Expected: all green (the Increment #1 Transport tests and BusNotify still pass with the new ctor deps).

- [ ] **Step 6: Commit**

```bash
git add src/Sms.Application/Services/Transport/BusParentAlertService.cs tests/Sms.Tests.Integration/Transport/BusParentPushFanoutTests.cs
git commit -m "feat(push): fire best-effort Expo push from parent bus alerts (both kinds)"
```

---

## Notes for the executor
- If the `using Sms.Tests.Unit.Routing;` import in Task 3 does not resolve `StubHttpMessageHandler`/`SingleClientHttpClientFactory` (e.g. they were made `file`-scoped later), copy those two small fake classes into the `Push` test namespace instead — do not weaken the originals.
- `BusParentAlertService`'s ctor gains two parameters; the DI registration (`AddScoped<IBusParentAlertService, BusParentAlertService>()`) needs no change (all deps are container-resolved), but any hand-constructed instance in a test would. Grep `new BusParentAlertService(` before finishing Task 5 — there should be none.
- Do not add a `web` platform, a `DELETE` endpoint, receipt polling, or a preference toggle — all explicitly deferred.
