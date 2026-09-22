using Sms.Modules.Sports.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Sports.Data;

public sealed class SportsRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    /// Athletes = sum of team roster sizes; Medals counts only rows for @year (the current year).
    public async Task<SportsSummaryResponse> SummaryAsync(int year, CancellationToken ct = default) =>
        (await QueryInlineAsync<SportsSummaryResponse>(
            @"SELECT
                CAST((SELECT COUNT(*) FROM ""dbo"".""SportsTeams"") AS int) AS ""Teams"",
                CAST((SELECT COUNT(*) FROM ""dbo"".""SportsEvents"") AS int) AS ""Events"",
                CAST(COALESCE((SELECT SUM(""Athletes"") FROM ""dbo"".""SportsTeams""), 0) AS int) AS ""Athletes"",
                CAST((SELECT COUNT(*) FROM ""dbo"".""SportsMedals"" WHERE ""Year"" = @year) AS int) AS ""Medals""",
            new { year }, ct)).First();

    public Task<IReadOnlyList<SportsTeamResponse>> ListTeamsAsync(CancellationToken ct = default) =>
        QueryInlineAsync<SportsTeamResponse>(
            "SELECT \"Id\", \"TenantId\", \"Name\", \"Sport\", \"Coach\", \"Athletes\" FROM \"dbo\".\"SportsTeams\" ORDER BY \"Name\"", null, ct);

    public Task<SportsTeamResponse?> CreateTeamAsync(Guid tenantId, CreateSportsTeamRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<SportsTeamResponse>("dbo.SportsTeam_Create",
            new { TenantId = tenantId, r.Name, r.Sport, r.Coach, r.Athletes }, ct);

    public Task<IReadOnlyList<SportsEventResponse>> ListEventsAsync(CancellationToken ct = default) =>
        QueryInlineAsync<SportsEventResponse>(
            "SELECT \"Id\", \"TenantId\", \"Name\", \"EventDate\", \"Venue\" FROM \"dbo\".\"SportsEvents\" ORDER BY \"EventDate\"", null, ct);

    public Task<SportsEventResponse?> CreateEventAsync(Guid tenantId, CreateSportsEventRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<SportsEventResponse>("dbo.SportsEvent_Create",
            new { TenantId = tenantId, r.Name, r.EventDate, r.Venue }, ct);

    public Task<IReadOnlyList<SportsMedalResponse>> ListMedalsAsync(CancellationToken ct = default) =>
        QueryInlineAsync<SportsMedalResponse>(
            "SELECT \"Id\", \"TenantId\", \"Kind\", \"Title\", \"Year\" FROM \"dbo\".\"SportsMedals\" ORDER BY \"Year\" DESC, \"Kind\"", null, ct);

    public Task<SportsMedalResponse?> CreateMedalAsync(Guid tenantId, string kind, string? title, int year, CancellationToken ct = default) =>
        QuerySingleProcAsync<SportsMedalResponse>("dbo.SportsMedal_Create",
            new { TenantId = tenantId, Kind = kind, Title = title, Year = year }, ct);
}
