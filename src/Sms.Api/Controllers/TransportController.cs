using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sms.Application.Services.Transport;
using Sms.Modules.Transport;
using Sms.Shared.Kernel.Authz;

namespace Sms.Api.Controllers;

/// Optional stop the child boards at, supplied when assigning a student to a bus.
public sealed record AssignStudentBusRequest(Guid? StopId);

public sealed record AssignBusTeacherRequest(Guid TeacherUserId);

public sealed record CreateBusRequest(
    string BusNo, string? RouteName, Guid? RouteId, string? Driver, string? DriverPhone, Guid? DriverStaffId,
    Guid? ConductorStaffId = null, int? Capacity = null);

public sealed record UpdateBusRequest(
    string? BusNo, Guid? RouteId, Guid? DriverStaffId, bool ClearDriver = false,
    Guid? ConductorStaffId = null, bool ClearConductor = false, int? Capacity = null, bool ClearCapacity = false);

public sealed record CreateRouteRequest(string Name, int? Stops);

public sealed record CreateRouteStopRequest(string Name, double Lat, double Lng);

public sealed record UpdateRouteStopRequest(string Name, double Lat, double Lng);

public sealed record ReorderRouteStopsRequest(IReadOnlyList<Guid> StopIds);

public sealed record StartBusTripRequest(string? Direction);

