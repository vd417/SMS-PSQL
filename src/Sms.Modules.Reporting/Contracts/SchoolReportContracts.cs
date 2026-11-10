namespace Sms.Modules.Reporting.Contracts;

public sealed record SchoolReportCatalogItem(
    string Key,
    string Tab,
    string Title,
    bool Dated,
    string Note,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> Filters);

public sealed record SchoolReportFilters(
    string? ClassName = null,
    string? Section = null,
    string? Student = null,
    string? Teacher = null,
    string? Employee = null,
    string? Department = null,
    string? Designation = null,
    string? Role = null,
    string? Gender = null,
    string? FeeHead = null,
    string? PaymentMode = null,
    string? PaymentStatus = null,
    string? AttendanceStatus = null,
    string? Route = null,
    string? Vehicle = null,
    string? FromDate = null,
    string? ToDate = null,
    string? FromTime = null,
    string? ToTime = null);

public sealed record SchoolReportQuery(
    string ReportKey,
    string? FromDate,
    string? ToDate,
    string? FromTime,
    string? ToTime,
    int? OffsetMinutes,
    string? ClassName,
    string? Section,
    string? Student,
    string? Teacher,
    string? Employee,
    string? Department,
    string? Designation,
    string? Role,
    string? Gender,
    string? FeeHead,
    string? PaymentMode,
    string? PaymentStatus,
    string? AttendanceStatus,
    string? Route,
    string? Vehicle,
    bool Summary = false,
    IReadOnlyList<string>? Columns = null,
    string? Format = null);

public sealed record SchoolReportCard(string Label, string Value);

public sealed record SchoolReportPoint(string Label, decimal Value);

public sealed record SchoolReportChart(string Title, string Kind, IReadOnlyList<SchoolReportPoint> Points);

public sealed record SchoolReportResult(
    string ReportKey,
    string Title,
    bool Dated,
    string Note,
    string SchoolName,
    string FromDate,
    string ToDate,
    string Filters,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<SchoolReportCard> Cards,
    IReadOnlyList<SchoolReportChart> Charts);

public sealed record SchoolReportOptions(
    IReadOnlyList<string> Classes,
    IReadOnlyList<string> Sections,
    IReadOnlyList<string> Departments,
    IReadOnlyList<string> Designations,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> FeeHeads,
    IReadOnlyList<string> PaymentModes,
    IReadOnlyList<string> PaymentStatuses,
    IReadOnlyList<string> AttendanceStatuses,
    IReadOnlyList<string> Routes,
    IReadOnlyList<string> Vehicles);

public sealed record SchoolReportEmailRequest(
    SchoolReportQuery Query,
    IReadOnlyList<string> Recipients,
    string? Subject,
    string? Message,
    string? Format);

public sealed record SchoolReportEmailResult(string Email, string Status, string? Detail);

public sealed record SaveReportScheduleRequest(
    Guid? Id,
    string ReportKey,
    string Frequency,
    string SendTime,
    int? DayOfWeek,
    int? DayOfMonth,
    int? OffsetMinutes,
    string? Format,
    IReadOnlyList<string> Recipients,
    string? Subject,
    string? Message,
    SchoolReportFilters? Filters);

public sealed record SchoolReportSchedule(
    Guid Id,
    string ReportKey,
    string Title,
    string Frequency,
    string SendTime,
    int? DayOfWeek,
    int? DayOfMonth,
    int UtcOffsetMinutes,
    string Format,
    string Recipients,
    string? Subject,
    string? Message,
    string FiltersJson,
    string Status,
    DateTime NextRunAt,
    DateTime? LastRunAt);

public sealed record SchoolReportDue(Guid Id, Guid TenantId);

public sealed record SchoolReportScheduleRun(
    Guid Id,
    Guid ScheduleId,
    DateTime StartedAt,
    DateTime? FinishedAt,
    string Status,
    string? Detail,
    DateTime? FromDate,
    DateTime? ToDate);
