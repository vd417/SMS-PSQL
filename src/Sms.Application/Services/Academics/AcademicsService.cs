using System.Security.Claims;
using Sms.Application.Common;
using Sms.Application.Services.Realtime;
using Sms.Application.Services.Sis;
using Sms.Modules.Academics;
using Sms.Modules.Academics.Contracts;
using Sms.Modules.Academics.Data;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Results;
using Sms.Shared.Kernel.Tenancy;
using Sms.Shared.Kernel.Time;

namespace Sms.Application.Services.Academics;

public sealed class AcademicsService(
    ClassRepository classes,
    SubjectRepository subjects,
    ClassSubjectRepository classSubjects,
    SchoolHouseRepository schoolHouses,
    AttendanceRepository attendance,
    PeriodAttendanceRepository periodAttendance,
    PeriodAttendanceQueryRepository periodAttendanceQuery,
    IAttendanceViewPermissionService attendanceViewPermissions,
    StaffAttendanceRepository staffAttendance,
    ExamRepository exams,
    HomeworkRepository homework,
    AchievementRepository achievements,
    TimetableRepository timetable,
    CalendarRepository calendar,
    AcademicPublishRepository academicPublish,
    PersonExtrasRepository personExtras,
    LibraryRepository library,
    AssignmentRepository assignments,
    AcademicsCommsNotifier academicsNotifier,
    ISisService sis,
    ITenantContext tenant,
    ITenantFeatureSet features,
    IClock clock,
    ILiveBroadcaster live) : IAcademicsService
{
    private static string? NormPersonType(string? t)
    {
        var v = (t ?? "").Trim().ToLowerInvariant();
        return v is "teacher" or "staff" ? v : null;
    }

    private static bool IsLeadership(ClaimsPrincipal caller) =>
        caller.FindAll("role")
            .Select(c => c.Value.Split('.').LastOrDefault()?.Replace('-', '_'))
            .Any(r => r is "principal" or "admin" or "owner" or "vice_principal");

    private static bool IsStudentOrParent(ClaimsPrincipal caller) =>
        caller.FindAll("role")
            .Select(c => c.Value.Trim().ToLowerInvariant())
            .Any(r => r == Policies.StudentOrParent ||
                r.Split('.').LastOrDefault() is "student" or "parent");

    /// SD-5: exam paper PATCH/DELETE is creator-or-principal, deliberately narrower than
    /// IsLeadership (which also treats admin/owner as leadership) — the approved rule names
    /// principal specifically.
    private static bool IsPrincipal(ClaimsPrincipal caller) =>
        caller.FindAll("role").Any(c => c.Value.Trim().Equals(Policies.Principal, StringComparison.OrdinalIgnoreCase));

    private static Guid? CallerUserId(ClaimsPrincipal caller) =>
        Guid.TryParse(caller.FindFirst("sub")?.Value, out var id) ? id : null;

    private static DateOnly SchoolToday(DateTime utcNow) =>
        DateOnly.FromDateTime(SchoolClock.ToSchoolLocal(utcNow));

    private async Task<ApiResult<T>?> DenyIfNotOwnStudentAsync<T>(
        Guid studentId, ClaimsPrincipal caller, CancellationToken ct)
    {
        if (!IsStudentOrParent(caller)) return null;
        if (await sis.IsLinkedToCallerAsync(studentId, ct)) return null;
        return ApiResult<T>.Fail(
            new Error("not_own_student", "students and parents can only read their linked student's attendance"),
            403);
    }

    // Only narrows for a caller whose SOLE role is "teacher" — principal/admin/owner tokens
    // (however else combined) keep seeing every class, matching sms-admin's and the
    // principal screens' existing expectation of an unscoped list.
    public async Task<ApiResult<IReadOnlyList<ClassResponse>>> ListClassesAsync(
        ClaimsPrincipal caller, CancellationToken ct = default)
    {
        var roles = caller.FindAll("role").Select(c => c.Value.Split('.').LastOrDefault()).ToArray();
        var isTeacherOnly = roles.Contains("teacher") &&
            !roles.Any(r => r is "principal" or "admin" or "owner");
        IReadOnlyList<ClassResponse> rows;
        if (!isTeacherOnly)
            rows = await classes.ListAsync(ct);
        else
        {
            var sub = caller.FindFirst("sub")?.Value;
            if (sub is null || !Guid.TryParse(sub, out var userId))
                return ApiResult<IReadOnlyList<ClassResponse>>.Fail(new Error("unauthorized", "unauthorized"), 401);
            rows = await classes.ListForTeacherAsync(userId, ct);
        }
        return ApiResult<IReadOnlyList<ClassResponse>>.Ok(await AttachSubjectsAsync(rows, ct));
    }

    public async Task<ApiResult<ClassResponse>> GetClassAsync(Guid id, CancellationToken ct = default)
    {
        var c = await classes.GetAsync(id, ct);
        return c is null
            ? ApiResult<ClassResponse>.Fail(new Error("not_found", "resource not found"), 404)
            : ApiResult<ClassResponse>.Ok(await AttachSubjectsAsync(c, ct));
    }

    public async Task<ApiResult<ClassResponse>> CreateClassAsync(CreateClassRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<ClassResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        var created = (await classes.CreateAsync(tid, req, ct))!;
        if (req.Subjects is { Count: > 0 })
            await classSubjects.ReplaceAsync(tid, created.Id, req.Subjects, ct);
        return ApiResult<ClassResponse>.Ok(await AttachSubjectsAsync(created, ct), 201);
    }

    public async Task<ApiResult<ClassResponse>> UpdateClassAsync(Guid id, UpdateClassRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<ClassResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        if (await classes.GetAsync(id, ct) is null)
            return ApiResult<ClassResponse>.Fail(new Error("not_found", "resource not found"), 404);
        var updated = await classes.UpdateAsync(id, tid, req, ct);
        if (updated is null)
            return ApiResult<ClassResponse>.Fail(new Error("not_found", "resource not found"), 404);
        if (req.Subjects is not null)
            await classSubjects.ReplaceAsync(tid, id, req.Subjects, ct);
        return ApiResult<ClassResponse>.Ok(await AttachSubjectsAsync(updated, ct));
    }

    public async Task<ApiResult<IReadOnlyList<string>>> ListClassSubjectsAsync(Guid classId, CancellationToken ct = default)
    {
        if (await classes.GetAsync(classId, ct) is null)
            return ApiResult<IReadOnlyList<string>>.Fail(new Error("not_found", "resource not found"), 404);
        return ApiResult<IReadOnlyList<string>>.Ok(await classSubjects.ListNamesAsync(classId, ct));
    }

    public async Task<ApiResult<IReadOnlyList<string>>> ReplaceClassSubjectsAsync(
        Guid classId, IReadOnlyList<string> names, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<IReadOnlyList<string>>.Fail(new Error("forbidden", "no tenant context"), 403);
        if (await classes.GetAsync(classId, ct) is null)
            return ApiResult<IReadOnlyList<string>>.Fail(new Error("not_found", "resource not found"), 404);
        var saved = await classSubjects.ReplaceAsync(tid, classId, names, ct);
        return ApiResult<IReadOnlyList<string>>.Ok(saved);
    }

    public async Task<ApiResult<IReadOnlyList<string>>> ListSchoolHousesAsync(CancellationToken ct = default) =>
        ApiResult<IReadOnlyList<string>>.Ok(await schoolHouses.ListNamesAsync(ct));

    public async Task<ApiResult<IReadOnlyList<string>>> ReplaceSchoolHousesAsync(
        IReadOnlyList<string> names, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<IReadOnlyList<string>>.Fail(new Error("forbidden", "no tenant context"), 403);
        var saved = await schoolHouses.ReplaceAsync(tid, names, ct);
        return ApiResult<IReadOnlyList<string>>.Ok(saved);
    }

    public async Task<ApiResult<IReadOnlyList<SubjectResponse>>> ListSubjectsAsync(CancellationToken ct = default) =>
        ApiResult<IReadOnlyList<SubjectResponse>>.Ok(await subjects.ListAsync(ct));

    public async Task<ApiResult<IReadOnlyList<SubjectResponse>>> ListSubjectsForStudentAsync(
        string? grade, string? section, string? classLabel, CancellationToken ct = default)
    {
        var catalog = await subjects.ListAsync(ct);
        var classRows = await classes.ListAsync(ct);
        var slots = await timetable.ListAsync(ct);
        var classIds = StudentClassScope.MatchingClassIds(classRows, grade, section, classLabel);
        var mapped = classIds.Count == 0
            ? (IReadOnlyList<string>)[]
            : (await classSubjects.ListForClassesAsync(classIds, ct)).Select(r => r.Name).ToList();
        return ApiResult<IReadOnlyList<SubjectResponse>>.Ok(
            StudentClassScope.SubjectsForStudent(catalog, classRows, slots, grade, section, classLabel, mapped));
    }

    public async Task<ApiResult<SubjectResponse>> GetSubjectAsync(Guid id, CancellationToken ct = default)
    {
        var s = await subjects.GetAsync(id, ct);
        return s is null
            ? ApiResult<SubjectResponse>.Fail(new Error("not_found", "resource not found"), 404)
            : ApiResult<SubjectResponse>.Ok(s);
    }

    public async Task<ApiResult<SubjectResponse>> CreateSubjectAsync(CreateSubjectRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<SubjectResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        return ApiResult<SubjectResponse>.Ok((await subjects.CreateAsync(tid, req, ct))!, 201);
    }

    public async Task<ApiResult<SubjectResponse>> UpdateSubjectAsync(Guid id, UpdateSubjectRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<SubjectResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        if (await subjects.GetAsync(id, ct) is null)
            return ApiResult<SubjectResponse>.Fail(new Error("not_found", "resource not found"), 404);
        var updated = await subjects.UpdateAsync(id, tid, req, ct);
        return updated is null
            ? ApiResult<SubjectResponse>.Fail(new Error("not_found", "resource not found"), 404)
            : ApiResult<SubjectResponse>.Ok(updated);
    }

    public async Task<ApiResult> DeleteSubjectAsync(Guid id, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult.Fail(new Error("forbidden", "no tenant context"), 403);
        if (await subjects.GetAsync(id, ct) is null)
            return ApiResult.Fail(new Error("not_found", "resource not found"), 404);
        await subjects.DeleteAsync(id, tid, ct);
        return ApiResult.NoContent();
    }

    public async Task<ApiResult<IReadOnlyList<AttendanceRecordResponse>>> ListAttendanceAsync(
        Guid classId, DateTime date, CancellationToken ct = default) =>
        ApiResult<IReadOnlyList<AttendanceRecordResponse>>.Ok(await attendance.ListAsync(classId, date, ct));

    public async Task<ApiResult<IReadOnlyList<AttendanceRecordResponse>>> ListAttendanceRangeAsync(
        Guid classId, DateTime from, DateTime to, CancellationToken ct = default) =>
        ApiResult<IReadOnlyList<AttendanceRecordResponse>>.Ok(
            await attendance.ListRangeAsync(classId, from, to, ct));

    public async Task<ApiResult> BulkUpsertAttendanceAsync(
        Guid classId, BulkAttendanceRequest req, ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult.Fail(new Error("forbidden", "no tenant context"), 403);

        var classRow = await classes.GetAsync(classId, ct);
        if (classRow is null)
            return ApiResult.Fail(new Error("not_found", "resource not found"), 404);

        var day = AttendanceRollCall.NormalizeDayKey(AttendanceRollCall.DayKey(req.Date));
        var slotRows = await timetable.ListForClassDayAsync(classId, day, ct);
        var firstSlot = AttendanceRollCall.FirstTeachingSlot(slotRows.Select(s =>
            new AttendanceRollCall.SlotInput(
                day, s.Period, s.Subject, classId, s.TeacherId,
                s.StartTime, s.EndTime, s.TeacherName)));
        var callerTeacherId = tenant.UserId is { } userId
            ? await classes.TeacherIdForUserAsync(userId, ct)
            : null;
        if (!AttendanceRollCall.CanMark(
                IsLeadership(caller), callerTeacherId, classRow.ClassTeacherId, firstSlot?.TeacherId))
        {
            return ApiResult.Fail(
                new Error(
                    "not_roll_call_teacher",
                    "only the class teacher, first-period teacher, or leadership can mark this day"),
                403);
        }

        await attendance.BulkUpsertAsync(tid, classId, req.Date, tenant.UserId, req.Records, ct);
        await live.PublishAsync(tid, LiveEventTypes.Attendance, ct: ct);
        try
        {
            await academicsNotifier.NotifyAbsenceMarksAsync(tid, req.Date, req.Records, ct: ct);
        }
        catch
        {
            // Parent notice is best-effort; the roll is already saved.
        }
        return ApiResult.NoContent();
    }

    public async Task<ApiResult<AttendanceRollCallResponse>> GetAttendanceRollCallAsync(
        Guid classId, DateTime date, ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (tenant.TenantId is null)
            return ApiResult<AttendanceRollCallResponse>.Fail(
                new Error("forbidden", "no tenant context"), 403);

        var classRow = await classes.GetAsync(classId, ct);
        if (classRow is null)
            return ApiResult<AttendanceRollCallResponse>.Fail(
                new Error("not_found", "resource not found"), 404);

        var day = AttendanceRollCall.NormalizeDayKey(AttendanceRollCall.DayKey(date));
        var slotRows = await timetable.ListForClassDayAsync(classId, day, ct);
        var firstSlot = AttendanceRollCall.FirstTeachingSlot(slotRows.Select(s =>
            new AttendanceRollCall.SlotInput(
                day, s.Period, s.Subject, classId, s.TeacherId,
                s.StartTime, s.EndTime, s.TeacherName)));
        var callerTeacherId = tenant.UserId is { } userId
            ? await classes.TeacherIdForUserAsync(userId, ct)
            : null;
        var leadership = IsLeadership(caller);
        var canMark = AttendanceRollCall.CanMark(
            leadership, callerTeacherId, classRow.ClassTeacherId, firstSlot?.TeacherId);
        var reason = AttendanceRollCall.Reason(
            leadership, callerTeacherId, classRow.ClassTeacherId, firstSlot?.TeacherId);
        var classTeacherName = classRow.ClassTeacherId is { } classTeacherId
            ? await classes.TeacherNameAsync(classTeacherId, ct)
            : null;
        var marked = (await attendance.ListAsync(classId, date, ct)).Count > 0;

        return ApiResult<AttendanceRollCallResponse>.Ok(new AttendanceRollCallResponse(
            date.Date,
            day,
            firstSlot?.Period,
            firstSlot?.Subject,
            firstSlot?.StartTime,
            firstSlot?.EndTime,
            firstSlot?.TeacherId,
            firstSlot?.TeacherName,
            classRow.ClassTeacherId,
            classTeacherName,
            canMark,
            reason,
            marked));
    }

    public async Task<ApiResult<IReadOnlyList<AttendanceRecordResponse>>> ListAttendanceForStudentAsync(
        Guid studentId, DateTime from, DateTime to, ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (await DenyIfNotOwnStudentAsync<IReadOnlyList<AttendanceRecordResponse>>(studentId, caller, ct) is { } denied)
            return denied;

        return ApiResult<IReadOnlyList<AttendanceRecordResponse>>.Ok(
            await attendance.ListForStudentAsync(studentId, from, to, ct));
    }

    public async Task<ApiResult<IReadOnlyList<ClassDayTimetableSlotResponse>>> ListClassDayTimetableAsync(
        Guid classId, DateTime date, ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (tenant.TenantId is null)
            return ApiResult<IReadOnlyList<ClassDayTimetableSlotResponse>>.Fail(
                new Error("forbidden", "no tenant context"), 403);
        var classRow = await classes.GetAsync(classId, ct);
        if (classRow is null)
            return ApiResult<IReadOnlyList<ClassDayTimetableSlotResponse>>.Fail(
                new Error("not_found", "resource not found"), 404);

        var day = AttendanceRollCall.NormalizeDayKey(AttendanceRollCall.DayKey(date));
        var slots = await timetable.ListForClassDayAsync(classId, day, ct);
        var teaching = slots
            .Where(s => !AttendanceRollCall.IsNonTeaching(s.Subject))
            .OrderBy(s => s.Period)
            .ToList();
        var markedRows = await periodAttendance.ListForClassDayAsync(classId, date, ct);
        var marked = markedRows
            .Select(r => (r.Period, Subject: r.Subject))
            .ToHashSet();
        // Timetable times are school-local wall clock. Prefer Asia/Kolkata (product default)
        // until tenants store an explicit timezone; never compare local periods to UTC blindly.
        var localNow = ToSchoolLocal(clock.UtcNow);
        var nowMins = date.Date == localNow.Date
            ? localNow.Hour * 60 + localNow.Minute
            : (int?)null;

        var leadership = IsLeadership(caller);
        var callerTeacherId = tenant.UserId is { } userId
            ? await classes.TeacherIdForUserAsync(userId, ct)
            : null;
        var isClassTeacher = classRow.ClassTeacherId is { } ctId && callerTeacherId == ctId;

        var list = teaching.Select(s =>
        {
            var subj = (s.Subject ?? "").Trim();
            var isCurrent = nowMins is { } mins
                && TryParseHm(s.StartTime, out var start)
                && TryParseHm(s.EndTime, out var end)
                && mins >= start && mins < end;
            var canMark = leadership
                || isClassTeacher
                || (s.TeacherId is { } pt && callerTeacherId == pt);
            return new ClassDayTimetableSlotResponse(
                s.Id, s.Period, s.Subject, s.SubjectId, s.StartTime, s.EndTime,
                s.TeacherId, s.TeacherName, isCurrent,
                marked.Contains((s.Period, subj)), canMark);
        }).ToList();

        return ApiResult<IReadOnlyList<ClassDayTimetableSlotResponse>>.Ok(list);
    }

    public async Task<ApiResult<IReadOnlyList<PeriodAttendanceRecordResponse>>> ListPeriodAttendanceAsync(
        Guid classId, DateTime date, int period, string subject, CancellationToken ct = default)
    {
        if (tenant.TenantId is null)
            return ApiResult<IReadOnlyList<PeriodAttendanceRecordResponse>>.Fail(
                new Error("forbidden", "no tenant context"), 403);
        if (await classes.GetAsync(classId, ct) is null)
            return ApiResult<IReadOnlyList<PeriodAttendanceRecordResponse>>.Fail(
                new Error("not_found", "resource not found"), 404);
        var subj = (subject ?? "").Trim();
        if (period < 1 || subj.Length == 0)
            return ApiResult<IReadOnlyList<PeriodAttendanceRecordResponse>>.Fail(
                new Error("invalid_request", "period and subject are required"), 400);

        return ApiResult<IReadOnlyList<PeriodAttendanceRecordResponse>>.Ok(
            await periodAttendance.ListAsync(classId, date, period, subj, ct));
    }

    private sealed record AttendanceQueryScope(
        Guid? AuthorizedTeacherId,
        Guid? TeacherUserId,
        Error? Error);

    private async Task<AttendanceQueryScope> AuthorizeAttendanceQueryAsync(
        ClaimsPrincipal caller,
        CancellationToken ct)
    {
        if (tenant.TenantId is null)
            return new(null, null, new Error("forbidden", "no tenant context"));
        if (!await attendanceViewPermissions.CanViewAsync(caller, ct))
            return new(
                null,
                null,
                new Error("forbidden", "attendance view permission is required"));

        var roles = caller.FindAll("role")
            .Select(c => c.Value.Trim().ToLowerInvariant().Split('.').LastOrDefault()?.Replace('-', '_'))
            .ToArray();
        var leadership = roles.Any(r => r is "principal" or "admin" or "owner" or "vice_principal");
        if (!roles.Contains("teacher") || leadership)
            return new(null, null, null);

        var teacherUserId = tenant.UserId;
        var teacherId = teacherUserId is { } userId
            ? await classes.TeacherIdForUserAsync(userId, ct)
            : null;
        return teacherId is null
            ? new(
                null,
                teacherUserId,
                new Error("forbidden", "teacher profile is required to list period attendance records"))
            : new(teacherId, teacherUserId, null);
    }

    private async Task<bool> CanQueryClassAsync(
        AttendanceQueryScope scope,
        Guid classId,
        CancellationToken ct)
    {
        if (scope.AuthorizedTeacherId is null) return true;
        if (scope.TeacherUserId is not { } teacherUserId) return false;
        return (await classes.ListForTeacherAsync(teacherUserId, ct)).Any(row => row.Id == classId);
    }

    public async Task<ApiResult<PeriodAttendanceAdvancedPage>> ListPeriodAttendanceAdvancedAsync(
        ClaimsPrincipal caller,
        string? preset,
        DateOnly? from,
        DateOnly? to,
        Guid? classId,
        string? grade,
        string? section,
        string? subject,
        int? period,
        Guid? assignedTeacherId,
        Guid? markedBy,
        string? markedByRole,
        string? status,
        string? q,
        int page = 1,
        int pageSize = 25,
        string? geoFenceStatus = null,
        CancellationToken ct = default)
    {
        var scope = await AuthorizeAttendanceQueryAsync(caller, ct);
        if (scope.Error is { } authorizationError)
            return ApiResult<PeriodAttendanceAdvancedPage>.Fail(
                authorizationError, 403);

        var today = SchoolToday(clock.UtcNow);
        var range = PeriodAttendanceDatePresets.Resolve(preset, from, to, today);
        var query = new PeriodAttendanceAdvancedQuery(
            range.From,
            range.To,
            classId,
            grade,
            section,
            subject,
            period,
            assignedTeacherId,
            markedBy,
            markedByRole,
            status,
            q,
            page,
            pageSize,
            scope.AuthorizedTeacherId,
            geoFenceStatus);

        return ApiResult<PeriodAttendanceAdvancedPage>.Ok(
            await periodAttendanceQuery.SearchAsync(query, ct));
    }

    public async Task<ApiResult<IReadOnlyList<PeriodAttendanceAuditRow>>> GetPeriodAttendanceAuditAsync(
        Guid recordId,
        ClaimsPrincipal caller,
        CancellationToken ct = default)
    {
        var scope = await AuthorizeAttendanceQueryAsync(caller, ct);
        if (scope.Error is { } authorizationError)
            return ApiResult<IReadOnlyList<PeriodAttendanceAuditRow>>.Fail(authorizationError, 403);

        return ApiResult<IReadOnlyList<PeriodAttendanceAuditRow>>.Ok(
            await periodAttendanceQuery.GetAuditAsync(recordId, ct));
    }

    public async Task<ApiResult<AdvClassDaySummary>> GetPeriodAttendanceClassDaySummaryAsync(
        ClaimsPrincipal caller,
        Guid classId,
        DateOnly date,
        CancellationToken ct = default)
    {
        var scope = await AuthorizeAttendanceQueryAsync(caller, ct);
        if (scope.Error is { } authorizationError)
            return ApiResult<AdvClassDaySummary>.Fail(authorizationError, 403);
        if (!await CanQueryClassAsync(scope, classId, ct))
            return ApiResult<AdvClassDaySummary>.Fail(
                new Error("forbidden", "teacher is not authorized for this class"), 403);

        return ApiResult<AdvClassDaySummary>.Ok(
            await periodAttendanceQuery.SummarizeClassDayAsync(classId, date, ct));
    }

    public async Task<ApiResult<IReadOnlyList<AdvSubjectSummaryRow>>> ListPeriodAttendanceSubjectSummariesAsync(
        ClaimsPrincipal caller,
        Guid classId,
        string? preset,
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct = default)
    {
        var scope = await AuthorizeAttendanceQueryAsync(caller, ct);
        if (scope.Error is { } authorizationError)
            return ApiResult<IReadOnlyList<AdvSubjectSummaryRow>>.Fail(authorizationError, 403);
        if (!await CanQueryClassAsync(scope, classId, ct))
            return ApiResult<IReadOnlyList<AdvSubjectSummaryRow>>.Fail(
                new Error("forbidden", "teacher is not authorized for this class"), 403);

        var range = PeriodAttendanceDatePresets.Resolve(
            preset, from, to, SchoolToday(clock.UtcNow));
        return ApiResult<IReadOnlyList<AdvSubjectSummaryRow>>.Ok(
            await periodAttendanceQuery.SummarizeSubjectsAsync(
                classId, range.From, range.To, ct));
    }

    public async Task<ApiResult<IReadOnlyList<AdvTeacherSummaryRow>>> ListPeriodAttendanceTeacherSummariesAsync(
        ClaimsPrincipal caller,
        string? preset,
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct = default)
    {
        var scope = await AuthorizeAttendanceQueryAsync(caller, ct);
        if (scope.Error is { } authorizationError)
            return ApiResult<IReadOnlyList<AdvTeacherSummaryRow>>.Fail(authorizationError, 403);

        var range = PeriodAttendanceDatePresets.Resolve(
            preset, from, to, SchoolToday(clock.UtcNow));
        var rows = await periodAttendanceQuery.SummarizeTeachersAsync(
            range.From, range.To, ct);
        if (scope.AuthorizedTeacherId is { } teacherId)
            rows = rows.Where(row => row.TeacherId == teacherId).ToList();
        return ApiResult<IReadOnlyList<AdvTeacherSummaryRow>>.Ok(rows);
    }

    public async Task<ApiResult<AdvRangeRollup>> GetPeriodAttendanceRangeSummaryAsync(
        ClaimsPrincipal caller,
        string? preset,
        DateOnly? from,
        DateOnly? to,
        Guid? classId,
        string? grade,
        string? section,
        Guid? studentId,
        string? subject,
        Guid? teacherId,
        CancellationToken ct = default)
    {
        var scope = await AuthorizeAttendanceQueryAsync(caller, ct);
        if (scope.Error is { } authorizationError)
            return ApiResult<AdvRangeRollup>.Fail(authorizationError, 403);

        var range = PeriodAttendanceDatePresets.Resolve(
            preset, from, to, SchoolToday(clock.UtcNow));
        var effectiveTeacherId = scope.AuthorizedTeacherId ?? teacherId;
        return ApiResult<AdvRangeRollup>.Ok(
            await periodAttendanceQuery.SummarizeRangeAsync(
                range.From,
                range.To,
                classId,
                grade,
                section,
                studentId,
                subject,
                effectiveTeacherId,
                ct));
    }

    public async Task<ApiResult> BulkUpsertPeriodAttendanceAsync(
        Guid classId, BulkPeriodAttendanceRequest req, ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult.Fail(new Error("forbidden", "no tenant context"), 403);
        var classRow = await classes.GetAsync(classId, ct);
        if (classRow is null)
            return ApiResult.Fail(new Error("not_found", "resource not found"), 404);

        var subj = (req.Subject ?? "").Trim();
        if (req.Period < 1 || subj.Length == 0 || req.Records is null || req.Records.Count == 0)
            return ApiResult.Fail(new Error("invalid_request", "period, subject, and records are required"), 400);

        var day = AttendanceRollCall.NormalizeDayKey(AttendanceRollCall.DayKey(req.Date));
        var slots = await timetable.ListForClassDayAsync(classId, day, ct);
        var slot = slots.FirstOrDefault(s =>
            s.Period == req.Period
            && string.Equals((s.Subject ?? "").Trim(), subj, StringComparison.OrdinalIgnoreCase)
            && !AttendanceRollCall.IsNonTeaching(s.Subject));
        if (slot is null)
            return ApiResult.Fail(new Error("invalid_period", "period/subject not on this class timetable for that day"), 400);

        var callerTeacherId = tenant.UserId is { } userId
            ? await classes.TeacherIdForUserAsync(userId, ct)
            : null;
        var leadership = IsLeadership(caller);
        var periodTeacher = slot.TeacherId;
        var allowed = leadership
            || (classRow.ClassTeacherId is { } ctId && callerTeacherId == ctId)
            || (periodTeacher is { } pt && callerTeacherId == pt);
        if (!allowed)
            return ApiResult.Fail(
                new Error("forbidden", "only the period teacher, class teacher, or leadership can mark this period"),
                403);

        var role = caller.FindAll("role").Select(c => c.Value).FirstOrDefault();
        var subjectId = req.SubjectId ?? slot.SubjectId;
        var periodId = req.PeriodId ?? slot.Id;
        await periodAttendance.BulkUpsertAsync(
            tid, classId, req.Date, req.Period, subj, periodId, subjectId,
            tenant.UserId, role, req.Records, ct,
            req.GeoFenceStatus, req.GeoDistanceMeters, req.GeoCapturedAt);
        await live.PublishAsync(tid, LiveEventTypes.Attendance, ct: ct);
        try
        {
            await academicsNotifier.NotifyAbsenceMarksAsync(
                tid, req.Date, req.Records, subj, req.Period, ct);
        }
        catch
        {
            // Parent notice is best-effort; the period mark is already saved.
        }
        return ApiResult.NoContent();
    }

    public async Task<ApiResult<IReadOnlyList<PeriodAttendanceRecordResponse>>> ListPeriodAttendanceForStudentAsync(
        Guid studentId, DateTime from, DateTime to, ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (await DenyIfNotOwnStudentAsync<IReadOnlyList<PeriodAttendanceRecordResponse>>(studentId, caller, ct) is { } denied)
            return denied;

        return ApiResult<IReadOnlyList<PeriodAttendanceRecordResponse>>.Ok(
            await periodAttendance.ListForStudentAsync(studentId, from, to, ct));
    }

    public async Task<ApiResult<PeriodAttendanceSummaryResponse>> GetPeriodAttendanceSummaryForStudentAsync(
        Guid studentId, DateTime from, DateTime to, ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (await DenyIfNotOwnStudentAsync<PeriodAttendanceSummaryResponse>(studentId, caller, ct) is { } denied)
            return denied;

        var today = DateTime.UtcNow.Date;
        return ApiResult<PeriodAttendanceSummaryResponse>.Ok(
            await periodAttendance.SummarizeForStudentAsync(studentId, from, to, today, ct));
    }

    public async Task<ApiResult<PeriodAttendanceSummaryResponse>> GetPeriodAttendanceSummaryForClassAsync(
        Guid classId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        return ApiResult<PeriodAttendanceSummaryResponse>.Ok(
            await periodAttendance.SummarizeForClassAsync(classId, from, to, today, ct));
    }

    private static bool TryParseHm(string? raw, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var m = System.Text.RegularExpressions.Regex.Match(raw.Trim(), @"^(\d{1,2}):(\d{2})");
        if (!m.Success) return false;
        minutes = int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value);
        return true;
    }

    private static DateTime ToSchoolLocal(DateTime utc)
    {
        var u = utc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(utc, DateTimeKind.Utc)
            : utc.ToUniversalTime();
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata");
            return TimeZoneInfo.ConvertTimeFromUtc(u, tz);
        }
        catch (TimeZoneNotFoundException)
        {
            return u.AddHours(5).AddMinutes(30);
        }
    }

    public async Task<ApiResult<IReadOnlyList<StaffAttendanceRecordResponse>>> ListStaffAttendanceAsync(
        string personType, DateTime date, CancellationToken ct = default)
    {
        var type = NormPersonType(personType);
        if (type is null)
            return ApiResult<IReadOnlyList<StaffAttendanceRecordResponse>>.Fail(
                new Error("invalid_request", "personType must be 'teacher' or 'staff'"), 400);
        if (type == "staff" && !FeatureGate.Allowed(tenant, features, FeatureCatalog.StaffSupport))
            return FeatureGate.Locked<IReadOnlyList<StaffAttendanceRecordResponse>>(FeatureCatalog.StaffSupport);
        return ApiResult<IReadOnlyList<StaffAttendanceRecordResponse>>.Ok(
            await staffAttendance.ListAsync(type, date, ct));
    }

    public async Task<ApiResult<IReadOnlyList<StaffAttendanceRecordResponse>>> ListStaffAttendanceRangeAsync(
        string personType, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var type = NormPersonType(personType);
        if (type is null)
            return ApiResult<IReadOnlyList<StaffAttendanceRecordResponse>>.Fail(
                new Error("invalid_request", "personType must be 'teacher' or 'staff'"), 400);
        if (to.Date < from.Date)
            return ApiResult<IReadOnlyList<StaffAttendanceRecordResponse>>.Fail(
                new Error("invalid_request", "to must be on or after from"), 400);
        if ((to.Date - from.Date).TotalDays > 366)
            return ApiResult<IReadOnlyList<StaffAttendanceRecordResponse>>.Fail(
                new Error("invalid_request", "date range cannot exceed 366 days"), 400);
        if (type == "staff" && !FeatureGate.Allowed(tenant, features, FeatureCatalog.StaffSupport))
            return FeatureGate.Locked<IReadOnlyList<StaffAttendanceRecordResponse>>(FeatureCatalog.StaffSupport);
        return ApiResult<IReadOnlyList<StaffAttendanceRecordResponse>>.Ok(
            await staffAttendance.ListRangeAsync(type, from, to, ct));
    }

    public async Task<ApiResult> BulkUpsertStaffAttendanceAsync(
        BulkStaffAttendanceRequest req, ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult.Fail(new Error("forbidden", "no tenant context"), 403);
        if (!IsLeadership(caller))
            return ApiResult.Fail(new Error("forbidden", "only owner, admin, or principal can mark teacher/staff attendance"), 403);
        var type = NormPersonType(req.PersonType);
        if (type is null)
            return ApiResult.Fail(new Error("invalid_request", "personType must be 'teacher' or 'staff'"), 400);
        if (type == "staff" && !FeatureGate.Allowed(tenant, features, FeatureCatalog.StaffSupport))
            return FeatureGate.Locked(FeatureCatalog.StaffSupport);
        if (req.Records is null || req.Records.Count == 0)
            return ApiResult.Fail(new Error("invalid_request", "records are required"), 400);

        var normalized = new List<StaffAttendanceUpsertRow>(req.Records.Count);
        foreach (var row in req.Records)
        {
            var status = StaffAttendanceStatus.Normalize(row.Status);
            if (status is null)
                return ApiResult.Fail(
                    new Error("invalid_request", "status must be present, absent, late, or half_day"), 400);
            normalized.Add(row with { Status = status });
        }

        await staffAttendance.BulkUpsertAsync(tid, type, req.Date, tenant.UserId, normalized, ct);
        await live.PublishAsync(tid, LiveEventTypes.Attendance, ct: ct);
        return ApiResult.NoContent();
    }

    public async Task<ApiResult<IReadOnlyList<StaffAttendanceRecordResponse>>> ListStaffAttendanceForPersonAsync(
        string personType, Guid personId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var type = NormPersonType(personType);
        if (type is null)
            return ApiResult<IReadOnlyList<StaffAttendanceRecordResponse>>.Fail(
                new Error("invalid_request", "personType must be 'teacher' or 'staff'"), 400);
        if (type == "staff" && !FeatureGate.Allowed(tenant, features, FeatureCatalog.StaffSupport))
            return FeatureGate.Locked<IReadOnlyList<StaffAttendanceRecordResponse>>(FeatureCatalog.StaffSupport);
        return ApiResult<IReadOnlyList<StaffAttendanceRecordResponse>>.Ok(
            await staffAttendance.ListForPersonAsync(type, personId, from, to, ct));
    }

    public async Task<ApiResult<IReadOnlyList<ExamResponse>>> ListExamsAsync(CancellationToken ct = default)
    {
        var list = await exams.ListExamsAsync(ct);
        var withClasses = new List<ExamResponse>(list.Count);
        foreach (var e in list)
        {
            var ids = await exams.ListExamClassIdsAsync(e.Id, ct);
            withClasses.Add(e with { ClassIds = ids });
        }
        return ApiResult<IReadOnlyList<ExamResponse>>.Ok(withClasses);
    }

    public async Task<ApiResult<ExamResponse>> GetExamAsync(Guid id, CancellationToken ct = default)
    {
        var e = await exams.GetExamAsync(id, ct);
        if (e is null)
            return ApiResult<ExamResponse>.Fail(new Error("not_found", "resource not found"), 404);
        var ids = await exams.ListExamClassIdsAsync(id, ct);
        return ApiResult<ExamResponse>.Ok(e with { ClassIds = ids });
    }

    public async Task<ApiResult<ExamResponse>> CreateExamAsync(CreateExamRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<ExamResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        var created = (await exams.CreateExamAsync(tid, req, ct))!;
        if (req.ClassIds is { Count: > 0 })
            await exams.ReplaceExamClassIdsAsync(tid, created.Id, req.ClassIds, ct);
        var ids = await exams.ListExamClassIdsAsync(created.Id, ct);
        return ApiResult<ExamResponse>.Ok(created with { ClassIds = ids }, 201);
    }

    public async Task<ApiResult<ExamResponse>> UpdateExamAsync(Guid id, UpdateExamRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<ExamResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        if (await exams.GetExamAsync(id, ct) is null)
            return ApiResult<ExamResponse>.Fail(new Error("not_found", "resource not found"), 404);
        var updated = (await exams.UpdateExamAsync(id, req, ct))!;
        if (req.ClassIds is not null)
            await exams.ReplaceExamClassIdsAsync(tid, id, req.ClassIds, ct);
        var ids = await exams.ListExamClassIdsAsync(id, ct);
        return ApiResult<ExamResponse>.Ok(updated with { ClassIds = ids });
    }

    public async Task<ApiResult<IReadOnlyList<ExamPaperResponse>>> ListExamPapersAsync(
        Guid? examId, CancellationToken ct = default) =>
        ApiResult<IReadOnlyList<ExamPaperResponse>>.Ok(await exams.ListExamPapersAsync(examId, ct));

    public async Task<ApiResult<IReadOnlyList<ExamPaperResponse>>> ListExamPapersForStudentAsync(
        Guid? examId, Guid studentId, CancellationToken ct = default)
    {
        var papers = await exams.ListExamPapersAsync(examId, ct);
        var student = await sis.GetStudentAsync(studentId, ct);
        if (!student.IsSuccess || student.Data is null)
            return ApiResult<IReadOnlyList<ExamPaperResponse>>.Ok(papers);
        var classRows = await classes.ListAsync(ct);
        var classIds = StudentClassScope.MatchingClassIds(
            classRows, student.Data.Grade, student.Data.Section, student.Data.ClassLabel);
        return ApiResult<IReadOnlyList<ExamPaperResponse>>.Ok(
            StudentClassScope.PapersForStudent(papers, classIds));
    }

    public async Task<ApiResult<ExamPaperResponse>> GetExamPaperAsync(Guid id, CancellationToken ct = default)
    {
        var p = await exams.GetExamPaperAsync(id, ct);
        return p is null
            ? ApiResult<ExamPaperResponse>.Fail(new Error("not_found", "resource not found"), 404)
            : ApiResult<ExamPaperResponse>.Ok(p);
    }

    public async Task<ApiResult<ExamPaperResponse>> CreateExamPaperAsync(
        CreateExamPaperRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<ExamPaperResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        var created = await exams.CreateExamPaperAsync(tid, req, tenant.UserId, ct);
        if (created is null)
            return ApiResult<ExamPaperResponse>.Fail(new Error("internal_error", "could not create exam paper"), 500);
        try
        {
            if (created.ExamId is null)
            {
                await academicsNotifier.NotifyClassTestScheduledAsync(
                    tid,
                    created.Name ?? req.Name,
                    created.Subject ?? req.Subject,
                    created.Date ?? req.Date,
                    created.ClassId ?? req.ClassId,
                    ct);
            }
        }
        catch
        {
            /* notification is best-effort; paper create already succeeded */
        }
        return ApiResult<ExamPaperResponse>.Ok(created, 201);
    }

    public async Task<ApiResult<ExamPaperResponse>> UpdateExamPaperAsync(
        Guid id, UpdateExamPaperRequest req, ClaimsPrincipal caller, CancellationToken ct = default)
    {
        var existing = await exams.GetExamPaperAsync(id, ct);
        if (existing is null)
            return ApiResult<ExamPaperResponse>.Fail(new Error("not_found", "resource not found"), 404);
        if (!CanEditExamPaper(existing, caller))
            return ApiResult<ExamPaperResponse>.Fail(new Error("forbidden", "not the creator or a principal"), 403);

        var updated = await exams.UpdateExamPaperAsync(id, req, ct);
        return updated is null
            ? ApiResult<ExamPaperResponse>.Fail(new Error("not_found", "resource not found"), 404)
            : ApiResult<ExamPaperResponse>.Ok(updated);
    }

    public async Task<ApiResult> DeleteExamPaperAsync(Guid id, ClaimsPrincipal caller, CancellationToken ct = default)
    {
        var existing = await exams.GetExamPaperAsync(id, ct);
        if (existing is null)
            return ApiResult.Fail(new Error("not_found", "resource not found"), 404);
        if (!CanEditExamPaper(existing, caller))
            return ApiResult.Fail(new Error("forbidden", "not the creator or a principal"), 403);

        await exams.DeleteExamPaperAsync(id, ct);
        return ApiResult.NoContent();
    }

    /// SD-5 (matrix row EXM-05): creator-or-principal. A legacy paper with no CreatedBy
    /// (pre-migration row) is deliberately principal-only — NULL never counts as "mine".
    private static bool CanEditExamPaper(ExamPaperResponse paper, ClaimsPrincipal caller)
    {
        if (IsPrincipal(caller)) return true;
        var callerId = CallerUserId(caller);
        return paper.CreatedBy is { } createdBy && callerId is { } uid && createdBy == uid;
    }

    public async Task<ApiResult<IReadOnlyList<GradeResponse>>> ListGradesAsync(
        Guid examPaperId, CancellationToken ct = default) =>
        ApiResult<IReadOnlyList<GradeResponse>>.Ok(await exams.ListGradesAsync(examPaperId, ct));

    public async Task<ApiResult<IReadOnlyList<GradeResponse>>> ListGradesForStudentAsync(
        Guid studentId, CancellationToken ct = default) =>
        ApiResult<IReadOnlyList<GradeResponse>>.Ok(await exams.ListGradesForStudentAsync(studentId, ct));

    public async Task<ApiResult<IReadOnlyList<ExamLetterGradeCount>>> CountLetterGradesForExamAsync(
        Guid examId, CancellationToken ct = default)
    {
        if (await exams.GetExamAsync(examId, ct) is null)
            return ApiResult<IReadOnlyList<ExamLetterGradeCount>>.Fail(new Error("not_found", "resource not found"), 404);
        return ApiResult<IReadOnlyList<ExamLetterGradeCount>>.Ok(await exams.CountLetterGradesForExamAsync(examId, ct));
    }

    public async Task<ApiResult<GradeResponse>> UpsertGradeAsync(UpsertGradeRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<GradeResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        return ApiResult<GradeResponse>.Ok((await exams.UpsertGradeAsync(tid, req, ct))!);
    }

    public async Task<ApiResult<IReadOnlyList<ExamAttendanceRecordResponse>>> ListExamAttendanceAsync(
        Guid examPaperId, CancellationToken ct = default) =>
        ApiResult<IReadOnlyList<ExamAttendanceRecordResponse>>.Ok(
            await exams.ListExamAttendanceAsync(examPaperId, ct));

    public async Task<ApiResult> BulkUpsertExamAttendanceAsync(
        Guid examPaperId, BulkExamAttendanceRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult.Fail(new Error("forbidden", "no tenant context"), 403);
        if (await exams.GetExamPaperAsync(examPaperId, ct) is null)
            return ApiResult.Fail(new Error("not_found", "resource not found"), 404);
        await exams.BulkUpsertExamAttendanceAsync(tid, examPaperId, tenant.UserId, req.Records, ct);
        return ApiResult.NoContent();
    }

    public async Task<ApiResult<IReadOnlyList<HomeworkResponse>>> ListHomeworkAsync(
        Guid? studentId, string? status, CancellationToken ct = default) =>
        ApiResult<IReadOnlyList<HomeworkResponse>>.Ok(await homework.ListAsync(studentId, status, ct));

    public async Task<ApiResult<HomeworkResponse>> GetHomeworkAsync(Guid id, CancellationToken ct = default)
    {
        var h = await homework.GetAsync(id, ct);
        return h is null
            ? ApiResult<HomeworkResponse>.Fail(new Error("not_found", "resource not found"), 404)
            : ApiResult<HomeworkResponse>.Ok(h);
    }

    public async Task<ApiResult<HomeworkResponse>> CreateHomeworkAsync(
        CreateHomeworkRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<HomeworkResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        return ApiResult<HomeworkResponse>.Ok((await homework.CreateAsync(tid, req, ct))!, 201);
    }

    public async Task<ApiResult<HomeworkResponse>> SetHomeworkStatusAsync(
        Guid id, SetHomeworkStatusRequest req, CancellationToken ct = default)
    {
        if (await homework.GetAsync(id, ct) is null)
            return ApiResult<HomeworkResponse>.Fail(new Error("not_found", "resource not found"), 404);
        return ApiResult<HomeworkResponse>.Ok((await homework.SetStatusAsync(id, req.Status, ct))!);
    }

    public async Task<ApiResult<IReadOnlyList<AchievementResponse>>> ListAchievementsAsync(
        Guid studentId, CancellationToken ct = default)
    {
        var student = await sis.GetStudentAsync(studentId, ct);
        if (!student.IsSuccess)
        {
            return ApiResult<IReadOnlyList<AchievementResponse>>.Fail(
                student.Error ?? new Error("not_found", "student not found"), student.StatusCode);
        }

        var hw = await homework.ListAsync(studentId, null, ct);
        var grades = await exams.ListGradesForStudentAsync(studentId, ct);
        var awarded = (await achievements.ListAsync(studentId, ct))
            .Select(AchievementComposer.FromAward)
            .ToList();
        var published = grades
            .Where(g => g.ExamPublished && g.MaxMarks > 0)
            .Select(g => (g.Marks, g.MaxMarks))
            .ToList();

        return ApiResult<IReadOnlyList<AchievementResponse>>.Ok(
            AchievementComposer.Compose(
                studentId,
                student.Data!.AttendancePct,
                hw.Select(h => h.Status).ToList(),
                published,
                awarded,
                clock.UtcNow));
    }

    public async Task<ApiResult<AchievementResponse>> CreateAchievementAsync(
        CreateAchievementRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<AchievementResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        var title = (req.Title ?? "").Trim();
        if (title.Length == 0)
            return ApiResult<AchievementResponse>.Fail(new Error("validation", "title is required"), 400);
        var student = await sis.GetStudentAsync(req.StudentId, ct);
        if (!student.IsSuccess)
            return ApiResult<AchievementResponse>.Fail(
                student.Error ?? new Error("not_found", "student not found"), student.StatusCode);

        var awardedOn = (req.AwardedOn ?? clock.UtcNow).Date;
        var icon = AchievementComposer.NormIcon(req.Icon);
        var hue = AchievementComposer.NormHue(req.Hue);
        var row = await achievements.CreateAsync(tid, req with { Title = title }, awardedOn, icon, hue, ct);
        return ApiResult<AchievementResponse>.Ok(AchievementComposer.FromAward(row!), 201);
    }

    public async Task<ApiResult<HomeworkResponse>> SubmitHomeworkAsync(Guid id, CancellationToken ct = default)
    {
        if (await homework.GetAsync(id, ct) is null)
            return ApiResult<HomeworkResponse>.Fail(new Error("not_found", "resource not found"), 404);
        return ApiResult<HomeworkResponse>.Ok((await homework.SetStatusAsync(id, "submitted", ct))!);
    }

    public async Task<ApiResult<IReadOnlyList<TimetableSlotResponse>>> ListTimetableAsync(
        ClaimsPrincipal caller, CancellationToken ct = default)
    {
        var roles = caller.FindAll("role").Select(c => c.Value).ToArray();
        var roleLeaves = roles.Select(r => r.Split('.').LastOrDefault()).ToArray();
        var isTeacherOnly = roleLeaves.Contains("teacher") &&
            !roleLeaves.Any(r => r is "principal" or "admin" or "owner");
        if (!isTeacherOnly)
            return ApiResult<IReadOnlyList<TimetableSlotResponse>>.Ok(await timetable.ListAsync(ct));

        var sub = caller.FindFirst("sub")?.Value;
        if (sub is null || !Guid.TryParse(sub, out var userId))
            return ApiResult<IReadOnlyList<TimetableSlotResponse>>.Fail(new Error("unauthorized", "unauthorized"), 401);
        return ApiResult<IReadOnlyList<TimetableSlotResponse>>.Ok(await timetable.ListForTeacherAsync(userId, ct));
    }

    public async Task<ApiResult<IReadOnlyList<TimetableSlotResponse>>> ListTimetableForStudentAsync(
        string? grade, string? section, string? classLabel, CancellationToken ct = default)
    {
        var all = await timetable.ListAsync(ct);
        var classRows = await classes.ListAsync(ct);
        var classIds = StudentClassScope.MatchingTimetableClassIds(classRows, grade, section, classLabel);
        var filtered = all
            .Where(s => StudentClassScope.SlotBelongsToStudent(s, classIds, grade, section, classLabel))
            .ToList();
        return ApiResult<IReadOnlyList<TimetableSlotResponse>>.Ok(filtered);
    }

    public async Task<ApiResult<IReadOnlyList<TimetableSlotResponse>>> ListTimetableForStudentIdAsync(
        Guid studentId, ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (await DenyIfNotOwnStudentAsync<IReadOnlyList<TimetableSlotResponse>>(studentId, caller, ct) is { } denied)
            return denied;

        var row = await sis.GetStudentAsync(studentId, ct);
        if (row.Error is not null)
            return ApiResult<IReadOnlyList<TimetableSlotResponse>>.Fail(row.Error, row.StatusCode);

        var s = row.Data!;
        return await ListTimetableForStudentAsync(s.Grade, s.Section, s.ClassLabel, ct);
    }

    public async Task<ApiResult<TimetableSlotResponse>> CreateTimetableSlotAsync(
        CreateTimetableSlotRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<TimetableSlotResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        return ApiResult<TimetableSlotResponse>.Ok((await timetable.CreateAsync(tid, req, ct))!, 201);
    }

    public async Task<ApiResult> ReplaceTimetableAsync(ReplaceTimetableRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult.Fail(new Error("forbidden", "no tenant context"), 403);
        if (req.ClassIds is null || req.ClassIds.Count == 0)
            return ApiResult.Fail(new Error("validation", "class_ids required"), 400);

        var owned = req.ClassIds.ToHashSet();
        var slots = req.Slots ?? Array.Empty<CreateTimetableSlotRequest>();
        foreach (var s in slots)
        {
            if (s.ClassId is null || !owned.Contains(s.ClassId.Value))
                return ApiResult.Fail(new Error("validation", "each slot class_id must be listed in class_ids"), 400);
        }

        await timetable.ReplaceForClassesAsync(tid, req.ClassIds, slots, ct);
        try
        {
            await academicsNotifier.NotifyTimetablePublishedAsync(tid, req.ClassIds.Count, slots.Count, ct);
        }
        catch
        {
            /* best-effort — publish already persisted */
        }
        return ApiResult.NoContent();
    }

    public async Task<ApiResult> DeleteTimetableSlotAsync(Guid id, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult.Fail(new Error("forbidden", "no tenant context"), 403);
        if (await timetable.GetAsync(id, ct) is null)
            return ApiResult.Fail(new Error("not_found", "resource not found"), 404);
        await timetable.DeleteAsync(id, tid, ct);
        return ApiResult.NoContent();
    }

    public async Task<ApiResult<IReadOnlyList<CalendarEventResponse>>> ListCalendarEventsAsync(CancellationToken ct = default) =>
        ApiResult<IReadOnlyList<CalendarEventResponse>>.Ok(await calendar.ListAsync(ct));

    public async Task<ApiResult<CalendarEventResponse>> CreateCalendarEventAsync(
        CreateCalendarEventRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<CalendarEventResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        if (string.IsNullOrWhiteSpace(req.Title))
            return ApiResult<CalendarEventResponse>.Fail(new Error("validation_error", "Title is required"), 400);
        if (string.IsNullOrWhiteSpace(req.Type))
            return ApiResult<CalendarEventResponse>.Fail(new Error("validation_error", "Type is required"), 400);
        return ApiResult<CalendarEventResponse>.Ok((await calendar.CreateAsync(tid, req, ct))!, 201);
    }

    public async Task<ApiResult> DeleteCalendarEventAsync(Guid id, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult.Fail(new Error("forbidden", "no tenant context"), 403);
        if (!await calendar.DeleteAsync(tid, id, ct))
            return ApiResult.Fail(new Error("not_found", "resource not found"), 404);
        return ApiResult.NoContent();
    }

    public async Task<ApiResult<PublishSnapshotResponse>> GetAcademicPeriodsAsync(CancellationToken ct = default)
    {
        var row = await academicPublish.GetPeriodsAsync(ct);
        if (row is not null) return ApiResult<PublishSnapshotResponse>.Ok(row);
        return ApiResult<PublishSnapshotResponse>.Ok(new PublishSnapshotResponse(
            Guid.Empty, tenant.TenantId ?? Guid.Empty, null, null, null, null));
    }

    public async Task<ApiResult<PublishSnapshotResponse>> UpsertAcademicPeriodsAsync(
        UpsertPublishSnapshotRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<PublishSnapshotResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        return ApiResult<PublishSnapshotResponse>.Ok((await academicPublish.UpsertPeriodsAsync(tid, req, ct))!);
    }

    public async Task<ApiResult<PublishSnapshotResponse>> GetClassTestScheduleAsync(CancellationToken ct = default)
    {
        var row = await academicPublish.GetClassTestsAsync(ct);
        if (row is not null) return ApiResult<PublishSnapshotResponse>.Ok(row);
        return ApiResult<PublishSnapshotResponse>.Ok(new PublishSnapshotResponse(
            Guid.Empty, tenant.TenantId ?? Guid.Empty, null, null, null, null));
    }

    public async Task<ApiResult<PublishSnapshotResponse>> UpsertClassTestScheduleAsync(
        UpsertPublishSnapshotRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<PublishSnapshotResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        var previous = await academicPublish.GetClassTestsAsync(ct);
        var saved = (await academicPublish.UpsertClassTestsAsync(tid, req, ct))!;
        try
        {
            foreach (var test in ClassTestScheduleNotices.NewTests(previous, saved))
            {
                var subjectLine = string.IsNullOrWhiteSpace(test.ClassName)
                    ? test.Subject
                    : string.IsNullOrWhiteSpace(test.Subject)
                        ? test.ClassName
                        : $"{test.Subject} · {test.ClassName}";
                await academicsNotifier.NotifyClassTestScheduledAsync(
                    tid, test.Title, subjectLine, test.Date, ct: ct);
            }
        }
        catch
        {
            /* notification is best-effort; schedule save already succeeded */
        }
        return ApiResult<PublishSnapshotResponse>.Ok(saved);
    }

    private static string? NormExtrasPersonType(string? t)
    {
        var v = (t ?? "").Trim().ToLowerInvariant();
        return v is "student" or "teacher" or "staff" ? v : null;
    }

    public async Task<ApiResult<PersonExtrasResponse>> GetPersonExtrasAsync(
        string personType, Guid personId, CancellationToken ct = default)
    {
        var kind = NormExtrasPersonType(personType);
        if (kind is null)
            return ApiResult<PersonExtrasResponse>.Fail(new Error("validation_error", "Invalid person type"), 400);
        var row = await personExtras.GetAsync(kind, personId, ct);
        if (row is not null) return ApiResult<PersonExtrasResponse>.Ok(row);
        return ApiResult<PersonExtrasResponse>.Ok(new PersonExtrasResponse(
            Guid.Empty, tenant.TenantId ?? Guid.Empty, kind, personId, "{}", DateTime.UtcNow));
    }

    public async Task<ApiResult<PersonExtrasResponse>> UpsertPersonExtrasAsync(
        string personType, Guid personId, UpsertPersonExtrasRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<PersonExtrasResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        var kind = NormExtrasPersonType(personType);
        if (kind is null)
            return ApiResult<PersonExtrasResponse>.Fail(new Error("validation_error", "Invalid person type"), 400);
        var json = string.IsNullOrWhiteSpace(req.ExtrasJson) ? "{}" : req.ExtrasJson;
        var saved = (await personExtras.UpsertAsync(tid, kind, personId, json, ct))!;
        if (kind == "student")
        {
            var mapped = GuardianContactFromExtras(json);
            await sis.SyncGuardianContactAsync(personId, mapped.Email, mapped.Phone, mapped.Name, ct);
        }
        return ApiResult<PersonExtrasResponse>.Ok(saved);
    }

    private static (string? Email, string? Phone, string? Name) GuardianContactFromExtras(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = doc.RootElement;
            var email = FirstParentField(root, "email")
                ?? RootString(root, "guardianEmail")
                ?? RootString(root, "guardian_email");
            var phone = FirstParentField(root, "phone")
                ?? RootString(root, "guardianPhone")
                ?? RootString(root, "guardian_phone");
            var name = FirstParentField(root, "name")
                ?? RootString(root, "guardianName")
                ?? RootString(root, "guardian_name");
            return (email, phone, name);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, null, null);
        }
    }

    private static string? RootString(System.Text.Json.JsonElement root, string name)
    {
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        if (!root.TryGetProperty(name, out var node)) return null;
        var v = node.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static string? FirstParentField(System.Text.Json.JsonElement root, string field)
    {
        foreach (var parent in new[] { "father", "mother", "guardian" })
        {
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            if (!root.TryGetProperty(parent, out var node) || node.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;
            if (!node.TryGetProperty(field, out var value)) continue;
            var v = value.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(v))
            {
                if (field == "email" && v.IndexOf('@') <= 0) continue;
                return v;
            }
        }
        return null;
    }

    public async Task<ApiResult<IReadOnlyList<LibraryBookResponse>>> ListLibraryBooksAsync(CancellationToken ct = default)
    {
        if (!FeatureGate.Allowed(tenant, features, FeatureCatalog.Operations))
            return FeatureGate.Locked<IReadOnlyList<LibraryBookResponse>>(FeatureCatalog.Operations);
        return ApiResult<IReadOnlyList<LibraryBookResponse>>.Ok(await library.ListAsync(clock.UtcNow, ct));
    }

    /// Flat late-fee rate (₹/day) applied to overdue books when computing library fines due.
    private const decimal LibraryFinePerDay = 5m;

    public async Task<ApiResult<LibrarySummaryResponse>> GetLibrarySummaryAsync(CancellationToken ct = default)
    {
        if (!FeatureGate.Allowed(tenant, features, FeatureCatalog.Operations))
            return FeatureGate.Locked<LibrarySummaryResponse>(FeatureCatalog.Operations);
        return ApiResult<LibrarySummaryResponse>.Ok(await library.SummaryAsync(clock.UtcNow, LibraryFinePerDay, ct));
    }

    public async Task<ApiResult<LibraryBookResponse>> CreateLibraryBookAsync(
        CreateLibraryBookRequest req, CancellationToken ct = default)
    {
        if (!FeatureGate.Allowed(tenant, features, FeatureCatalog.Operations))
            return FeatureGate.Locked<LibraryBookResponse>(FeatureCatalog.Operations);
        if (tenant.TenantId is not { } tid)
            return ApiResult<LibraryBookResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        return ApiResult<LibraryBookResponse>.Ok((await library.CreateAsync(tid, req, ct))!, 201);
    }

    public async Task<ApiResult<IReadOnlyList<AssignmentResponse>>> ListAssignmentsAsync(CancellationToken ct = default) =>
        ApiResult<IReadOnlyList<AssignmentResponse>>.Ok(await assignments.ListAsync(clock.UtcNow, ct));

    public async Task<ApiResult<AssignmentResponse>> CreateAssignmentAsync(
        CreateAssignmentRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<AssignmentResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        var created = await assignments.CreateAsync(tid, req, ct);
        if (created is null)
            return ApiResult<AssignmentResponse>.Fail(new Error("internal_error", "could not create assignment"), 500);
        try
        {
            await academicsNotifier.NotifyHomeworkAssignedAsync(
                tid, req.Title, created.ClassName ?? req.ClassName, created.DueDate ?? req.DueDate, ct);
        }
        catch
        {
            /* best-effort */
        }
        return ApiResult<AssignmentResponse>.Ok(created, 201);
    }

    public async Task<ApiResult<AssignmentResponse>> UpdateAssignmentAsync(
        Guid id, CreateAssignmentRequest req, CancellationToken ct = default)
    {
        var updated = await assignments.UpdateAsync(id, req, ct);
        if (updated is null)
            return ApiResult<AssignmentResponse>.Fail(new Error("not_found", "resource not found"), 404);
        return ApiResult<AssignmentResponse>.Ok(updated);
    }

    private async Task<ClassResponse> AttachSubjectsAsync(ClassResponse row, CancellationToken ct)
    {
        var names = await classSubjects.ListNamesAsync(row.Id, ct);
        return row with { Subjects = names };
    }

    private async Task<IReadOnlyList<ClassResponse>> AttachSubjectsAsync(
        IReadOnlyList<ClassResponse> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return rows;
        var map = (await classSubjects.ListForClassesAsync(rows.Select(r => r.Id).ToList(), ct))
            .GroupBy(r => r.ClassId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Name).ToList());
        return rows.Select(r => r with { Subjects = map.GetValueOrDefault(r.Id) ?? [] }).ToList();
    }
}
