namespace Sms.Modules.Academics.Contracts;

// ---- Class ----
// NextPeriod is a trailing init-only property with a secondary 10-param constructor, not a
// primary-constructor parameter: Dapper materializes via a constructor matching the exact
// column count of whatever query ran, and Class_Create/Class_Update still return the
// original 9 columns while ListAsync/GetAsync now return 10 (+ NextPeriod).
public sealed record ClassResponse(
    Guid Id, Guid TenantId, string Name, string? Grade, string? Section, string? Subject,
    string? Room, int StudentCount, Guid? ClassTeacherId)
{
    public string? NextPeriod { get; init; }

    public ClassResponse(
        Guid Id, Guid TenantId, string Name, string? Grade, string? Section, string? Subject,
        string? Room, int StudentCount, Guid? ClassTeacherId, string? NextPeriod)
        : this(Id, TenantId, Name, Grade, Section, Subject, Room, StudentCount, ClassTeacherId) =>
        this.NextPeriod = NextPeriod;
    public IReadOnlyList<string> Subjects { get; init; } = [];
}
public sealed record CreateClassRequest(
    string Name, string? Grade, string? Section, string? Subject, string? Room, Guid? ClassTeacherId,
    IReadOnlyList<string>? Subjects = null);

public sealed record UpdateClassRequest(
    string? Name = null,
    string? Grade = null,
    string? Section = null,
    string? Subject = null,
    string? Room = null,
    Guid? ClassTeacherId = null,
    bool ClearClassTeacher = false,
    IReadOnlyList<string>? Subjects = null);

public sealed record ReplaceClassSubjectsRequest(IReadOnlyList<string>? Subjects = null);

public sealed record ClassPeriodOverrideResponse(Guid ClassId, int PeriodCount);

public sealed record SetClassPeriodOverrideRequest(int? PeriodCount);

// ---- Subject ----
// TeacherName is a trailing init-only property with a secondary constructor, not a primary-
// constructor parameter: Dapper materializes via a constructor matching the exact column count
// of whatever query ran, and Subject_Create/Update/Get still return the original 6 columns
// while ListAsync now returns 7 (+ TeacherName).
public sealed record SubjectResponse(Guid Id, Guid TenantId, string Name, string? Short, Guid? TeacherId, string? Color)
{
    public string? TeacherName { get; init; }

    public SubjectResponse(
        Guid Id, Guid TenantId, string Name, string? Short, Guid? TeacherId, string? Color, string? TeacherName)
        : this(Id, TenantId, Name, Short, TeacherId, Color) =>
        this.TeacherName = TeacherName;
}
public sealed record CreateSubjectRequest(string Name, string? Short, Guid? TeacherId, string? Color);
public sealed record UpdateSubjectRequest(
    string? Name = null,
    string? Short = null,
    Guid? TeacherId = null,
    string? Color = null,
    bool ClearTeacher = false);

// ---- Attendance (roll-call) ----
public sealed record AttendanceRecordResponse(
    Guid Id, Guid TenantId, Guid ClassId, Guid StudentId, DateTime Date, string Status, Guid? MarkedBy);
public sealed record AttendanceUpsertRow(Guid StudentId, string Status);
public sealed record BulkAttendanceRequest(DateTime Date, IReadOnlyList<AttendanceUpsertRow> Records);
/// Lightweight day-mark used by the absence-alert worker to compute streaks.
public sealed record AttendanceMarkRow(Guid StudentId, DateTime Date, string Status);

// ---- Staff/teacher attendance (admin roll-call for someone else, not self check-in) ----
public sealed record StaffAttendanceRecordResponse(
    Guid Id, Guid TenantId, string PersonType, Guid PersonId, DateTime Date, string Status, Guid? MarkedBy);
public sealed record StaffAttendanceUpsertRow(Guid PersonId, string Status);
public sealed record BulkStaffAttendanceRequest(
    string PersonType, DateTime Date, IReadOnlyList<StaffAttendanceUpsertRow> Records);

// ---- Exam attendance (roll-call for one exam paper — the paper already carries its own date) ----
public sealed record ExamAttendanceRecordResponse(
    Guid Id, Guid TenantId, Guid ExamPaperId, Guid StudentId, string Status, Guid? MarkedBy);
public sealed record ExamAttendanceUpsertRow(Guid StudentId, string Status);
public sealed record BulkExamAttendanceRequest(IReadOnlyList<ExamAttendanceUpsertRow> Records);

