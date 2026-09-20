using FluentAssertions;
using Sms.Modules.AiSearch.Data;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.AiSearch;

[Collection("sql")]
public class AiSearchLogRepositoryTests(PostgresFixture fx)
{
    [Fact]
    public async Task InsertAsync_persists_a_row()
    {
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        var repo = new AiSearchLogRepository(factory);
        var entry = new AiSearchLogEntry(
            Guid.NewGuid(), Guid.NewGuid(), "school.admin", "Aaj kitne bachche aaye?",
            "hinglish", "DailyAttendanceSummary", 1, true, DateTime.UtcNow);

        var rows = await repo.InsertAsync(entry);

        rows.Should().Be(1);
    }
}