/// School-admin transport surface (Operations screen). Distinct from the teacher-app /v1/bus routes.
[Route("v1/transport")]
[Authorize(Policy = Policies.Principal)]
public sealed class TransportController(IBusService bus, IStudentBusService studentBus, IBusNotifyService notify) : ApiControllerBase
{
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct) =>
        FromResult(await bus.GetSummaryAsync(ct));

    [HttpGet("fleet")]
    public async Task<IActionResult> Fleet(CancellationToken ct) =>
        FromResult(await bus.GetFleetAsync(ct));

    [HttpGet("buses")]
    public async Task<IActionResult> ListBuses(CancellationToken ct) =>
        FromResult(await bus.ListBusesAsync(ct));

    [HttpPost("buses")]
    public async Task<IActionResult> CreateBus([FromBody] CreateBusRequest req, CancellationToken ct) =>
        FromResult(await bus.CreateBusAsync(req.BusNo, req.RouteName, req.RouteId, req.Driver, req.DriverPhone, req.DriverStaffId, req.ConductorStaffId, req.Capacity, ct));

    [HttpPut("buses/{busId:guid}")]
    public async Task<IActionResult> UpdateBus(
        Guid busId, [FromBody] UpdateBusRequest req, CancellationToken ct) =>
        FromResult(await bus.UpdateBusAsync(busId, req.BusNo, req.RouteId, req.DriverStaffId, req.ClearDriver, req.ConductorStaffId, req.ClearConductor, req.Capacity, req.ClearCapacity, ct));

    [HttpGet("buses/{busId:guid}/assignment-history")]
    public async Task<IActionResult> AssignmentHistory(Guid busId, CancellationToken ct) =>
        FromResult(await bus.GetAssignmentHistoryAsync(busId, ct));

    [HttpPut("buses/{busId:guid}/teacher")]
    public async Task<IActionResult> AssignTeacher(
        Guid busId, [FromBody] AssignBusTeacherRequest req, CancellationToken ct) =>
        FromResult(await bus.AssignTeacherAsync(busId, req.TeacherUserId, ct));

    [HttpDelete("buses/{busId:guid}/teacher")]
    public async Task<IActionResult> UnassignTeacher(Guid busId, CancellationToken ct) =>
        FromResult(await bus.UnassignTeacherAsync(busId, ct));

    [HttpGet("buses/{busId:guid}/traveling-teachers")]
    public async Task<IActionResult> ListTravelingTeachers(Guid busId, CancellationToken ct) =>
        FromResult(await bus.ListTravelingTeachersAsync(busId, ct));

    [HttpPut("buses/{busId:guid}/traveling-teachers/{teacherUserId:guid}")]
    public async Task<IActionResult> AddTravelingTeacher(Guid busId, Guid teacherUserId, CancellationToken ct) =>
        FromResult(await bus.AddTravelingTeacherAsync(busId, teacherUserId, ct));

    [HttpDelete("buses/{busId:guid}/traveling-teachers/{teacherUserId:guid}")]
    public async Task<IActionResult> RemoveTravelingTeacher(Guid busId, Guid teacherUserId, CancellationToken ct) =>
        FromResult(await bus.RemoveTravelingTeacherAsync(busId, teacherUserId, ct));

    [HttpGet("buses/{busId:guid}/students")]
    public async Task<IActionResult> BusStudents(Guid busId, CancellationToken ct) =>
        FromResult(await studentBus.ListByBusAsync(busId, ct));

    [HttpGet("students")]
    public async Task<IActionResult> ListMappedStudents(
        [FromQuery] Guid? routeId, [FromQuery] Guid? stopId, [FromQuery] Guid? busId,
        [FromQuery] string? grade, [FromQuery] Guid? feeHeadId, [FromQuery] string? status,
        CancellationToken ct) =>
        FromResult(await studentBus.ListMappedAsync(
            new TransportStudentsFilter(routeId, stopId, busId, grade, feeHeadId, status), ct));

    [HttpPut("buses/{busId:guid}/students/{studentId:guid}")]
    public async Task<IActionResult> AssignStudent(
        Guid busId, Guid studentId, [FromBody] AssignStudentBusRequest? req, CancellationToken ct) =>
        FromResult(await studentBus.AssignAsync(busId, studentId, req?.StopId, ct));

    [HttpDelete("buses/{busId:guid}/students/{studentId:guid}")]
    public async Task<IActionResult> UnassignStudent(Guid busId, Guid studentId, CancellationToken ct) =>
        FromResult(await studentBus.UnassignAsync(studentId, ct));

    [HttpPost("buses/{busId:guid}/notify")]
    public async Task<IActionResult> NotifyParents(
        Guid busId, [FromBody] SendBusNotificationRequest req, CancellationToken ct) =>
        FromResult(await notify.NotifyAsync(busId, req, ct));

    [HttpGet("routes")]
    public async Task<IActionResult> ListRoutes(CancellationToken ct) =>
        FromResult(await bus.ListRoutesAsync(ct));

    [HttpPost("routes")]
    public async Task<IActionResult> CreateRoute([FromBody] CreateRouteRequest req, CancellationToken ct) =>
        FromResult(await bus.CreateRouteAsync(req.Name, req.Stops ?? 1));

    [HttpDelete("routes/{routeId:guid}")]
    public async Task<IActionResult> DeleteRoute(Guid routeId, CancellationToken ct) =>
        FromResult(await bus.DeleteRouteAsync(routeId, ct));

    [HttpGet("routes/{routeId:guid}/stops")]
    public async Task<IActionResult> ListRouteStops(Guid routeId, CancellationToken ct) =>
        FromResult(await bus.ListRouteStopsAsync(routeId));

    [HttpPost("routes/{routeId:guid}/stops")]
    public async Task<IActionResult> CreateRouteStop(
        Guid routeId, [FromBody] CreateRouteStopRequest req, CancellationToken ct) =>
        FromResult(await bus.CreateRouteStopAsync(routeId, req.Name, req.Lat, req.Lng));

    [HttpPut("routes/{routeId:guid}/stops/{stopId:guid}")]
    public async Task<IActionResult> UpdateRouteStop(
        Guid routeId, Guid stopId, [FromBody] UpdateRouteStopRequest req, CancellationToken ct) =>
        FromResult(await bus.UpdateRouteStopAsync(routeId, stopId, req.Name, req.Lat, req.Lng));

    [HttpDelete("routes/{routeId:guid}/stops/{stopId:guid}")]
    public async Task<IActionResult> DeleteRouteStop(Guid routeId, Guid stopId, CancellationToken ct) =>
        FromResult(await bus.DeleteRouteStopAsync(routeId, stopId));

    [HttpPut("routes/{routeId:guid}/stops/reorder")]
    public async Task<IActionResult> ReorderRouteStops(
        Guid routeId, [FromBody] ReorderRouteStopsRequest req, CancellationToken ct) =>
        FromResult(await bus.ReorderRouteStopsAsync(routeId, req.StopIds));

    [HttpPost("buses/{busId:guid}/trip/start")]
    public async Task<IActionResult> StartBusTrip(
        Guid busId, [FromBody] StartBusTripRequest? req, CancellationToken ct) =>
        FromResult(await bus.StartBusTripAsync(busId, req?.Direction ?? "pickup"));

    [HttpPost("buses/{busId:guid}/trip/pings")]
    public async Task<IActionResult> IngestBusTripPings(
        Guid busId, [FromBody] BulkPingRequest req, CancellationToken ct) =>
        FromResult(await bus.IngestBusTripPingsAsync(busId, req));

    [HttpPost("buses/{busId:guid}/trip/end")]
    public async Task<IActionResult> EndBusTrip(Guid busId, CancellationToken ct) =>
        FromResult(await bus.EndBusTripAsync(busId));
}
