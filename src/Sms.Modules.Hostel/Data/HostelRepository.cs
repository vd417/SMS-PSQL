using Sms.Modules.Hostel.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Hostel.Data;

public sealed class HostelRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    /// Occupancy = residents ÷ total bed capacity, as a whole percent (0 when no capacity yet).
    public async Task<HostelSummaryResponse> SummaryAsync(CancellationToken ct = default) =>
        (await QueryInlineAsync<HostelSummaryResponse>(
            @"WITH counts AS (
                SELECT
                    (SELECT COUNT(*) FROM ""dbo"".""HostelResidents"") AS residents,
                    COALESCE((SELECT SUM(""Capacity"") FROM ""dbo"".""HostelRooms""), 0) AS capacity
              )
              SELECT
                CAST((SELECT COUNT(*) FROM ""dbo"".""HostelBlocks"") AS int) AS ""Blocks"",
                CAST((SELECT COUNT(*) FROM ""dbo"".""HostelRooms"") AS int) AS ""Rooms"",
                CAST(counts.residents AS int) AS ""Residents"",
                CASE WHEN counts.capacity = 0 THEN 0
                     ELSE CAST(ROUND(100.0 * counts.residents / counts.capacity, 0) AS int) END AS ""OccupancyPct""
              FROM counts",
            null, ct)).First();

    public Task<IReadOnlyList<HostelBlockResponse>> ListBlocksAsync(CancellationToken ct = default) =>
        QueryInlineAsync<HostelBlockResponse>(
            "SELECT \"Id\", \"TenantId\", \"Name\", \"Warden\" FROM \"dbo\".\"HostelBlocks\" ORDER BY \"Name\"", null, ct);

    public Task<HostelBlockResponse?> CreateBlockAsync(Guid tenantId, CreateHostelBlockRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<HostelBlockResponse>("dbo.HostelBlock_Create",
            new { TenantId = tenantId, r.Name, r.Warden }, ct);

    public Task<IReadOnlyList<HostelRoomResponse>> ListRoomsAsync(CancellationToken ct = default) =>
        QueryInlineAsync<HostelRoomResponse>(
            @"SELECT r.""Id"", r.""TenantId"", r.""BlockId"", b.""Name"" AS ""BlockName"", r.""RoomNo"", r.""Capacity"",
                     CAST((SELECT COUNT(*) FROM ""dbo"".""HostelResidents"" res WHERE res.""RoomId"" = r.""Id"") AS int) AS ""Residents""
              FROM ""dbo"".""HostelRooms"" r LEFT JOIN ""dbo"".""HostelBlocks"" b ON b.""Id"" = r.""BlockId""
              ORDER BY b.""Name"", r.""RoomNo""", null, ct);

    public Task<HostelRoomResponse?> CreateRoomAsync(Guid tenantId, CreateHostelRoomRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<HostelRoomResponse>("dbo.HostelRoom_Create",
            new { TenantId = tenantId, r.BlockId, r.RoomNo, r.Capacity }, ct);

    public Task<IReadOnlyList<HostelResidentResponse>> ListResidentsAsync(CancellationToken ct = default) =>
        QueryInlineAsync<HostelResidentResponse>(
            @"SELECT res.""Id"", res.""TenantId"", res.""RoomId"", r.""RoomNo"", res.""StudentName"", res.""StudentId""
              FROM ""dbo"".""HostelResidents"" res LEFT JOIN ""dbo"".""HostelRooms"" r ON r.""Id"" = res.""RoomId""
              ORDER BY res.""StudentName""", null, ct);

    public Task<HostelResidentResponse?> CreateResidentAsync(Guid tenantId, CreateHostelResidentRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<HostelResidentResponse>("dbo.HostelResident_Create",
            new { TenantId = tenantId, r.RoomId, r.StudentName, r.StudentId }, ct);
}
