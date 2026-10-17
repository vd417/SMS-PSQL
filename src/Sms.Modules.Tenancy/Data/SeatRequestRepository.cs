using Sms.Modules.Tenancy.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Tenancy.Data;

/// <summary>Seat-increase requests raised by school owners and reviewed by the CATRE team.</summary>
public sealed class SeatRequestRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string Cols =
        "\"Id\", \"TenantId\", \"TenantName\", \"CurrentSeats\", \"RequestedSeats\", \"Status\", \"Note\", \"CreatedAt\", \"UpdatedAt\"";

    public async Task<SeatRequestResponse?> CreateAsync(
        Guid tenantId, string? tenantName, int current, int requested, string? note, Guid? byUserId,
        CancellationToken ct = default) =>
        (await QueryInlineAsync<SeatRequestResponse>(
            $"""
            INSERT INTO "dbo"."SeatIncreaseRequests"
                ("TenantId", "TenantName", "CurrentSeats", "RequestedSeats", "Note", "RequestedByUserId")
            VALUES (@tenantId, @tenantName, @current, @requested, @note, @byUserId)
            RETURNING {Cols}
            """,
            new { tenantId, tenantName, current, requested, note, byUserId }, ct)).FirstOrDefault();

    public Task<IReadOnlyList<SeatRequestResponse>> ListAsync(
        string? status, Guid? tenantId, CancellationToken ct = default) =>
        QueryInlineAsync<SeatRequestResponse>(
            $"""
            SELECT {Cols} FROM "dbo"."SeatIncreaseRequests"
            WHERE (@status IS NULL OR "Status" = @status)
              AND (@tenantId IS NULL OR "TenantId" = @tenantId)
            ORDER BY "CreatedAt" DESC
            """,
            new { status, tenantId }, ct);

    public async Task<SeatRequestResponse?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await QueryInlineAsync<SeatRequestResponse>(
            $"SELECT {Cols} FROM \"dbo\".\"SeatIncreaseRequests\" WHERE \"Id\" = @id", new { id }, ct))
        .FirstOrDefault();

    public async Task<SeatRequestResponse?> SetStatusAsync(
        Guid id, string status, Guid? reviewedBy, CancellationToken ct = default) =>
        (await QueryInlineAsync<SeatRequestResponse>(
            $"""
            UPDATE "dbo"."SeatIncreaseRequests"
            SET "Status" = @status, "ReviewedByUserId" = @reviewedBy, "UpdatedAt" = now()
            WHERE "Id" = @id
            RETURNING {Cols}
            """,
            new { id, status, reviewedBy }, ct)).FirstOrDefault();
}
