using System.Security.Claims;
using Sms.Application.Common;
using Sms.Modules.Academics.Contracts;

namespace Sms.Application.Services.Academics;

public interface IAcademicsService
{
    Task<ApiResult<IReadOnlyList<ClassResponse>>> ListClassesAsync(ClaimsPrincipal caller, CancellationToken ct = default);
    Task<ApiResult<ClassResponse>> GetClassAsync(Guid id, CancellationToken ct = default);
    Task<ApiResult<ClassResponse>> CreateClassAsync(CreateClassRequest req, CancellationToken ct = default);
    Task<ApiResult<ClassResponse>> UpdateClassAsync(Guid id, UpdateClassRequest req, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<string>>> ListClassSubjectsAsync(Guid classId, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<string>>> ReplaceClassSubjectsAsync(
        Guid classId, IReadOnlyList<string> names, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<string>>> ListSchoolHousesAsync(CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<string>>> ReplaceSchoolHousesAsync(
        IReadOnlyList<string> names, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<SubjectResponse>>> ListSubjectsAsync(CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<SubjectResponse>>> ListSubjectsForStudentAsync(
        string? grade, string? section, string? classLabel, CancellationToken ct = default);
    Task<ApiResult<SubjectResponse>> GetSubjectAsync(Guid id, CancellationToken ct = default);
    Task<ApiResult<SubjectResponse>> CreateSubjectAsync(CreateSubjectRequest req, CancellationToken ct = default);
    Task<ApiResult<SubjectResponse>> UpdateSubjectAsync(Guid id, UpdateSubjectRequest req, CancellationToken ct = default);
    Task<ApiResult> DeleteSubjectAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<AttendanceRecordResponse>>> ListAttendanceAsync(
        Guid classId, DateTime date, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<AttendanceRecordResponse>>> ListAttendanceRangeAsync(
        Guid classId, DateTime from, DateTime to, CancellationToken ct = default);
    Task<ApiResult> BulkUpsertAttendanceAsync(
        Guid classId, BulkAttendanceRequest req, ClaimsPrincipal caller, CancellationToken ct = default);
    Task<ApiResult<AttendanceRollCallResponse>> GetAttendanceRollCallAsync(
        Guid classId, DateTime date, ClaimsPrincipal caller, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<AttendanceRecordResponse>>> ListAttendanceForStudentAsync(
        Guid studentId, DateTime from, DateTime to, ClaimsPrincipal caller, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<ClassDayTimetableSlotResponse>>> ListClassDayTimetableAsync(
        Guid classId, DateTime date, ClaimsPrincipal caller, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<PeriodAttendanceRecordResponse>>> ListPeriodAttendanceAsync(
        Guid classId, DateTime date, int period, string subject, CancellationToken ct = default);
    Task<ApiResult<PeriodAttendanceAdvancedPage>> ListPeriodAttendanceAdvancedAsync(
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
        CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<PeriodAttendanceAuditRow>>> GetPeriodAttendanceAuditAsync(
        Guid recordId,
        ClaimsPrincipal caller,
        CancellationToken ct = default);
    Task<ApiResult<AdvClassDaySummary>> GetPeriodAttendanceClassDaySummaryAsync(
        ClaimsPrincipal caller,
        Guid classId,
        DateOnly date,
        CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<AdvSubjectSummaryRow>>> ListPeriodAttendanceSubjectSummariesAsync(
        ClaimsPrincipal caller,
        Guid classId,
        string? preset,
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<AdvTeacherSummaryRow>>> ListPeriodAttendanceTeacherSummariesAsync(
        ClaimsPrincipal caller,
        string? preset,
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct = default);
    Task<ApiResult<AdvRangeRollup>> GetPeriodAttendanceRangeSummaryAsync(
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
        CancellationToken ct = default);
    Task<ApiResult> BulkUpsertPeriodAttendanceAsync(
        Guid classId, BulkPeriodAttendanceRequest req, ClaimsPrincipal caller, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<PeriodAttendanceRecordResponse>>> ListPeriodAttendanceForStudentAsync(
        Guid studentId, DateTime from, DateTime to, ClaimsPrincipal caller, CancellationToken ct = default);
    Task<ApiResult<PeriodAttendanceSummaryResponse>> GetPeriodAttendanceSummaryForStudentAsync(
        Guid studentId, DateTime from, DateTime to, ClaimsPrincipal caller, CancellationToken ct = default);
    Task<ApiResult<PeriodAttendanceSummaryResponse>> GetPeriodAttendanceSummaryForClassAsync(
        Guid classId, DateTime from, DateTime to, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<StaffAttendanceRecordResponse>>> ListStaffAttendanceAsync(
        string personType, DateTime date, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<StaffAttendanceRecordResponse>>> ListStaffAttendanceRangeAsync(
        string personType, DateTime from, DateTime to, CancellationToken ct = default);
    Task<ApiResult> BulkUpsertStaffAttendanceAsync(
        BulkStaffAttendanceRequest req, ClaimsPrincipal caller, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<StaffAttendanceRecordResponse>>> ListStaffAttendanceForPersonAsync(
        string personType, Guid personId, DateTime from, DateTime to, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<ExamResponse>>> ListExamsAsync(CancellationToken ct = default);
    Task<ApiResult<ExamResponse>> GetExamAsync(Guid id, CancellationToken ct = default);
    Task<ApiResult<ExamResponse>> CreateExamAsync(CreateExamRequest req, CancellationToken ct = default);
    Task<ApiResult<ExamResponse>> UpdateExamAsync(Guid id, UpdateExamRequest req, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<ExamPaperResponse>>> ListExamPapersAsync(
        Guid? examId, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<ExamPaperResponse>>> ListExamPapersForStudentAsync(
        Guid? examId, Guid studentId, CancellationToken ct = default);
    Task<ApiResult<ExamPaperResponse>> GetExamPaperAsync(Guid id, CancellationToken ct = default);
    Task<ApiResult<ExamPaperResponse>> CreateExamPaperAsync(CreateExamPaperRequest req, CancellationToken ct = default);
    Task<ApiResult<ExamPaperResponse>> UpdateExamPaperAsync(
        Guid id, UpdateExamPaperRequest req, ClaimsPrincipal caller, CancellationToken ct = default);
    Task<ApiResult> DeleteExamPaperAsync(Guid id, ClaimsPrincipal caller, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<GradeResponse>>> ListGradesAsync(Guid examPaperId, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<GradeResponse>>> ListGradesForStudentAsync(Guid studentId, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<ExamLetterGradeCount>>> CountLetterGradesForExamAsync(Guid examId, CancellationToken ct = default);
    Task<ApiResult<GradeResponse>> UpsertGradeAsync(UpsertGradeRequest req, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<ExamAttendanceRecordResponse>>> ListExamAttendanceAsync(
        Guid examPaperId, CancellationToken ct = default);
    Task<ApiResult> BulkUpsertExamAttendanceAsync(
        Guid examPaperId, BulkExamAttendanceRequest req, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<HomeworkResponse>>> ListHomeworkAsync(
        Guid? studentId, string? status, CancellationToken ct = default);
    Task<ApiResult<HomeworkResponse>> GetHomeworkAsync(Guid id, CancellationToken ct = default);
    Task<ApiResult<HomeworkResponse>> CreateHomeworkAsync(CreateHomeworkRequest req, CancellationToken ct = default);
    Task<ApiResult<HomeworkResponse>> SetHomeworkStatusAsync(
        Guid id, SetHomeworkStatusRequest req, CancellationToken ct = default);
    Task<ApiResult<HomeworkResponse>> SubmitHomeworkAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<AchievementResponse>>> ListAchievementsAsync(
        Guid studentId, CancellationToken ct = default);
    Task<ApiResult<AchievementResponse>> CreateAchievementAsync(
        CreateAchievementRequest req, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<TimetableSlotResponse>>> ListTimetableAsync(
        ClaimsPrincipal caller, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<TimetableSlotResponse>>> ListTimetableForStudentAsync(
        string? grade, string? section, string? classLabel, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<TimetableSlotResponse>>> ListTimetableForStudentIdAsync(
        Guid studentId, ClaimsPrincipal caller, CancellationToken ct = default);
    Task<ApiResult<TimetableSlotResponse>> CreateTimetableSlotAsync(
        CreateTimetableSlotRequest req, CancellationToken ct = default);
    Task<ApiResult> ReplaceTimetableAsync(ReplaceTimetableRequest req, CancellationToken ct = default);
    Task<ApiResult> DeleteTimetableSlotAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<CalendarEventResponse>>> ListCalendarEventsAsync(CancellationToken ct = default);
    Task<ApiResult<CalendarEventResponse>> CreateCalendarEventAsync(
        CreateCalendarEventRequest req, CancellationToken ct = default);
    Task<ApiResult> DeleteCalendarEventAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<PublishSnapshotResponse>> GetAcademicPeriodsAsync(CancellationToken ct = default);
    Task<ApiResult<PublishSnapshotResponse>> UpsertAcademicPeriodsAsync(
        UpsertPublishSnapshotRequest req, CancellationToken ct = default);
    Task<ApiResult<PublishSnapshotResponse>> GetClassTestScheduleAsync(CancellationToken ct = default);
    Task<ApiResult<PublishSnapshotResponse>> UpsertClassTestScheduleAsync(
        UpsertPublishSnapshotRequest req, CancellationToken ct = default);

    Task<ApiResult<PersonExtrasResponse>> GetPersonExtrasAsync(
        string personType, Guid personId, CancellationToken ct = default);
    Task<ApiResult<PersonExtrasResponse>> UpsertPersonExtrasAsync(
        string personType, Guid personId, UpsertPersonExtrasRequest req, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<LibraryBookResponse>>> ListLibraryBooksAsync(CancellationToken ct = default);
    Task<ApiResult<LibrarySummaryResponse>> GetLibrarySummaryAsync(CancellationToken ct = default);
    Task<ApiResult<LibraryBookResponse>> CreateLibraryBookAsync(
        CreateLibraryBookRequest req, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<AssignmentResponse>>> ListAssignmentsAsync(CancellationToken ct = default);
    Task<ApiResult<AssignmentResponse>> CreateAssignmentAsync(
        CreateAssignmentRequest req, CancellationToken ct = default);
    Task<ApiResult<AssignmentResponse>> UpdateAssignmentAsync(
        Guid id, CreateAssignmentRequest req, CancellationToken ct = default);
}
