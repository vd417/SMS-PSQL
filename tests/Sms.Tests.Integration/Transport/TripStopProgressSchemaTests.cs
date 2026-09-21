using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Dapper;
using Xunit;
using FluentAssertions;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class TripStopProgressSchemaTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    [Fact]
    public async Task Migration_creates_TripStopProgress_and_new_columns()
    {
        await using var app = App(); // forces migrations to have run via PostgresFixture.InitializeAsync
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();

        var tripStopProgressExists = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.tables WHERE name = 'TripStopProgress'");
        tripStopProgressExists.Should().Be(1);

        var currentStopIdExists = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Trips') AND name = 'CurrentStopId'");
        currentStopIdExists.Should().Be(1);

        var accuracyExists = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TripPings') AND name = 'Accuracy'");
        accuracyExists.Should().Be(1);
    }

    [Fact]
    public async Task ConfirmArrival_and_Complete_procs_round_trip()
    {
        var tenantId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var stopId = Guid.NewGuid();

        await using var app = App();
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        await conn.ExecuteAsync("INSERT INTO dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, 'BUS-1')",
            new { Id = busId, TenantId = tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO dbo.Trips (Id, TenantId, BusId, Direction, Status, StartedAt) VALUES (@Id, @TenantId, @BusId, 'pickup', 'live', SYSUTCDATETIME())",
            new { Id = tripId, TenantId = tenantId, BusId = busId });

        await conn.ExecuteAsync("dbo.TripStopProgress_ConfirmArrival",
            new { TenantId = tenantId, TripId = tripId, StopId = stopId, Seq = 1, ArrivedAt = DateTime.UtcNow, ConfirmedAt = DateTime.UtcNow },
            commandType: System.Data.CommandType.StoredProcedure);

        var currentStopId = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT CurrentStopId FROM dbo.Trips WHERE Id = @tripId", new { tripId });
        currentStopId.Should().Be(stopId);

        await conn.ExecuteAsync("dbo.TripStopProgress_Complete",
            new { TenantId = tenantId, TripId = tripId, StopId = stopId, DepartedAt = DateTime.UtcNow },
            commandType: System.Data.CommandType.StoredProcedure);

        var afterComplete = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT CurrentStopId FROM dbo.Trips WHERE Id = @tripId", new { tripId });
        afterComplete.Should().BeNull();

        var departedAt = await conn.ExecuteScalarAsync<DateTime?>(
            "SELECT DepartedAt FROM dbo.TripStopProgress WHERE TripId = @tripId AND StopId = @stopId", new { tripId, stopId });
        departedAt.Should().NotBeNull();
    }
}
