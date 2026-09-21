using Sms.Modules.Tenancy.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Tenancy.Data;

public sealed class TicketRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string Cols =
        "\"Id\", \"Subject\", \"TenantId\", \"TenantName\", \"Status\", \"Priority\", \"Assignee\", \"Created\", \"Updated\", \"MessagesCount\"";

    public async Task<Guid> CreateAsync(CreateTicketRequest r, CancellationToken ct = default) =>
        await QuerySingleProcAsync<Guid>("dbo.Ticket_Create",
            new { r.Subject, r.TenantId, r.TenantName, r.Priority }, ct);

    public Task<IReadOnlyList<TicketResponse>> ListAsync(string? status, string? q, CancellationToken ct = default) =>
        QueryInlineAsync<TicketResponse>(
            $"SELECT {Cols} FROM \"dbo\".\"Tickets\" WHERE (@status IS NULL OR \"Status\" = @status) " +
            "AND (@q IS NULL OR \"Subject\" LIKE '%' || @q || '%' OR \"TenantName\" LIKE '%' || @q || '%') ORDER BY \"Updated\" DESC",
            new { status, q }, ct);

    public async Task<TicketDetailResponse?> GetDetailAsync(Guid id, CancellationToken ct = default)
    {
        var t = (await QueryInlineAsync<TicketResponse>($"SELECT {Cols} FROM \"dbo\".\"Tickets\" WHERE \"Id\" = @id", new { id }, ct))
            .FirstOrDefault();
        if (t is null) return null;
        var msgs = await QueryInlineAsync<TicketMessageResponse>(
            "SELECT \"Id\", \"TicketId\", \"Who\", \"Role\", \"Text\", \"When\" FROM \"dbo\".\"TicketMessages\" WHERE \"TicketId\" = @id ORDER BY \"When\"",
            new { id }, ct);
        return new TicketDetailResponse(t.Id, t.Subject, t.TenantId, t.TenantName, t.Status, t.Priority,
            t.Assignee, t.Created, t.Updated, t.MessagesCount, msgs);
    }

    public Task<TicketResponse?> UpdateAsync(Guid id, string? status, string? assignee, CancellationToken ct = default) =>
        QuerySingleProcAsync<TicketResponse>("dbo.Ticket_Update",
            new { Id = id, Status = status, Assignee = assignee }, ct);

    public Task AddMessageAsync(Guid id, string who, string text, CancellationToken ct = default) =>
        ExecuteProcAsync("dbo.Ticket_AddMessage", new { TicketId = id, Who = who, Role = "agent", Text = text }, ct);
}
