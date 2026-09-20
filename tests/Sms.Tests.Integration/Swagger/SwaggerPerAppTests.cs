using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Sms.Api.Swagger;
using Swashbuckle.AspNetCore.Swagger;

namespace Sms.Tests.Integration.Swagger;

[Collection("sql")]
public class SwaggerPerAppTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static OpenApiDocument Doc(WebApplicationFactory<Program> app, string name)
    {
        using var scope = app.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ISwaggerProvider>().GetSwagger(name);
    }

    [Fact]
    public void Five_app_documents_are_registered()
    {
        ApiAudienceMap.Apps.Select(a => a.Key).Should()
            .BeEquivalentTo(new[] { "catre-admin", "school-admin", "teacher", "student", "staff" });
    }

    [Fact]
    public void Catre_admin_doc_has_clients_but_not_staff_trips()
    {
        using var app = App();
        var paths = Doc(app, "catre-admin").Paths.Keys;
        paths.Should().Contain("/v1/clients");
        paths.Should().NotContain("/v1/staff/trips");
    }

    [Fact]
    public void Staff_doc_has_trips_but_not_clients()
    {
        using var app = App();
        var paths = Doc(app, "staff").Paths.Keys;
        paths.Should().Contain("/v1/staff/trips");
        paths.Should().NotContain("/v1/clients");
    }

    [Fact]
    public void Staff_hr_records_belong_to_school_admin_not_staff_app()
    {
        using var app = App();
        // /v1/staff (HR list) is School Admin; /v1/staff/trips (transport) is the Staff app.
        Doc(app, "school-admin").Paths.Keys.Should().Contain("/v1/staff");
        Doc(app, "staff").Paths.Keys.Should().NotContain("/v1/staff");
    }

    [Fact]
    public void Shared_threads_endpoint_appears_in_each_consuming_app()
    {
        using var app = App();
        Doc(app, "teacher").Paths.Keys.Should().Contain("/v1/threads");
        Doc(app, "student").Paths.Keys.Should().Contain("/v1/threads");
        Doc(app, "school-admin").Paths.Keys.Should().Contain("/v1/threads");
    }

    [Fact]
    public void Auth_appears_in_every_app_document()
    {
        using var app = App();
        foreach (var (key, _) in ApiAudienceMap.Apps)
            Doc(app, key).Paths.Keys.Should().Contain("/v1/auth/login", $"every app logs in ({key})");
    }

    [Fact]
    public void Teacher_doc_includes_approvals_principal_and_dashboard_stats()
    {
        using var app = App();
        var paths = Doc(app, "teacher").Paths.Keys;
        paths.Should().Contain("/v1/approvals",                    "teacher sees their own leave approvals");
        paths.Should().Contain("/v1/principal/overview",           "principal overview is a teacher-app screen");
        paths.Should().Contain("/v1/principal/attendance",         "principal attendance is a teacher-app screen");
        paths.Should().Contain("/v1/classes/{classId}/students",   "class student list is a core teacher screen");
        paths.Should().Contain("/v1/dashboard/stats",              "dashboard stats screen is teacher-app only");
    }

    [Fact]
    public void Catre_dashboard_overview_still_belongs_to_catre_admin_only()
    {
        using var app = App();
        var catrePaths  = Doc(app, "catre-admin").Paths.Keys;
        var teacherPaths = Doc(app, "teacher").Paths.Keys;
        catrePaths.Should().Contain("/v1/dashboard/overview",  "catre admin owns the Catre dashboard");
        teacherPaths.Should().NotContain("/v1/dashboard/overview", "catre dashboard must not leak to teacher doc");
    }

    [Fact]
    public void Teacher_doc_includes_timetable_calendar_library_and_assignments()
    {
        using var app = App();
        var paths = Doc(app, "teacher").Paths.Keys;
        paths.Should().Contain("/v1/timetable",   "timetable is a teacher-app screen");
        paths.Should().Contain("/v1/calendar",    "calendar is a teacher-app screen");
        paths.Should().Contain("/v1/library",     "library is a teacher-app screen");
        paths.Should().Contain("/v1/assignments", "assignments is a teacher-app screen");
        paths.Should().Contain("/v1/achievements", "teachers award student achievements");
        paths.Should().Contain("/v1/me/settings", "in-app notice preferences");
    }

    [Fact]
    public void School_admin_doc_includes_calendar()
    {
        using var app = App();
        Doc(app, "school-admin").Paths.Keys.Should()
            .Contain("/v1/calendar", "CRM calendar is backed by GET/POST/DELETE /v1/calendar");
    }

    [Fact]
    public void Teacher_doc_includes_bus_assigned_and_roster()
    {
        using var app = App();
        var paths = Doc(app, "teacher").Paths.Keys;
        paths.Should().Contain("/v1/bus/assigned", "teacher views assigned buses");
        paths.Should().Contain("/v1/bus/{busId}/roster", "teacher views bus roster");
    }
}
