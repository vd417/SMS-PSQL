using Microsoft.Extensions.DependencyInjection;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Attendance;

public sealed record SchoolLocationResponse(double Lat, double Lng, int RadiusMeters, string? Name);
public sealed record UpsertSchoolLocationRequest(double Lat, double Lng, int RadiusMeters, string? Name);
public sealed record CheckEventResponse(
    string Kind, DateTime At, double Lat, double Lng, double AccuracyMeters, double DistanceMeters, bool Verified);
public sealed record PunchRequest(string Kind, DateTime At, double Lat, double Lng, double AccuracyMeters, int? OffsetMinutes = null);
public sealed record TeacherAttendanceDayResponse(DateOnly Date, CheckEventResponse? CheckIn, CheckEventResponse? CheckOut);
public sealed record TeacherAttendanceSummaryResponse(int DaysPresent, int DaysFlagged, double TotalHours);

// Staff self-service shape (sms-staff app) — composed from the same TeacherAttendanceDayResponse
// and SchoolLocationResponse the teacher-app endpoints already return, not a new data model.
public sealed record StaffAttendanceLogEntry(string Kind, DateTime At, bool InZone);
public sealed record StaffAttendanceResponse(
    bool CheckedIn, DateTime? CheckInAt, IReadOnlyList<StaffAttendanceLogEntry> LastLog,
    string DutyPost, int GeofenceRadiusM);
public sealed record StaffCheckRequest(DateTime At, double Lat, double Lng, double AccuracyMeters, int? OffsetMinutes = null);

/// Raised when a geo punch cannot be recorded (school location missing or outside geofence).
public sealed class GeofencePunchRejectedException : Exception
{
    public string ErrorCode { get; }
    public int StatusCode { get; }

    public GeofencePunchRejectedException(string errorCode, string message, int statusCode = 422)
        : base(message)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }
}

/// Per-school absence-alert configuration: streak thresholds + optional daily auto-send schedule.
public sealed record AttendanceAlertConfigResponse(
    int NoticeDays, int EmailDays, bool AutoSend, string AutoTime, string AutoChannel, DateTime? LastAutoSentDate);
public sealed record UpsertAttendanceAlertConfigRequest(
    int NoticeDays, int EmailDays, bool AutoSend, string? AutoTime, string? AutoChannel);

/// One tenant whose daily absence-alert auto-send is due right now.
public sealed record DueAlertTenant(Guid TenantId, int NoticeDays, int EmailDays, string AutoChannel);

public sealed class AttendanceAlertConfigRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public Task<AttendanceAlertConfigResponse?> GetAsync(Guid tenantId, CancellationToken ct = default) =>
        QuerySingleProcAsync<AttendanceAlertConfigResponse>(
            "dbo.AttendanceAlertConfig_Get", new { TenantId = tenantId }, ct);

    public Task<AttendanceAlertConfigResponse?> UpsertAsync(
        Guid tenantId, UpsertAttendanceAlertConfigRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<AttendanceAlertConfigResponse>(
            "dbo.AttendanceAlertConfig_Upsert",
            new { TenantId = tenantId, r.NoticeDays, r.EmailDays, r.AutoSend, r.AutoTime, r.AutoChannel }, ct);

    /// Tenants due for a daily auto-send. MUST be called on a platform session so RLS returns all rows.
    public Task<IReadOnlyList<DueAlertTenant>> ListDueAsync(DateTime today, int nowMinutes, CancellationToken ct = default) =>
        QueryProcAsync<DueAlertTenant>(
            "dbo.AttendanceAlertConfig_ListDue", new { Today = today.Date, NowMinutes = nowMinutes }, ct);

    /// Marks that today's sweep has run for a tenant (call on that tenant's session context).
    public Task MarkAutoSentAsync(Guid tenantId, DateTime date, CancellationToken ct = default) =>
        ExecuteProcAsync("dbo.AttendanceAlertConfig_MarkAutoSent", new { TenantId = tenantId, Date = date.Date }, ct);
}

