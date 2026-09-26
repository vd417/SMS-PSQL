using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sms.Application.Services.Transport;
using Sms.Modules.Transport;
using Sms.Shared.Kernel.Authz;

namespace Sms.Api.Controllers;

[Route("v1/staff")]
[Authorize]
public sealed class TripController(ITripService trips) : ApiControllerBase
{
    [HttpPost("trips")]
    public async Task<IActionResult> Start([FromBody] StartTripRequest req, CancellationToken ct)
    {
        if (!RoleChecks.CanOperateTrips(User))
            return ForbiddenResult("driver or staff only");
        return FromResult(await trips.StartAsync(req, ct));
    }

    [HttpGet("trip/current")]
    public async Task<IActionResult> GetCurrent(CancellationToken ct) =>
        FromResult(await trips.GetCurrentAsync(ct));

    [HttpGet("trip/assignment")]
    public async Task<IActionResult> GetAssignment(CancellationToken ct) =>
        FromResult(await trips.GetAssignmentAsync(ct));

    [HttpGet("trips/{tripId:guid}/roster")]
    public async Task<IActionResult> GetRoster(Guid tripId, CancellationToken ct) =>
        FromResult(await trips.GetRosterAsync(tripId, ct));

    [HttpPost("trips/{tripId:guid}/pings")]
    public async Task<IActionResult> IngestPings(Guid tripId, [FromBody] BulkPingRequest req, CancellationToken ct) =>
        FromResult(await trips.IngestPingsAsync(tripId, req, ct));

    [HttpPost("trips/{tripId:guid}/end")]
    public async Task<IActionResult> End(Guid tripId, CancellationToken ct) =>
        FromResult(await trips.EndAsync(tripId, ct));

    [HttpGet("trips/{tripId:guid}/boarding")]
    public async Task<IActionResult> ListBoarding(Guid tripId, CancellationToken ct) =>
        FromResult(await trips.ListBoardingAsync(tripId, ct));

    [HttpGet("trips/{tripId:guid}/stops")]
    public async Task<IActionResult> GetStopProgress(Guid tripId, CancellationToken ct) =>
        FromResult(await trips.GetStopProgressAsync(tripId, ct));

    [HttpPost("trips/{tripId:guid}/boarding")]
    public async Task<IActionResult> UpsertBoarding(Guid tripId, [FromBody] BoardingRequest req, CancellationToken ct) =>
        FromResult(await trips.UpsertBoardingAsync(tripId, req, ct));

    [HttpPost("trips/{tripId:guid}/stops/{stopId:guid}/confirm-arrival")]
    public async Task<IActionResult> ConfirmStopArrival(Guid tripId, Guid stopId, CancellationToken ct) =>
        FromResult(await trips.ConfirmStopArrivalAsync(tripId, stopId, ct));

    [HttpPost("trips/{tripId:guid}/stops/{stopId:guid}/complete")]
    public async Task<IActionResult> CompleteStop(Guid tripId, Guid stopId, CancellationToken ct) =>
        FromResult(await trips.CompleteStopAsync(tripId, stopId, ct));

    [HttpPost("trips/{tripId:guid}/school-arrived")]
    public async Task<IActionResult> SchoolArrived(Guid tripId, CancellationToken ct) =>
        FromResult(await trips.MarkSchoolArrivedAsync(tripId, ct));
}
