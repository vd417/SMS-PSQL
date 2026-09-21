using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Services.AiSearch;
using Sms.Application.Services.AiSearch.Handlers;
using Sms.Modules.Staffing.Contracts;
using Sms.Modules.Staffing.Data;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.AiSearch;

/// Exercises TeacherSearchHandler directly (no HTTP layer needed yet — the AI search controller
/// lands in a later task). TeacherRepository.ListAsync is DB-session-scoped by the SQL Server RLS
/// tenant filter (no explicit tenantId parameter), so the whole point of this test is proving
/// cross-tenant isolation actually holds end to end, exactly like StudentSearchHandlerTests'
/// admin-search case.
[Collection("sql")]
public class TeacherSearchHandlerTests(PostgresFixture fx)
{
    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", "integration-test-signing-key-32-bytes-min!!");
        });

    private static async Task<AiSearchResponse> Handle(
        WebApplicationFactory<Program> app, Guid tenantId, AiAuthorizationResult auth, string language = "en")
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, Guid.NewGuid(), isPlatform: false);
        var handler = new TeacherSearchHandler(
            scope.ServiceProvider.GetRequiredService<TeacherRepository>(),
            scope.ServiceProvider.GetRequiredService<IAiAnswerTemplateService>());
        return await handler.HandleAsync(auth, language, 1, 20);
    }

    private async Task Seed(Func<NpgsqlConnection, Task> work)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=1");
        await work(conn);
    }

    private static async Task InsertTeacher(NpgsqlConnection conn, Guid id, Guid tenantId, string name) =>
        await conn.ExecuteAsync(
            "INSERT dbo.Teachers (Id, TenantId, Name) VALUES (@id, @tenantId, @name)",
            new { id, tenantId, name });

    private static AiAuthorizationResult AdminAuth(string? name) => new(
        Allowed: true, ResultIntent: "TeacherSearch", ResolvedStudentId: null,
        AllowedChildStudentIds: null, AllowedClassNames: null,
        ClampedFilters: new AiSearchFilters(name, null, null, null, false),
        Unrestricted: true, NameUnmatched: false);

    [Fact]
    public async Task Search_returns_only_the_callers_tenant_teachers()
    {
        await using var app = App();
        var tenantIdA = Guid.NewGuid();
        var tenantIdB = Guid.NewGuid();

        await Seed(async conn =>
        {
            await InsertTeacher(conn, Guid.NewGuid(), tenantIdA, "Meena Rao");
            await InsertTeacher(conn, Guid.NewGuid(), tenantIdB, "Meena Rao");
        });

        var response = await Handle(app, tenantIdA, AdminAuth("Meena"));

        response.Intent.Should().Be("TeacherSearch");
        response.Data.Should().NotBeNull();

        var rows = (IReadOnlyList<TeacherResponse>)response.Data!;
        rows.Should().ContainSingle();
        rows[0].TenantId.Should().Be(tenantIdA);
        rows[0].Name.Should().Be("Meena Rao");
    }

    [Fact]
    public async Task Search_with_no_matches_returns_no_match_answer_not_null_data_crash()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();

        var response = await Handle(app, tenantId, AdminAuth("NoSuchTeacherXyz"));

        response.Intent.Should().Be("TeacherSearch");
        response.Data.Should().NotBeNull();
        var rows = (IReadOnlyList<TeacherResponse>)response.Data!;
        rows.Should().BeEmpty();

        var templates = app.Services.GetRequiredService<IAiAnswerTemplateService>();
        response.Answer.Should().Be(templates.RenderNoMatch("en"));
    }
}
