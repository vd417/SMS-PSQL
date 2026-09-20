using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Services.AiSearch;
using Sms.Application.Services.AiSearch.Handlers;
using Sms.Application.Services.Attendance;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.AiSearch;

/// Regression coverage for b432fe9's fix: TeacherAttendanceHandler and StaffAttendanceHandler both
/// delegate to a shared TeacherAttendanceHandler.GetSummaryAsync helper, but each MUST report its
/// own Intent in the returned AiSearchResponse rather than always "TeacherAttendance". Both handlers
/// call the self-scoped IAttendanceService.GetSummaryAsync(null, null, ct), which is gated behind
/// StaffCheckInAllowed (tenant.IsPlatform || FeatureCatalog.Attendance) — running as a platform
/// session sidesteps the plan-feature gate without needing to seed any CheckIns rows, since an
/// unseeded user simply yields a zero-valued (but successful) summary.
[Collection("sql")]
public class TeacherAttendanceHandlerTests(PostgresFixture fx)
{
    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", "integration-test-signing-key-32-bytes-min!!");
        });

    private static readonly AiAuthorizationResult Auth = new(
        Allowed: true, ResultIntent: "TeacherAttendance", ResolvedStudentId: null,
        AllowedChildStudentIds: null, AllowedClassNames: null,
        ClampedFilters: new AiSearchFilters(null, null, null, null, false),
        Unrestricted: false, NameUnmatched: false);

    [Fact]
    public async Task Teacher_attendance_handler_reports_its_own_intent()
    {
        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>()
            .Set(Guid.NewGuid(), Guid.NewGuid(), isPlatform: true);

        var handler = new TeacherAttendanceHandler(
            scope.ServiceProvider.GetRequiredService<IAttendanceService>(),
            scope.ServiceProvider.GetRequiredService<IAiAnswerTemplateService>());

        var response = await handler.HandleAsync(Auth, "en", 1, 20);

        response.Intent.Should().Be("TeacherAttendance");
        response.Data.Should().NotBeNull();
    }

    /// The regression test: before b432fe9's fix, StaffAttendanceHandler delegated to a brand-new
    /// TeacherAttendanceHandler instance and returned ITS hardcoded "TeacherAttendance" intent. This
    /// must fail on that old code and pass now that the shared helper takes the caller's own intent.
    [Fact]
    public async Task Staff_attendance_handler_reports_its_own_intent_not_teacher()
    {
        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>()
            .Set(Guid.NewGuid(), Guid.NewGuid(), isPlatform: true);

        var handler = new StaffAttendanceHandler(
            scope.ServiceProvider.GetRequiredService<IAttendanceService>(),
            scope.ServiceProvider.GetRequiredService<IAiAnswerTemplateService>());

        var response = await handler.HandleAsync(Auth, "en", 1, 20);

        response.Intent.Should().Be("StaffAttendance");
        response.Data.Should().NotBeNull();
    }
}
