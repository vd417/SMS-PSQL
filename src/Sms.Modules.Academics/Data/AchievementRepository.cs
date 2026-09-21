using Sms.Modules.Academics.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Academics.Data;

public sealed class AchievementRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string Cols = "\"Id\", \"TenantId\", \"StudentId\", \"Title\", \"AwardedOn\", \"Icon\", \"Hue\"";

    public Task<IReadOnlyList<AchievementAwardRow>> ListAsync(Guid studentId, CancellationToken ct = default) =>
        QueryInlineAsync<AchievementAwardRow>(
            $"SELECT {Cols} FROM \"dbo\".\"Achievements\" WHERE \"StudentId\" = @studentId ORDER BY \"AwardedOn\" DESC, \"CreatedAt\" DESC",
            new { studentId }, ct);

    public async Task<AchievementAwardRow?> CreateAsync(
        Guid tenantId, CreateAchievementRequest r, DateTime awardedOn, string icon, string hue,
        CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await ExecuteInlineAsync(@"
INSERT INTO ""dbo"".""Achievements"" (""Id"", ""TenantId"", ""StudentId"", ""Title"", ""AwardedOn"", ""Icon"", ""Hue"", ""CreatedAt"")
VALUES (@id, @tenantId, @studentId, @title, @awardedOn, @icon, @hue, now())",
            new
            {
                id,
                tenantId,
                studentId = r.StudentId,
                title = r.Title.Trim(),
                awardedOn,
                icon,
                hue,
            }, ct);

        return (await QueryInlineAsync<AchievementAwardRow>(
            $"SELECT {Cols} FROM \"dbo\".\"Achievements\" WHERE \"Id\" = @id", new { id }, ct)).FirstOrDefault();
    }
}
