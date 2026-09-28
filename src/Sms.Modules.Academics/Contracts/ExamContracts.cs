namespace Sms.Modules.Academics.Contracts;

// ---- Exam (term) ----
// ClassIds is a trailing init-only property with a secondary constructor, not a primary-
// constructor parameter: Dapper materializes via a constructor matching the exact column
// count of whatever query ran, and the inline SELECT/stored procs only ever return the
// original 11 columns (ClassIds is populated separately via ListExamClassIdsAsync).
public sealed record ExamResponse(
    Guid Id, Guid TenantId, string Name, string? Type, string? Grades, DateTime? FromDate, DateTime? ToDate,
    int SubjectCount, string Status, decimal MarksEnteredPct, bool Published)
{
    public IReadOnlyList<Guid>? ClassIds { get; init; }

    public ExamResponse(
        Guid Id, Guid TenantId, string Name, string? Type, string? Grades, DateTime? FromDate, DateTime? ToDate,
        int SubjectCount, string Status, decimal MarksEnteredPct, bool Published, IReadOnlyList<Guid>? ClassIds)
        : this(Id, TenantId, Name, Type, Grades, FromDate, ToDate, SubjectCount, Status, MarksEnteredPct, Published) =>
        this.ClassIds = ClassIds;
}
public sealed record CreateExamRequest(
    string Name, string? Type, string? Grades, DateTime? FromDate, DateTime? ToDate, int SubjectCount,
    IReadOnlyList<Guid>? ClassIds = null);
public sealed record UpdateExamRequest(
    string? Status, bool? Published, decimal? MarksEnteredPct, IReadOnlyList<Guid>? ClassIds = null);

// ---- ExamPaper (shared resource) ----
// CreatedBy (SD-5) is a trailing init-only property with a secondary constructor, not a
// primary-constructor parameter, for the same reason as ExamResponse.ClassIds above: Dapper
// materializes via a constructor matching the exact column count of whatever query ran, and
// dbo.ExamPaper_Create/_Update's RETURNS TABLE deliberately still return the original 16
// columns (only the inline Get/List SELECTs add CreatedBy).
public sealed record ExamPaperResponse(
    Guid Id, Guid TenantId, Guid? ExamId, Guid? ClassId, string? Name, string? Subject, Guid? SubjectId,
    DateTime? Date, string? StartTime, int? DurationMin, int MaxMarks, string? Room,
    string? Invigilator1, string? Invigilator2, string Status, string? Topics = null)
{
    public Guid? CreatedBy { get; init; }

    public ExamPaperResponse(
        Guid Id, Guid TenantId, Guid? ExamId, Guid? ClassId, string? Name, string? Subject, Guid? SubjectId,
        DateTime? Date, string? StartTime, int? DurationMin, int MaxMarks, string? Room,
        string? Invigilator1, string? Invigilator2, string Status, string? Topics, Guid? CreatedBy)
        : this(Id, TenantId, ExamId, ClassId, Name, Subject, SubjectId, Date, StartTime, DurationMin, MaxMarks, Room,
               Invigilator1, Invigilator2, Status, Topics) =>
        this.CreatedBy = CreatedBy;
}
public sealed record CreateExamPaperRequest(
    Guid? ExamId, Guid? ClassId, string? Name, string? Subject, Guid? SubjectId, DateTime? Date,
    string? StartTime, int? DurationMin, int MaxMarks, string? Room, string? Invigilator1, string? Invigilator2,
    string? Topics = null);

public sealed record UpdateExamPaperRequest(
    string? Name, string? Subject, Guid? SubjectId, DateTime? Date, string? StartTime, int? DurationMin,
    int? MaxMarks, string? Room, string? Invigilator1, string? Invigilator2, string? Status, string? Topics = null);

// ---- Grade ----
/// Property bag so Dapper can map both paper-scoped lists (11 cols) and
/// student report-card lists (+ SubjectId/Subject/PaperName/ExamPublished).
public sealed class GradeResponse
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid StudentId { get; init; }
    public string? StudentName { get; init; }
    public Guid ExamPaperId { get; init; }
    public decimal Marks { get; init; }
    public decimal MaxMarks { get; init; }
    public string? Grade { get; init; }
    public decimal Gpa { get; init; }
    public bool Pass { get; init; }
    public DateTime? Date { get; init; }
    public Guid? SubjectId { get; init; }
    public string? Subject { get; init; }
    public string? PaperName { get; init; }
    public bool ExamPublished { get; init; }
}
public sealed record ExamLetterGradeCount(string Grade, int Count);
public sealed record UpsertGradeRequest(Guid StudentId, string? StudentName, Guid ExamPaperId, decimal Marks);
