using Sms.Modules.Tenancy.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Tenancy.Data;

public sealed class PlanRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string Cols =
        "\"Id\", \"Name\", \"Tier\", \"Pricing\", \"Price\", \"PerStudent\", \"MinStudents\", \"Period\", \"FeaturesCsv\", " +
        "\"LimitsStudents\", \"LimitsStaff\", \"LimitsStorageGb\", \"Visibility\", \"Audience\", \"Band\", \"OfferLabel\", \"OfferPct\", \"Color\", \"Description\"";

    public Task<PlanRow?> UpsertAsync(PlanUpsertRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<PlanRow>("dbo.Plan_Upsert", new
        {
            r.Id, r.Name, Tier = ResolveTier(r.Tier, r.Name), r.Pricing, r.Price, r.PerStudent, r.MinStudents, r.Period,
            FeaturesCsv = r.Features is null || r.Features.Count == 0 ? null : string.Join(',', r.Features),
            LimitsStudents = r.Limits.Students, LimitsStaff = r.Limits.Staff, LimitsStorageGb = r.Limits.StorageGb,
            r.Visibility, r.Audience, r.Band,
            OfferLabel = r.Offer?.Label, OfferPct = r.Offer?.Pct, r.Color, r.Description
        }, ct);

    public async Task<PlanRow?> SetVisibilityAsync(Guid id, string visibility, CancellationToken ct = default) =>
        (await QueryInlineAsync<PlanRow>(
            $"UPDATE \"dbo\".\"Plans\" SET \"Visibility\" = @visibility WHERE \"Id\" = @id; SELECT {Cols} FROM \"dbo\".\"Plans\" WHERE \"Id\" = @id;",
            new { id, visibility }, ct)).FirstOrDefault();

    // The Catre plan editor omits `tier` (it carries `feature_tiers`, which we ignore). Default the
    // label from the name (Tier column is nvarchar(20)) so the single-tier model still saves.
    private static string ResolveTier(string? tier, string name) =>
        string.IsNullOrWhiteSpace(tier)
            ? new string(name.Trim().ToLowerInvariant().Take(20).ToArray())
            : tier.Trim();

    public async Task<PlanRow?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await QueryInlineAsync<PlanRow>($"SELECT {Cols} FROM \"dbo\".\"Plans\" WHERE \"Id\" = @id", new { id }, ct))
        .FirstOrDefault();

    public Task<IReadOnlyList<PlanRow>> ListAsync(
        string? visibility, string? audience, CancellationToken ct = default) =>
        QueryInlineAsync<PlanRow>(
            $"SELECT {Cols} FROM \"dbo\".\"Plans\" WHERE (@visibility IS NULL OR \"Visibility\" = @visibility) " +
            "AND (@audience IS NULL OR \"Audience\" = @audience) ORDER BY \"Price\"",
            new { visibility, audience }, ct);
}