public sealed class CheckInRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const double AccuracyCapMeters = 50;
    private sealed record CheckInRow(string Kind, DateTime At, double Lat, double Lng, double AccuracyMeters, double DistanceMeters, bool Verified);

    public async Task<SchoolLocationResponse?> GetSchoolLocationAsync(Guid tenantId, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<SchoolLocationResponse>(
            @"SELECT COALESCE(sl.""Lat"", t.""Lat"", 0) AS ""Lat"",
                     COALESCE(sl.""Lng"", t.""Lng"", 0) AS ""Lng"",
                     COALESCE(sl.""RadiusMeters"", t.""GeofenceRadiusMeters"", 50) AS ""RadiusMeters"",
                     COALESCE(sl.""Name"", t.""Name"") AS ""Name""
              FROM ""dbo"".""Tenants"" t
              LEFT JOIN ""dbo"".""SchoolLocations"" sl ON sl.""TenantId"" = t.""Id""
              WHERE t.""Id"" = @tenantId",
            new { tenantId }, ct);
        var loc = rows.FirstOrDefault();
        if (loc is null || (loc.Lat == 0 && loc.Lng == 0))
            return null;
        return loc;
    }

    public async Task<SchoolLocationResponse?> UpsertSchoolLocationAsync(
        Guid tenantId, UpsertSchoolLocationRequest r, CancellationToken ct = default) =>
        (await QueryProcAsync<SchoolLocationResponse>("dbo.SchoolLocation_Upsert",
            new { TenantId = tenantId, r.Lat, r.Lng, r.RadiusMeters, r.Name }, ct)).FirstOrDefault();

    public Task DeleteSchoolLocationAsync(Guid tenantId, CancellationToken ct = default) =>
        ExecuteProcAsync("dbo.SchoolLocation_Delete", new { TenantId = tenantId }, ct);

    /// Server-authoritative geofence verify: distance is computed here (haversine) from the stored
    /// school location, NOT trusted from the client. verified = distance <= radius + min(accuracy, cap).
    public async Task<CheckEventResponse> PunchAsync(Guid tenantId, Guid userId, PunchRequest r, CancellationToken ct = default)
    {
        var loc = await GetSchoolLocationAsync(tenantId, ct);
        if (loc is null)
            throw new GeofencePunchRejectedException(
                "school_location_not_configured", "School location is not configured");

        var distance = Haversine(loc.Lat, loc.Lng, r.Lat, r.Lng);
        var verified = distance <= loc.RadiusMeters + Math.Min(r.AccuracyMeters, AccuracyCapMeters);

        await ExecuteProcAsync("dbo.CheckIn_Insert", new
        {
            TenantId = tenantId, UserId = userId, r.Kind, r.At, r.Lat, r.Lng, r.AccuracyMeters,
            DistanceMeters = distance, Verified = verified
        }, ct);

        return new CheckEventResponse(
            r.Kind, r.At, r.Lat, r.Lng, r.AccuracyMeters, Math.Round(distance, 1), verified);
    }

    /// Silver/Gold manual punch — records time only, no GPS verification.
    public async Task<CheckEventResponse> ManualPunchAsync(
        Guid tenantId, Guid userId, string kind, DateTime atUtc, CancellationToken ct = default)
    {
        await ExecuteProcAsync("dbo.CheckIn_Insert", new
        {
            TenantId = tenantId, UserId = userId, Kind = kind, At = atUtc,
            Lat = 0.0, Lng = 0.0, AccuracyMeters = 0.0, DistanceMeters = 0.0, Verified = true
        }, ct);
        var at = DateTime.SpecifyKind(atUtc, DateTimeKind.Utc);
        return new CheckEventResponse(kind, at, 0, 0, 0, 0, true);
    }

  public async Task<TeacherAttendanceDayResponse> GetTodayAsync(
        Guid userId, DateOnly day, TimeSpan utcOffset, CancellationToken ct = default)
    {
        var (startUtc, endUtc) = LocalDayBoundsUtc(day, utcOffset);
        var rows = await QueryInlineAsync<CheckInRow>(
            "SELECT \"Kind\", \"At\", \"Lat\", \"Lng\", \"AccuracyMeters\", \"DistanceMeters\", \"Verified\" FROM \"dbo\".\"CheckIns\" " +
            "WHERE \"UserId\" = @userId AND \"At\" >= @startUtc AND \"At\" < @endUtc ORDER BY \"At\"",
            new { userId, startUtc, endUtc }, ct);
        var ci = rows.Where(x => x.Kind == "in").Select(ToEvent).LastOrDefault();
        var co = rows.Where(x => x.Kind == "out").Select(ToEvent).LastOrDefault();
        return new TeacherAttendanceDayResponse(day, ci, co);
    }

    public Task<IReadOnlyList<TeacherAttendanceDayResponse>> GetHistoryAsync(
        Guid userId, int limit, TimeSpan utcOffset, CancellationToken ct = default) =>
        GetHistoryAsync(new[] { userId }, limit, utcOffset, ct);

    /// Same per-day in/out grouping as the single-user overload, merged across every login
    /// user linked to one roster row (a teacher/staff person can have more than one account —
    /// see ReportingRepository.CandidateUserIds).
    public async Task<IReadOnlyList<TeacherAttendanceDayResponse>> GetHistoryAsync(
        IReadOnlyCollection<Guid> userIds, int limit, TimeSpan utcOffset, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return Array.Empty<TeacherAttendanceDayResponse>();

        var rows = await QueryInlineAsync<CheckInRow>(
            "SELECT \"Kind\", \"At\", \"Lat\", \"Lng\", \"AccuracyMeters\", \"DistanceMeters\", \"Verified\" FROM \"dbo\".\"CheckIns\" " +
            "WHERE \"UserId\" = ANY(@userIds) ORDER BY \"At\" DESC", new { userIds = userIds.ToArray() }, ct);

        return rows.GroupBy(r => DateOnly.FromDateTime(r.At.Add(utcOffset)))
            .OrderByDescending(g => g.Key)
            .Take(limit)
            .Select(g => new TeacherAttendanceDayResponse(
                g.Key,
                g.Where(x => x.Kind == "in").OrderBy(x => x.At).Select(ToEvent).LastOrDefault(),
                g.Where(x => x.Kind == "out").OrderBy(x => x.At).Select(ToEvent).LastOrDefault()))
            .ToList();
    }

    public async Task<TeacherAttendanceSummaryResponse> GetSummaryAsync(
        Guid userId, int year, int month, TimeSpan utcOffset, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<CheckInRow>(
            "SELECT \"Kind\", \"At\", \"Lat\", \"Lng\", \"AccuracyMeters\", \"DistanceMeters\", \"Verified\" FROM \"dbo\".\"CheckIns\" " +
            "WHERE \"UserId\" = @userId", new { userId }, ct);

        var inMonth = rows.Where(r =>
        {
            var local = r.At.Add(utcOffset);
            return local.Year == year && local.Month == month;
        }).ToList();

        var byDay = inMonth.GroupBy(r => DateOnly.FromDateTime(r.At.Add(utcOffset))).ToList();
        int daysPresent = byDay.Count(g => g.Any(x => x.Kind == "in"));
        int daysFlagged = byDay.Count(g => g.Any(x => !x.Verified));
        double totalHours = byDay.Sum(g =>
        {
            var firstIn = g.Where(x => x.Kind == "in").OrderBy(x => x.At).Select(x => (DateTime?)x.At).FirstOrDefault();
            var lastOut = g.Where(x => x.Kind == "out").OrderBy(x => x.At).Select(x => (DateTime?)x.At).LastOrDefault();
            return firstIn is { } i && lastOut is { } o && o > i ? (o - i).TotalHours : 0;
        });
        return new TeacherAttendanceSummaryResponse(daysPresent, daysFlagged, Math.Round(totalHours, 2));
    }

    /// Total hours worked in the current ISO week (Monday-Sunday, in the caller's local time),
    /// same first-in/last-out-per-day logic as GetSummaryAsync's month scan, just windowed
    /// differently. Used by the staff dashboard's "hours this week" figure.
    public async Task<double> GetWeekSummaryAsync(Guid userId, TimeSpan utcOffset, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<CheckInRow>(
            "SELECT \"Kind\", \"At\", \"Lat\", \"Lng\", \"AccuracyMeters\", \"DistanceMeters\", \"Verified\" FROM \"dbo\".\"CheckIns\" " +
            "WHERE \"UserId\" = @userId", new { userId }, ct);

        var todayLocal = DateOnly.FromDateTime(DateTime.UtcNow.Add(utcOffset));
        var daysSinceMonday = ((int)todayLocal.DayOfWeek + 6) % 7;
        var weekStart = todayLocal.AddDays(-daysSinceMonday);
        var weekEnd = weekStart.AddDays(6);

        var inWeek = rows.Where(r =>
        {
            var local = DateOnly.FromDateTime(r.At.Add(utcOffset));
            return local >= weekStart && local <= weekEnd;
        }).ToList();

        var byDay = inWeek.GroupBy(r => DateOnly.FromDateTime(r.At.Add(utcOffset)));
        double totalHours = byDay.Sum(g =>
        {
            var firstIn = g.Where(x => x.Kind == "in").OrderBy(x => x.At).Select(x => (DateTime?)x.At).FirstOrDefault();
            var lastOut = g.Where(x => x.Kind == "out").OrderBy(x => x.At).Select(x => (DateTime?)x.At).LastOrDefault();
            return firstIn is { } i && lastOut is { } o && o > i ? (o - i).TotalHours : 0;
        });
        return Math.Round(totalHours, 2);
    }

    private static CheckEventResponse ToEvent(CheckInRow x) =>
        new(x.Kind, DateTime.SpecifyKind(x.At, DateTimeKind.Utc), x.Lat, x.Lng, x.AccuracyMeters, x.DistanceMeters, x.Verified);

    /// <summary>UTC instants for local calendar midnight through next midnight.</summary>
    internal static (DateTime StartUtc, DateTime EndUtc) LocalDayBoundsUtc(DateOnly day, TimeSpan utcOffset)
    {
        var localMidnight = day.ToDateTime(TimeOnly.MinValue);
        var startUtc = localMidnight - utcOffset;
        return (startUtc, startUtc.AddDays(1));
    }

    private static double Haversine(double lat1, double lng1, double lat2, double lng2)
    {
        const double r = 6371000; // earth radius, metres
        double dLat = (lat2 - lat1) * Math.PI / 180;
        double dLng = (lng2 - lng1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                   Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return r * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}

public static class AttendanceModule
{
    public static IServiceCollection AddAttendanceModule(this IServiceCollection services)
    {
        services.AddScoped<CheckInRepository>();
        services.AddScoped<AttendanceAlertConfigRepository>();
        return services;
    }
}
