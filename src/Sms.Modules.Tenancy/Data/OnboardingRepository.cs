using Sms.Modules.Tenancy.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Tenancy.Data;

public sealed class OnboardingRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public async Task<Guid> CreateAsync(CreateOnboardingRequest r, CancellationToken ct = default) =>
        await QuerySingleProcAsync<Guid>("dbo.Onboarding_Create",
            new { r.Name, r.Slug, r.Owner, r.Value, r.Stage,
                  r.ContactName, r.ContactEmail, r.ContactPhone, r.Address, r.TenantId }, ct);

    public Task AdvanceAsync(Guid id, string stage, CancellationToken ct = default) =>
        ExecuteProcAsync("dbo.Onboarding_Advance", new { Id = id, Stage = stage }, ct);

    public async Task AdvanceByTenantAsync(Guid tenantId, string stage, CancellationToken ct = default)
    {
        var id = (await QueryInlineAsync<Guid?>(
            """SELECT "Id" FROM "dbo"."OnboardingItems" WHERE "TenantId" = @tenantId ORDER BY "Age" DESC LIMIT 1""",
            new { tenantId }, ct)).FirstOrDefault();
        if (id is Guid oid)
            await AdvanceAsync(oid, stage, ct);
    }

    public Task SetChecklistAsync(Guid id, string label, bool done, CancellationToken ct = default) =>
        ExecuteProcAsync("dbo.Onboarding_SetChecklist", new { Id = id, Label = label, Done = done }, ct);

    public async Task<OnboardingItemResponse?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var item = (await QueryInlineAsync<OnboardingItemRow>(
            "SELECT \"Id\", \"TenantId\", \"Name\", \"Slug\", \"Owner\", \"Value\", \"Stage\", \"Age\", " +
            "\"ContactName\", \"ContactEmail\", \"ContactPhone\", \"Address\" FROM \"dbo\".\"OnboardingItems\" WHERE \"Id\" = @id",
            new { id }, ct)).FirstOrDefault();
        if (item is null) return null;
        var checks = await QueryInlineAsync<ChecklistRow>(
            "SELECT \"OnboardingId\", \"Label\", \"Done\" FROM \"dbo\".\"OnboardingChecklist\" WHERE \"OnboardingId\" = @id ORDER BY \"Seq\"",
            new { id }, ct);
        return Compose(item, checks);
    }

    public async Task<IReadOnlyList<OnboardingItemResponse>> ListAsync(string? stage, CancellationToken ct = default)
    {
        var items = await QueryInlineAsync<OnboardingItemRow>(
            "SELECT \"Id\", \"TenantId\", \"Name\", \"Slug\", \"Owner\", \"Value\", \"Stage\", \"Age\", " +
            "\"ContactName\", \"ContactEmail\", \"ContactPhone\", \"Address\" FROM \"dbo\".\"OnboardingItems\" " +
            "WHERE (@stage IS NULL OR \"Stage\" = @stage) ORDER BY \"Age\" DESC", new { stage }, ct);
        var checks = await QueryInlineAsync<ChecklistRow>(
            "SELECT \"OnboardingId\", \"Label\", \"Done\" FROM \"dbo\".\"OnboardingChecklist\" ORDER BY \"Seq\"", null, ct);
        var byItem = checks.GroupBy(c => c.OnboardingId).ToDictionary(g => g.Key, g => (IReadOnlyList<ChecklistRow>)g.ToList());
        return items.Select(i => Compose(i, byItem.TryGetValue(i.Id, out var cs) ? cs : [])).ToList();
    }

    private static OnboardingItemResponse Compose(OnboardingItemRow i, IReadOnlyList<ChecklistRow> checks)
    {
        var list = checks.Select(c => new OnboardingChecklistItem(c.Label, c.Done)).ToList();
        return new OnboardingItemResponse(i.Id, i.TenantId, i.Name, i.Slug, i.Owner, i.Value, i.Stage,
            list, list.Count(c => c.Done), i.Age,
            i.ContactName, i.ContactEmail, i.ContactPhone, i.Address);
    }
}
