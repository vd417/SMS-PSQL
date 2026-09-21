using System.Text.Json;
using Sms.Modules.Academics.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Academics.Data;

public sealed class ExamRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string ExamCols =
        "\"Id\", \"TenantId\", \"Name\", \"Type\", \"Grades\", \"FromDate\", \"ToDate\", \"SubjectCount\", \"Status\", \"MarksEnteredPct\", \"Published\"";
    private const string PaperCols =
        "\"Id\", \"TenantId\", \"ExamId\", \"ClassId\", \"Name\", \"Subject\", \"SubjectId\", \"Date\", \"StartTime\", \"DurationMin\", \"MaxMarks\", " +
        "\"Room\", \"Invigilator1\", \"Invigilator2\", \"Status\", \"Topics\"";
    private const string GradeCols =
        "\"Id\", \"TenantId\", \"StudentId\", \"StudentName\", \"ExamPaperId\", \"Marks\", \"MaxMarks\", \"Grade\", \"Gpa\", \"Pass\", \"Date\"";

    // Exams
    public Task<ExamResponse?> CreateExamAsync(Guid tenantId, CreateExamRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<ExamResponse>("dbo.Exam_Create",
            new { TenantId = tenantId, r.Name, r.Type, r.Grades, r.FromDate, r.ToDate, r.SubjectCount }, ct);

    public Task<ExamResponse?> UpdateExamAsync(Guid id, UpdateExamRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<ExamResponse>("dbo.Exam_Update",
            new { Id = id, r.Status, r.Published, r.MarksEnteredPct }, ct);

    public async Task<ExamResponse?> GetExamAsync(Guid id, CancellationToken ct = default) =>
        (await QueryInlineAsync<ExamResponse>($"SELECT {ExamCols} FROM \"dbo\".\"Exams\" WHERE \"Id\" = @id", new { id }, ct))
        .FirstOrDefault();

    public Task<IReadOnlyList<ExamResponse>> ListExamsAsync(CancellationToken ct = default) =>
        QueryInlineAsync<ExamResponse>($"SELECT {ExamCols} FROM \"dbo\".\"Exams\" ORDER BY \"FromDate\" DESC", null, ct);

    public Task<IReadOnlyList<ExamResponse>> ListUpcomingExamsAsync(
        Guid tenantId, DateTime fromDate, CancellationToken ct = default) =>
        QueryInlineAsync<ExamResponse>(
            $"SELECT {ExamCols} FROM \"dbo\".\"Exams\" WHERE \"TenantId\" = @tenantId AND \"FromDate\" >= @fromDate ORDER BY \"FromDate\" ASC",
            new { tenantId, fromDate }, ct);

    public Task<IReadOnlyList<Guid>> ListExamClassIdsAsync(Guid examId, CancellationToken ct = default) =>
        QueryProcAsync<Guid>("dbo.ExamClass_List", new { ExamId = examId }, ct);

    public Task<IReadOnlyList<Guid>> ReplaceExamClassIdsAsync(
        Guid tenantId, Guid examId, IReadOnlyList<Guid> classIds, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(classIds);
        return QueryProcAsync<Guid>("dbo.ExamClass_Replace", new
        {
            TenantId = tenantId,
            ExamId = examId,
            ClassIdsJson = json,
        }, ct);
    }

    // Exam papers
    public Task<ExamPaperResponse?> CreateExamPaperAsync(Guid tenantId, CreateExamPaperRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<ExamPaperResponse>("dbo.ExamPaper_Create", new
        {
            TenantId = tenantId, r.ExamId, r.ClassId, r.Name, r.Subject, r.SubjectId, r.Date, r.StartTime,
            r.DurationMin, r.MaxMarks, r.Room, r.Invigilator1, r.Invigilator2, r.Topics
        }, ct);

    public async Task<ExamPaperResponse?> GetExamPaperAsync(Guid id, CancellationToken ct = default) =>
        (await QueryInlineAsync<ExamPaperResponse>($"SELECT {PaperCols} FROM \"dbo\".\"ExamPapers\" WHERE \"Id\" = @id", new { id }, ct))
        .FirstOrDefault();

    public Task<IReadOnlyList<ExamPaperResponse>> ListExamPapersAsync(Guid? examId, CancellationToken ct = default) =>
        QueryInlineAsync<ExamPaperResponse>(
            $"SELECT {PaperCols} FROM \"dbo\".\"ExamPapers\" WHERE (@examId IS NULL OR \"ExamId\" = @examId) ORDER BY \"Date\"",
            new { examId }, ct);

    public Task<ExamPaperResponse?> UpdateExamPaperAsync(Guid id, UpdateExamPaperRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<ExamPaperResponse>("dbo.ExamPaper_Update", new
        {
            Id = id, r.Name, r.Subject, r.SubjectId, r.Date, r.StartTime, r.DurationMin,
            r.MaxMarks, r.Room, r.Invigilator1, r.Invigilator2, r.Status, r.Topics
        }, ct);

    public Task<int> DeleteExamPaperAsync(Guid id, CancellationToken ct = default) =>
        ExecuteProcAsync("dbo.ExamPaper_Delete", new { Id = id }, ct);

    // Grades
    public Task<GradeResponse?> UpsertGradeAsync(Guid tenantId, UpsertGradeRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<GradeResponse>("dbo.Grade_Upsert",
            new { TenantId = tenantId, r.StudentId, r.StudentName, r.ExamPaperId, r.Marks }, ct);

    public Task<IReadOnlyList<GradeResponse>> ListGradesAsync(Guid examPaperId, CancellationToken ct = default) =>
        QueryInlineAsync<GradeResponse>(
            $"SELECT {GradeCols} FROM \"dbo\".\"Grades\" WHERE \"ExamPaperId\" = @examPaperId ORDER BY \"StudentName\"",
            new { examPaperId }, ct);

    public Task<IReadOnlyList<GradeResponse>> ListGradesForStudentAsync(Guid studentId, CancellationToken ct = default) =>
        QueryInlineAsync<GradeResponse>(@"
SELECT g.""Id"", g.""TenantId"", g.""StudentId"", g.""StudentName"", g.""ExamPaperId"", g.""Marks"", g.""MaxMarks"", g.""Grade"", g.""Gpa"", g.""Pass"", g.""Date"",
       p.""SubjectId"", p.""Subject"", p.""Name"" AS ""PaperName"", COALESCE(e.""Published"", false) AS ""ExamPublished""
FROM ""dbo"".""Grades"" g
LEFT JOIN ""dbo"".""ExamPapers"" p ON p.""Id"" = g.""ExamPaperId""
LEFT JOIN ""dbo"".""Exams"" e ON e.""Id"" = p.""ExamId""
WHERE g.""StudentId"" = @studentId
ORDER BY g.""Date"" DESC, p.""Subject""",
            new { studentId }, ct);

    /// <summary>Letter-grade histogram for every paper in an exam (one round-trip for CRM dashboard).</summary>
    public Task<IReadOnlyList<ExamLetterGradeCount>> CountLetterGradesForExamAsync(Guid examId, CancellationToken ct = default) =>
        QueryInlineAsync<ExamLetterGradeCount>(@"
SELECT trim(g.""Grade"") AS ""Grade"", CAST(COUNT(*) AS int) AS ""Count""
FROM ""dbo"".""Grades"" g
INNER JOIN ""dbo"".""ExamPapers"" p ON p.""Id"" = g.""ExamPaperId""
WHERE p.""ExamId"" = @examId AND g.""Grade"" IS NOT NULL AND trim(g.""Grade"") <> ''
GROUP BY trim(g.""Grade"")",
            new { examId }, ct);

    // Exam attendance — admin roll-call for one paper, distinct from Grades (marks/scores).
    public Task BulkUpsertExamAttendanceAsync(Guid tenantId, Guid examPaperId, Guid? markedBy,
        IReadOnlyList<ExamAttendanceUpsertRow> rows, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(rows.Select(r => new { r.StudentId, r.Status }));
        return ExecuteProcAsync("dbo.ExamAttendance_BulkUpsert",
            new { TenantId = tenantId, ExamPaperId = examPaperId, MarkedBy = markedBy, Rows = json }, ct);
    }

    public Task<IReadOnlyList<ExamAttendanceRecordResponse>> ListExamAttendanceAsync(
        Guid examPaperId, CancellationToken ct = default) =>
        QueryInlineAsync<ExamAttendanceRecordResponse>(
            "SELECT \"Id\", \"TenantId\", \"ExamPaperId\", \"StudentId\", \"Status\", \"MarkedBy\" FROM \"dbo\".\"ExamAttendanceRecords\" " +
            "WHERE \"ExamPaperId\" = @examPaperId ORDER BY \"StudentId\"",
            new { examPaperId }, ct);
}
