using Sms.Modules.Academics.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Academics.Data;

public sealed class CalendarRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string Cols = "\"Id\", \"TenantId\", \"Title\", \"Date\", \"Time\", \"Type\", \"Description\", \"ChannelsJson\"";

    public Task<IReadOnlyList<CalendarEventResponse>> ListAsync(CancellationToken ct = default) =>
        QueryInlineAsync<CalendarEventResponse>(
            $"SELECT {Cols} FROM \"dbo\".\"CalendarEvents\" ORDER BY \"Date\", \"Time\"", null, ct);

    public Task<CalendarEventResponse?> CreateAsync(Guid tenantId, CreateCalendarEventRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<CalendarEventResponse>("dbo.CalendarEvent_Create", new
        {
            TenantId = tenantId,
            r.Title,
            r.Date,
            r.Time,
            r.Type,
            r.Description,
            r.ChannelsJson,
        }, ct);

    public async Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        var row = await QuerySingleProcAsync<DeletedCount>("dbo.CalendarEvent_Delete", new
        {
            TenantId = tenantId,
            Id = id,
        }, ct);
        return row is { Deleted: > 0 };
    }

    private sealed record DeletedCount(int Deleted);
}