public sealed record AttendanceRollCallResponse(
    DateTime Date,
    string Day,
    int? Period,
    string? Subject,
    string? StartTime,
    string? EndTime,
    Guid? TeacherId,
    string? TeacherName,
    Guid? ClassTeacherId,
    string? ClassTeacherName,
    bool CanMark,
    string Reason,
    bool Marked);

// ---- Period attendance (timetable subject + period; independent of daily AttendanceRecords) ----
public sealed record PeriodAttendanceRecordResponse(
    Guid Id, Guid TenantId, Guid ClassId, Guid StudentId, DateTime Date,
    int Period, Guid? PeriodId, string Subject, Guid? SubjectId,
    string Status, Guid? MarkedBy, string? MarkedByRole);

public sealed record BulkPeriodAttendanceRequest(
    DateTime Date,
    int Period,
    string Subject,
    Guid? SubjectId,
    Guid? PeriodId,
    IReadOnlyList<AttendanceUpsertRow> Records,
    string? GeoFenceStatus = null,
    int? GeoDistanceMeters = null,
    DateTime? GeoCapturedAt = null);

public sealed record ClassDayTimetableSlotResponse(
    Guid Id,
    int Period,
    string? Subject,
    Guid? SubjectId,
    string? StartTime,
    string? EndTime,
    Guid? TeacherId,
    string? TeacherName,
    bool IsCurrent,
    bool Marked,
    bool CanMark);

/// <summary>
/// Official period-based attendance aggregate. Same shape for CRM / Teacher / Student / Parent.
/// <see cref="AttendancePercentage"/> is null when no periods are marked (not 0%).
/// <see cref="PresentTodayBadge"/> is UI-only (≥50% of today's marked periods); never use as official %.
/// </summary>
public sealed record PeriodAttendanceSummaryResponse(
    int TotalMarkedPeriods,
    int PresentPeriods,
    int LatePeriods,
    int AbsentPeriods,
    int LeavePeriods,
    decimal? AttendancePercentage,
    bool? PresentTodayBadge);

public sealed record PeriodAttendanceAdvancedRow(
    Guid Id,
    Guid ClassId,
    string Grade,
    string Section,
    string ClassLabel,
    Guid StudentId,
    string StudentName,
    string AdmissionNo,
    DateTime Date,
    int Period,
    Guid? PeriodId,
    string Subject,
    Guid? SubjectId,
    string? StartTime,
    string? EndTime,
    string Status,
    Guid? AssignedTeacherId,
    string? AssignedTeacherName,
    Guid? MarkedBy,
    string? MarkedByName,
    string? MarkedByRole,
    DateTime? MarkedAt,
    string GeoFenceStatus,
    int? GeoDistanceMeters,
    DateTime? GeoCapturedAt,
    Guid? UpdatedBy,
    string? UpdatedByName,
    string? UpdatedByRole,
    DateTime? UpdatedAt);

public sealed record PeriodAttendanceAdvancedPage(
    IReadOnlyList<PeriodAttendanceAdvancedRow> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record PeriodAttendanceAdvancedQuery(
    DateOnly From,
    DateOnly To,
    Guid? ClassId,
    string? Grade,
    string? Section,
    string? Subject,
    int? Period,
    Guid? AssignedTeacherId,
    Guid? MarkedBy,
    string? MarkedByRole,
    string? Status,
    string? Q,
    int Page,
    int PageSize,
    Guid? AuthorizedTeacherId = null,
    string? GeoFenceStatus = null);

public sealed record PeriodAttendanceAuditRow(
    Guid Id,
    Guid RecordId,
    Guid ClassId,
    Guid StudentId,
    DateTime Date,
    int Period,
    string Subject,
    string? FromStatus,
    string ToStatus,
    Guid? ActorId,
    string? ActorName,
    string? ActorRole,
    DateTime At);

public sealed record AdvClassDaySummary(
    int TotalStudents, int Present, int Absent, int Late, int Leave, int NotMarked,
    decimal? AttendancePercentage,
    int TotalPeriods, int MarkedPeriods, int PendingPeriods);

public sealed record AdvSubjectSummaryRow(
    string Subject, string? TeacherName, int Periods, int Marked, int Pending,
    int Present, int Absent, int Late, decimal? AttendancePercentage);

public sealed record AdvTeacherSummaryRow(
    Guid TeacherId, string TeacherName,
    int Classes, int Sections, int Subjects,
    int ExpectedPeriods, int MarkedPeriods, int PendingPeriods,
    int TeacherMarked, int StaffMarked, int PrincipalMarked, int AdminMarked);

public sealed record AdvRangeRollup(
    int TotalMarkedPeriods, int Present, int Absent, int Late, int Leave,
    decimal? AttendancePercentage);
