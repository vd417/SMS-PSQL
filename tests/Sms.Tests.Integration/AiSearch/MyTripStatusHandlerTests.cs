using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Services.AiSearch;
using Sms.Application.Services.AiSearch.Handlers;
using Sms.Application.Services.Transport;
using Sms.Modules.Transport;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.AiSearch;

/// MyTripStatusHandler is self-scoped exactly like TeacherAttendance/StaffAttendance:
/// ITripService.GetCurrentAsync() resolves the caller's OWN trip internally via ITenantContext,
/// never a request-supplied id. These tests seed a trip through the real ITripService.StartAsync
/// (the same self-scoped path TripOwnershipTests already relies on) rather than inventing new
/// trip-seeding SQL.
[Collection("sql")]
public class MyTripStatusHandlerTests(PostgresFixture fx)
{
    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", "integration-test-signing-key-32-bytes-min!!");
        });

    private static readonly AiAuthorizationResult Auth = new(
        Allowed: true, ResultIntent: "MyTripStatus", ResolvedStudentId: null,
        AllowedChildStudentIds: null, AllowedClassNames: null,
        ClampedFilters: new AiSearchFilters(null, null, null, null, false),
        Unrestricted: false, NameUnmatched: false);

    [Fact]
    public async Task Driver_with_an_active_trip_gets_bus_and_status()
    {
        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>()
            .Set(Guid.NewGuid(), Guid.NewGuid(), isPlatform: false);

        var trips = scope.ServiceProvider.GetRequiredService<ITripService>();
        await trips.StartAsync(new StartTripRequest(null, "BUS-12", "morning"));

        var handler = new MyTripStatusHandler(
            trips, scope.ServiceProvider.GetRequiredService<IAiAnswerTemplateService>());

        var response = await handler.HandleAsync(Auth, "en", 1, 20);

        response.Status.Should().Be("success");
        response.Answer.Should().Contain("BUS-12");
    }

    [Fact]
    public async Task Driver_with_no_active_trip_gets_a_clean_no_active_trip_answer()
    {
        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>()
            .Set(Guid.NewGuid(), Guid.NewGuid(), isPlatform: false);

        var trips = scope.ServiceProvider.GetRequiredService<ITripService>();
        var templates = scope.ServiceProvider.GetRequiredService<IAiAnswerTemplateService>();
        var handler = new MyTripStatusHandler(trips, templates);

        var response = await handler.HandleAsync(Auth, "en", 1, 20);

        response.Status.Should().Be("no_match");
        response.Answer.Should().Be(templates.RenderNoActiveTrip("en"));
    }

    [Fact]
    public async Task A_driver_never_sees_another_drivers_trip()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var driverA = Guid.NewGuid();
        var driverB = Guid.NewGuid();

        using (var scopeA = app.Services.CreateScope())
        {
            scopeA.ServiceProvider.GetRequiredService<ITenantContext>()
                .Set(tenantId, driverA, isPlatform: false);
            var tripsA = scopeA.ServiceProvider.GetRequiredService<ITripService>();
            await tripsA.StartAsync(new StartTripRequest(null, "BUS-99", "afternoon"));
        }

        using var scopeB = app.Services.CreateScope();
        scopeB.ServiceProvider.GetRequiredService<ITenantContext>()
            .Set(tenantId, driverB, isPlatform: false);
        var tripsB = scopeB.ServiceProvider.GetRequiredService<ITripService>();
        var templatesB = scopeB.ServiceProvider.GetRequiredService<IAiAnswerTemplateService>();
        var handler = new MyTripStatusHandler(tripsB, templatesB);

        var response = await handler.HandleAsync(Auth, "en", 1, 20);

        response.Status.Should().Be("no_match");
        response.Answer.Should().Be(templatesB.RenderNoActiveTrip("en"));
    }
}
