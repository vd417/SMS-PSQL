using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sms.Application.Common;
using Sms.Application.Services.Tenancy;
using Sms.Modules.Reporting.Contracts;
using Sms.Modules.Reporting.Data;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Results;
using Sms.Shared.Kernel.Tenancy;
using Sms.Shared.Kernel.Time;

namespace Sms.Application.Services.Reporting;

public interface ISchoolReportService
{
    IReadOnlyList<SchoolReportCatalogItem> Catalog();
    Task<ApiResult<SchoolReportOptions>> OptionsAsync(CancellationToken ct = default);
    Task<ApiResult<SchoolReportResult>> RunAsync(SchoolReportQuery query, CancellationToken ct = default);
    Task<ApiResult<SchoolReportFile>> ExportAsync(SchoolReportQuery query, string? format, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<SchoolReportEmailResult>>> EmailAsync(SchoolReportEmailRequest req, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<SchoolReportSchedule>>> ListSchedulesAsync(string? reportKey, CancellationToken ct = default);
    Task<ApiResult<SchoolReportSchedule>> SaveScheduleAsync(SaveReportScheduleRequest req, CancellationToken ct = default);
    Task<ApiResult<SchoolReportSchedule>> SetStatusAsync(Guid id, bool active, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<SchoolReportEmailResult>>> RunNowAsync(Guid id, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<SchoolReportScheduleRun>>> RunsAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<SchoolReportDue>> ListDueAsync(DateTime utcNow, CancellationToken ct = default);
    Task SendDueAsync(Guid scheduleId, CancellationToken ct = default);
}

public sealed record SchoolReportFile(byte[] Bytes, string ContentType, string FileName);

public sealed partial class SchoolReportService(
    SchoolReportRepository repo,
    ITenantContext tenant,
    IEmailSender email,
    IClock clock,
    ISchoolMailIdentity schoolMail) : ISchoolReportService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public IReadOnlyList<SchoolReportCatalogItem> Catalog() => SchoolReportCatalog.ToCatalog();

    public async Task<ApiResult<SchoolReportOptions>> OptionsAsync(CancellationToken ct = default)
    {
        if (tenant.TenantId is null) return ApiResult<SchoolReportOptions>.Fail(new Error("forbidden", "school only"), 403);
        return ApiResult<SchoolReportOptions>.Ok(await repo.OptionsAsync(ct));
    }

    public async Task<ApiResult<SchoolReportResult>> RunAsync(SchoolReportQuery query, CancellationToken ct = default)
    {
        var built = await BuildAsync(query, ct);
        return built.Error is { } error
            ? ApiResult<SchoolReportResult>.Fail(error, built.Status)
            : ApiResult<SchoolReportResult>.Ok(built.Result!);
    }

    public async Task<ApiResult<SchoolReportFile>> ExportAsync(SchoolReportQuery query, string? format, CancellationToken ct = default)
    {
        var built = await BuildAsync(query, ct);
        if (built.Error is { } error) return ApiResult<SchoolReportFile>.Fail(error, built.Status);
        var file = FileFor(built.Result!, built.Query!, format ?? query.Format);
        return ApiResult<SchoolReportFile>.Ok(file);
    }

    public async Task<ApiResult<IReadOnlyList<SchoolReportEmailResult>>> EmailAsync(
        SchoolReportEmailRequest req, CancellationToken ct = default)
    {
        if (req.Query is null)
            return ApiResult<IReadOnlyList<SchoolReportEmailResult>>.Fail(new Error("validation_failed", "query is required"), 422);
        var emails = ParseEmails(req.Recipients);
        if (emails.Error is { } error)
            return ApiResult<IReadOnlyList<SchoolReportEmailResult>>.Fail(error, 422);
        var built = await BuildAsync(req.Query, ct);
        if (built.Error is { } buildError)
            return ApiResult<IReadOnlyList<SchoolReportEmailResult>>.Fail(buildError, built.Status);
        var results = await SendAsync(built.Result!, built.Query!, emails.Emails!, req.Subject, req.Message, req.Format, ct);
        return ApiResult<IReadOnlyList<SchoolReportEmailResult>>.Ok(results);
    }

    public async Task<ApiResult<IReadOnlyList<SchoolReportSchedule>>> ListSchedulesAsync(string? reportKey, CancellationToken ct = default)
    {
        if (tenant.TenantId is null) return ApiResult<IReadOnlyList<SchoolReportSchedule>>.Fail(new Error("forbidden", "school only"), 403);
        var key = string.IsNullOrWhiteSpace(reportKey) ? null : reportKey.Trim();
        if (key is not null && SchoolReportCatalog.Find(key) is null)
            return ApiResult<IReadOnlyList<SchoolReportSchedule>>.Fail(new Error("validation_failed", "unknown report"), 422);
        return ApiResult<IReadOnlyList<SchoolReportSchedule>>.Ok(await repo.ListSchedulesAsync(key, ct));
    }

    public async Task<ApiResult<SchoolReportSchedule>> SaveScheduleAsync(SaveReportScheduleRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<SchoolReportSchedule>.Fail(new Error("forbidden", "school only"), 403);
        var def = SchoolReportCatalog.Find(req.ReportKey);
        if (def is null)
            return ApiResult<SchoolReportSchedule>.Fail(new Error("validation_failed", "unknown report"), 422);
        var frequency = (req.Frequency ?? "").Trim().ToLowerInvariant();
        if (frequency is not ("daily" or "weekly" or "15days" or "monthly"))
            return ApiResult<SchoolReportSchedule>.Fail(new Error("validation_failed", "frequency must be daily, weekly, 15 days, or monthly"), 422);
        if (!TimeRegex().IsMatch(req.SendTime ?? ""))
            return ApiResult<SchoolReportSchedule>.Fail(new Error("validation_failed", "send time must be HH:mm"), 422);
        var format = NormalizeFormat(req.Format);
        if (format is null)
            return ApiResult<SchoolReportSchedule>.Fail(new Error("validation_failed", "format must be xlsx or pdf"), 422);
        var emails = ParseEmails(req.Recipients);
        if (emails.Error is { } error)
            return ApiResult<SchoolReportSchedule>.Fail(error, 422);
        var offset = SchoolReportMath.ClampOffset(req.OffsetMinutes);
        var send = TimeOnly.ParseExact(req.SendTime!, "HH:mm", CultureInfo.InvariantCulture);
        var next = SchoolReportMath.NextRunUtc(frequency, send, offset, req.DayOfWeek, req.DayOfMonth, clock.UtcNow);
        var filters = (req.Filters ?? new SchoolReportFilters()) with
        {
            FromDate = null,
            ToDate = null,
            FromTime = null,
            ToTime = null,
        };
        var filtersJson = JsonSerializer.Serialize(filters, Json);
        var recipients = string.Join(", ", emails.Emails!);
        var subject = TrimOrNull(req.Subject, 200);
        var message = TrimOrNull(req.Message, 2000);
        var saved = req with { Frequency = frequency, Format = format, OffsetMinutes = offset, Subject = subject, Message = message };
        Guid id;
        if (req.Id is { } existing)
        {
            if (await repo.GetScheduleAsync(existing, ct) is null)
                return ApiResult<SchoolReportSchedule>.Fail(new Error("not_found", "schedule not found"), 404);
            await repo.UpdateScheduleAsync(existing, saved, def.Title, recipients, filtersJson, next, ct);
            id = existing;
        }
        else
        {
            id = await repo.InsertScheduleAsync(tid, tenant.UserId, saved, def.Title, recipients, filtersJson, next, ct);
        }
        return ApiResult<SchoolReportSchedule>.Ok((await repo.GetScheduleAsync(id, ct))!, req.Id is null ? 201 : 200);
    }

    public async Task<ApiResult<SchoolReportSchedule>> SetStatusAsync(Guid id, bool active, CancellationToken ct = default)
    {
        if (tenant.TenantId is null)
            return ApiResult<SchoolReportSchedule>.Fail(new Error("forbidden", "school only"), 403);
        var row = await repo.GetScheduleAsync(id, ct);
        if (row is null)
            return ApiResult<SchoolReportSchedule>.Fail(new Error("not_found", "schedule not found"), 404);
        DateTime? next = null;
        if (active && TimeOnly.TryParseExact(row.SendTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var send))
            next = SchoolReportMath.NextRunUtc(row.Frequency, send, row.UtcOffsetMinutes, row.DayOfWeek, row.DayOfMonth, clock.UtcNow);
        await repo.SetStatusAsync(id, active ? "active" : "paused", next, ct);
        return ApiResult<SchoolReportSchedule>.Ok((await repo.GetScheduleAsync(id, ct))!);
    }

    public async Task<ApiResult<IReadOnlyList<SchoolReportEmailResult>>> RunNowAsync(Guid id, CancellationToken ct = default)
    {
        if (tenant.TenantId is null)
            return ApiResult<IReadOnlyList<SchoolReportEmailResult>>.Fail(new Error("forbidden", "school only"), 403);
        var sent = await DeliverScheduleAsync(id, claim: false, ct);
        return sent.Error is { } error
            ? ApiResult<IReadOnlyList<SchoolReportEmailResult>>.Fail(error, sent.Status)
            : ApiResult<IReadOnlyList<SchoolReportEmailResult>>.Ok(sent.Results!);
    }

    public async Task<ApiResult<IReadOnlyList<SchoolReportScheduleRun>>> RunsAsync(Guid id, CancellationToken ct = default)
    {
        if (tenant.TenantId is null)
            return ApiResult<IReadOnlyList<SchoolReportScheduleRun>>.Fail(new Error("forbidden", "school only"), 403);
        if (await repo.GetScheduleAsync(id, ct) is null)
            return ApiResult<IReadOnlyList<SchoolReportScheduleRun>>.Fail(new Error("not_found", "schedule not found"), 404);
        return ApiResult<IReadOnlyList<SchoolReportScheduleRun>>.Ok(await repo.ListRunsAsync(id, ct));
    }

    public Task<IReadOnlyList<SchoolReportDue>> ListDueAsync(DateTime utcNow, CancellationToken ct = default) =>
        repo.ListDueAsync(utcNow, ct);

    public async Task SendDueAsync(Guid scheduleId, CancellationToken ct = default)
    {
        await DeliverScheduleAsync(scheduleId, claim: true, ct);
    }

    private async Task<(SchoolReportResult? Result, SchoolReportQuery? Query, Error? Error, int Status)> BuildAsync(
        SchoolReportQuery query, CancellationToken ct)
    {
        if (tenant.TenantId is not { } tid)
            return (null, null, new Error("forbidden", "school only"), 403);
        var def = SchoolReportCatalog.Find(query.ReportKey);
        if (def is null)
            return (null, null, new Error("validation_failed", "unknown report"), 422);
        if (!TryRange(query, out var from, out var to, out var error))
            return (null, null, error, 422);
        if (!string.IsNullOrWhiteSpace(query.FromTime) && !TimeRegex().IsMatch(query.FromTime))
            return (null, null, new Error("validation_failed", "from time must be HH:mm"), 422);
        if (!string.IsNullOrWhiteSpace(query.ToTime) && !TimeRegex().IsMatch(query.ToTime))
            return (null, null, new Error("validation_failed", "to time must be HH:mm"), 422);
        var offset = SchoolReportMath.ClampOffset(query.OffsetMinutes);
        var filters = FiltersOf(query);
        if (IsStudentAttendance(def.Key) && !string.IsNullOrWhiteSpace(query.Session))
        {
            var bounds = await repo.SessionBoundsAsync(query.Session.Trim(), ct);
            if (bounds is null)
                return (null, null, new Error("validation_failed", "academic session not found"), 422);
            if (bounds.StartsOn is { } start)
            {
                var startDate = DateOnly.FromDateTime(start);
                if (startDate > from) from = startDate;
            }
            if (bounds.EndsOn is { } end)
            {
                var endDate = DateOnly.FromDateTime(end);
                if (endDate < to) to = endDate;
            }
        }
        var outputKey = query.Summary && query.ReportKey is "student_attendance" or "student_attendance_history"
            ? "student_attendance_summary"
            : query.ReportKey;
        var output = SchoolReportCatalog.Find(outputKey) ?? def;
        var sqlKey = StudentSqlKey(output.Key);
        var args = new SchoolReportArgs
        {
            FromDate = from.ToDateTime(TimeOnly.MinValue),
            ToDate = to.ToDateTime(TimeOnly.MinValue),
            FromTime = string.IsNullOrWhiteSpace(query.FromTime) ? null : query.FromTime.Trim(),
            ToTime = string.IsNullOrWhiteSpace(query.ToTime) ? null : query.ToTime.Trim(),
            OffsetMinutes = offset,
            ClassName = SchoolReportMath.Exact(filters.ClassName),
            Section = SchoolReportMath.Exact(filters.Section),
            Student = SchoolReportMath.Like(filters.Student),
            Teacher = SchoolReportMath.Like(filters.Teacher),
            Employee = SchoolReportMath.Like(filters.Employee),
            Department = SchoolReportMath.Exact(filters.Department),
            Designation = SchoolReportMath.Exact(filters.Designation),
            Role = SchoolReportMath.Exact(filters.Role),
            Gender = NormalizeGender(filters.Gender),
            FeeHead = SchoolReportMath.Exact(filters.FeeHead),
            PaymentMode = SchoolReportMath.Exact(filters.PaymentMode),
            PaymentStatus = SchoolReportMath.Exact(filters.PaymentStatus),
            AttendanceStatus = IsStudentAttendance(def.Key)
                ? NormalizeStudentAttendance(filters.AttendanceStatus)
                : SchoolReportMath.Exact(filters.AttendanceStatus),
            Route = SchoolReportMath.Exact(filters.Route),
            Vehicle = SchoolReportMath.Exact(filters.Vehicle),
            Subject = SchoolReportMath.Like(query.Subject),
            Period = SchoolReportMath.Exact(query.Period),
        };
        var skip = to < from
            || (output.Key == "student_attendance_history" && string.IsNullOrWhiteSpace(query.Student));
        IReadOnlyList<IReadOnlyList<string>> rows = skip ? [] : await repo.RunAsync(sqlKey, args, ct);
        if (output.Key is "student_attendance_class" or "student_attendance_lowest")
        {
            rows = SchoolReportMath.ClassAttendance(rows);
            if (output.Key == "student_attendance_lowest")
                rows = rows.OrderBy(r => SchoolReportMath.Amount(r, r.Count - 1)).ToList();
        }
        else if (output.Key == "student_attendance_low")
            rows = SchoolReportMath.Below75(rows);
        else if (output.Key is "student_attendance_streak5" or "student_attendance_streak7")
        {
            var min = output.Key.EndsWith('7') ? 7 : 5;
            rows = rows.Where(r => SchoolReportMath.Amount(r, 6) >= min).ToList();
        }
        else if (output.Key == "student_attendance_pending")
            rows = rows.Where(r => SchoolReportMath.Amount(r, 6) > 0).ToList();
        else if (output.Key == "student_attendance_decline")
            rows = rows.Where(r => SchoolReportMath.Amount(r, 6) <= -10).ToList();
        else if (output.Key == "student_attendance_improve")
            rows = rows.Where(r => SchoolReportMath.Amount(r, 6) >= 10).ToList();
        IReadOnlyList<string> columns = output.Columns;
        if (output.Key is "student_attendance" or "student_attendance_history")
            (columns, rows) = SchoolReportMath.OmitIfBlank(columns, rows, "Subject", "Period");
        var cards = SchoolReportMath.Cards(output.Key, columns, rows);
        var charts = SchoolReportMath.Charts(output.Key, columns, rows);
        if (query.Summary && !IsStudentAttendance(def.Key))
            (columns, rows) = SchoolReportMath.Summarize(def.Key, columns, rows);
        var keep = query.Columns;
        if (IsStudentAttendance(output.Key) && keep is { Count: > 0 }
            && keep.Any(k => !columns.Any(c => string.Equals(c, k, StringComparison.OrdinalIgnoreCase))))
            keep = null;
        (columns, rows) = SchoolReportMath.KeepColumns(columns, rows, keep);
        var school = await repo.SchoolNameAsync(tid, ct);
        var result = new SchoolReportResult(
            output.Key, output.Title, output.Dated, output.Note, school,
            from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            SchoolReportMath.FilterLabel(filters),
            columns, rows, cards, charts);
        return (result, query, null, 200);
    }

    private async Task<(IReadOnlyList<SchoolReportEmailResult>? Results, Error? Error, int Status)> DeliverScheduleAsync(
        Guid id, bool claim, CancellationToken ct)
    {
        var row = await repo.GetScheduleAsync(id, ct);
        if (row is null) return (null, new Error("not_found", "schedule not found"), 404);
        if (tenant.TenantId is not { } tid) return (null, new Error("forbidden", "school only"), 403);
        var offset = TimeSpan.FromMinutes(row.UtcOffsetMinutes);
        var local = clock.UtcNow.Add(offset);
        if (claim)
        {
            if (!TimeOnly.TryParseExact(row.SendTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var send))
                return ([], null, 200);
            var next = SchoolReportMath.NextRunUtc(row.Frequency, send, row.UtcOffsetMinutes, row.DayOfWeek, row.DayOfMonth, clock.UtcNow);
            if (await repo.ClaimAsync(id, clock.UtcNow, next, ct) == 0)
                return ([], null, 200);
        }
        SchoolReportFilters? filters = null;
        try { filters = JsonSerializer.Deserialize<SchoolReportFilters>(row.FiltersJson, Json); }
        catch { filters = null; }
        var window = SchoolReportMath.Window(row.Frequency, local);
        var query = new SchoolReportQuery(
            row.ReportKey,
            window.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            window.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            null,
            null,
            row.UtcOffsetMinutes,
            filters?.ClassName, filters?.Section, filters?.Student, filters?.Teacher, filters?.Employee,
            filters?.Department, filters?.Designation, filters?.Role, filters?.Gender, filters?.FeeHead,
            filters?.PaymentMode, filters?.PaymentStatus, filters?.AttendanceStatus, filters?.Route, filters?.Vehicle,
            Subject: filters?.Subject, Period: filters?.Period, Session: filters?.Session);
        var built = await BuildAsync(query, ct);
        IReadOnlyList<SchoolReportEmailResult> results;
        if (built.Error is { } error)
            results = [new("*", "failed", error.Message)];
        else
            results = await SendAsync(built.Result!, built.Query!, SplitStored(row.Recipients), row.Subject, row.Message, row.Format, ct);
        var ok = results.Count > 0 && results.All(r => r.Status == "sent");
        var detail = string.Join("; ", results.Select(r => $"{r.Email}: {r.Status}" + (r.Detail is null ? "" : $" ({r.Detail})")));
        await repo.InsertRunAsync(tid, id, ok ? "sent" : "failed", detail, window.From, window.To, ct);
        return (results, null, 200);
    }

    private async Task<IReadOnlyList<SchoolReportEmailResult>> SendAsync(
        SchoolReportResult report, SchoolReportQuery query, IReadOnlyList<string> emails,
        string? subject, string? message, string? format, CancellationToken ct)
    {
        var file = FileFor(report, query, format);
        var titleSubject = string.IsNullOrWhiteSpace(subject)
            ? $"{report.SchoolName} — {report.Title}"
            : subject.Trim();
        var body = string.IsNullOrWhiteSpace(message)
            ? $"{report.Title} for {report.FromDate} to {report.ToDate}. Filters: {report.Filters}."
            : message.Trim();
        var from = await schoolMail.ForTenantAsync(tenant.TenantId, ct);
        var results = new List<SchoolReportEmailResult>();
        foreach (var address in emails)
        {
            try
            {
                await email.SendAsync(new EmailMessage(
                    address, titleSubject, body, file.Bytes, file.FileName, file.ContentType).WithRoute(from), ct);
                results.Add(new(address, "sent", null));
            }
            catch (Exception ex)
            {
                results.Add(new(address, "failed", ex.Message));
            }
        }
        return results;
    }

    private static SchoolReportFile FileFor(SchoolReportResult report, SchoolReportQuery query, string? format)
    {
        var pdf = string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase);
        var name = Slug(report.Title) + "-" + report.FromDate + "-" + report.ToDate + (pdf ? ".pdf" : ".xlsx");
        if (pdf)
        {
            var bytes = SchoolReportPdf.Build(
                report.SchoolName, report.Title, report.FromDate, report.ToDate, report.Filters, report.Columns, report.Rows);
            return new SchoolReportFile(bytes, "application/pdf", name);
        }
        var xlsx = SchoolReportWorkbook.Build(
            report.SchoolName, report.Title, report.FromDate, report.ToDate, query.FromTime, query.ToTime,
            report.Filters, report.Columns, report.Rows);
        return new SchoolReportFile(xlsx, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
    }

    private bool TryRange(SchoolReportQuery query, out DateOnly from, out DateOnly to, out Error? error)
    {
        error = null;
        var offset = SchoolReportMath.ClampOffset(query.OffsetMinutes);
        var local = clock.UtcNow.AddMinutes(offset);
        from = ParseDate(query.FromDate) ?? new DateOnly(local.Year, local.Month, 1);
        to = ParseDate(query.ToDate) ?? DateOnly.FromDateTime(local);
        if (!string.IsNullOrWhiteSpace(query.FromDate) && ParseDate(query.FromDate) is null)
        {
            error = new Error("validation_failed", "from date must be yyyy-MM-dd");
            return false;
        }
        if (!string.IsNullOrWhiteSpace(query.ToDate) && ParseDate(query.ToDate) is null)
        {
            error = new Error("validation_failed", "to date must be yyyy-MM-dd");
            return false;
        }
        if (to < from)
        {
            error = new Error("validation_failed", "to date is before from date");
            return false;
        }
        if (to.DayNumber - from.DayNumber > 800)
        {
            error = new Error("validation_failed", "date range cannot exceed 800 days");
            return false;
        }
        return true;
    }

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static SchoolReportFilters FiltersOf(SchoolReportQuery q) => new(
        q.ClassName, q.Section, q.Student, q.Teacher, q.Employee, q.Department, q.Designation, q.Role,
        q.Gender, q.FeeHead, q.PaymentMode, q.PaymentStatus, q.AttendanceStatus, q.Route, q.Vehicle,
        Subject: q.Subject, Period: q.Period, Session: q.Session);

    private static bool IsStudentAttendance(string key) =>
        key.StartsWith("student_attendance", StringComparison.OrdinalIgnoreCase);

    private static string StudentSqlKey(string key) => key switch
    {
        "student_attendance_history" => "student_attendance",
        "student_attendance_low" or "student_attendance_class" or "student_attendance_lowest" => "student_attendance_summary",
        "student_attendance_streak5" or "student_attendance_streak7" => "student_attendance_streak",
        "student_attendance_pending" => "student_attendance_marking",
        "student_attendance_decline" or "student_attendance_improve" => "student_attendance_shift",
        _ => key,
    };

    private static string? NormalizeStudentAttendance(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v switch
        {
            null or "" => null,
            "p" or "present" => "present",
            "a" or "absent" => "absent",
            "l" or "late" => "late",
            "h" or "half day" or "half_day" or "halfday" => "half_day",
            "n" or "pending" => "pending",
            "lv" or "leave" or "on_leave" or "on leave" => "leave",
            _ => v,
        };
    }

    private static string? NormalizeGender(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v switch
        {
            null or "" => null,
            "m" or "male" => "M",
            "f" or "female" => "F",
            "o" or "other" => "O",
            _ => v.ToUpperInvariant(),
        };
    }

    private static string? NormalizeFormat(string? format)
    {
        var v = (format ?? "xlsx").Trim().ToLowerInvariant();
        return v is "xlsx" or "pdf" ? v : null;
    }

    private static (IReadOnlyList<string>? Emails, Error? Error) ParseEmails(IReadOnlyList<string>? raw)
    {
        var emails = (raw ?? [])
            .SelectMany(v => (v ?? "").Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries))
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (emails.Count == 0) return (null, new Error("validation_failed", "at least one email is required"));
        if (emails.Count > 20) return (null, new Error("validation_failed", "at most 20 emails"));
        if (emails.Any(e => !EmailRegex().IsMatch(e) || e.Length > 256))
            return (null, new Error("validation_failed", "one or more emails are not valid"));
        return (emails, null);
    }

    private static IReadOnlyList<string> SplitStored(string recipients) =>
        recipients.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? TrimOrNull(string? value, int max)
    {
        var t = value?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return t.Length <= max ? t : t[..max];
    }

    private static string Slug(string value)
    {
        var chars = value.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return string.IsNullOrEmpty(slug) ? "report" : slug;
    }

    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();
}
