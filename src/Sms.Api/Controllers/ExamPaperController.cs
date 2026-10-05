using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sms.Application.Services.Academics;
using Sms.Application.Services.Sis;
using Sms.Modules.Academics.Contracts;
using Sms.Shared.Kernel.Authz;

namespace Sms.Api.Controllers;

[Route("v1")]
[Authorize]
public sealed class ExamPaperController(
    IAcademicsService academics,
    IExamMarksNotifyService marksNotify,
    ISisService sis) : ApiControllerBase
{
    [HttpGet("exam-papers")]
    public async Task<IActionResult> List(
        [FromQuery(Name = "exam_id")] Guid? examId,
        [FromQuery(Name = "student_id")] Guid? studentId,
        CancellationToken ct)
    {
        if (studentId is Guid sid)
        {
            if (!RoleChecks.IsStaff(User) && !await sis.IsLinkedToCallerAsync(sid, ct))
                return ForbiddenResult("not your linked student");
            return FromResult(await academics.ListExamPapersForStudentAsync(examId, sid, ct));
        }

        var me = await sis.GetMyStudentAsync(ct);
        if (me.IsSuccess)
            return FromResult(await academics.ListExamPapersForStudentAsync(examId, me.Data!.Id, ct));

        return FromResult(await academics.ListExamPapersAsync(examId, ct));
    }

    [HttpGet("exam-papers/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        FromResult(await academics.GetExamPaperAsync(id, ct));

    [HttpPost("exam-papers")]
    [Authorize(Policy = AuthorizationPolicies.TeacherApp)]
    public async Task<IActionResult> Create([FromBody] CreateExamPaperRequest req, CancellationToken ct) =>
        FromResult(await academics.CreateExamPaperAsync(req, ct));

    [HttpPatch("exam-papers/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.TeacherApp)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateExamPaperRequest req, CancellationToken ct) =>
        FromResult(await academics.UpdateExamPaperAsync(id, req, User, ct));

    [HttpDelete("exam-papers/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.TeacherApp)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        FromResult(await academics.DeleteExamPaperAsync(id, User, ct));

    [HttpGet("exam-papers/{id:guid}/attendance")]
    public async Task<IActionResult> ListAttendance(Guid id, CancellationToken ct) =>
        FromResult(await academics.ListExamAttendanceAsync(id, ct));

    [HttpPut("exam-papers/{id:guid}/attendance")]
    [Authorize(Policy = AuthorizationPolicies.TeacherApp)]
    public async Task<IActionResult> BulkUpsertAttendance(
        Guid id, [FromBody] BulkExamAttendanceRequest req, CancellationToken ct) =>
        FromResult(await academics.BulkUpsertExamAttendanceAsync(id, req, ct));

    [HttpPost("exam-papers/{id:guid}/notify-marks")]
    [Authorize(Policy = AuthorizationPolicies.TeacherApp)]
    public async Task<IActionResult> NotifyMarks(Guid id, CancellationToken ct) =>
        FromResult(await marksNotify.NotifyPublishedAsync(id, ct));
}
