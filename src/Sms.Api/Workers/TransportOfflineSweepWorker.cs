using System.Collections.Concurrent;
using Sms.Application.Services.Realtime;
using Sms.Application.Services.Transport;
using Sms.Modules.Transport;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Api.Workers;

/// Periodically finds live trips with no recent ping and broadcasts a
/// status_changed(offline) event to that bus's group — the one state
/// transition no ping will ever announce on its own. Mirrors
/// AbsenceAlertWorker's scope-per-sweep + platform-context pattern.
///
/// It also auto-ends trips on two signals, so a finished or abandoned trip can't
/// keep its bus blocked (Trip_Start's duplicate-active-trip guard rejects a new
/// trip, surfaced as a 409 on trip/start, while any trip for that bus is still
/// 'live'/'arrived'):
///   - Completed (CompletedGrace, short): a pickup that reached school, or a drop
///     whose every stop is covered, that has then sat idle — the normal "driver
///     finished but didn't press End" case, closed promptly.
///   - Abandoned (AutoEndAfter, long): a trip silent far longer than any real
///     one would be — the driver closed the app mid-route, confirming nothing.
///     Route-completion can't catch this (the route was never finished), so the
///     long silence timer is the only backstop; the threshold stays long so a
///     merely-offline trip the driver will resume is never ended under them.
public sealed class TransportOfflineSweepWorker(
    IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<TransportOfflineSweepWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);
    // Clamp floor (5 min) stays well above StaleAfter so the two thresholds can't cross; default 30 min.
    private readonly TimeSpan _autoEndAfter = TimeSpan.FromMinutes(Math.Clamp(config.GetValue<int?>("TransportOfflineSweep:AutoEndMinutes") ?? 30, 5, 720));
    // Idle grace after a trip finishes its route before we close it — short, lets the driver End or start the next leg first.
    private readonly TimeSpan _completedGrace = TimeSpan.FromMinutes(Math.Clamp(config.GetValue<int?>("TransportOfflineSweep:CompletedGraceMinutes") ?? 5, 1, 120));
    private readonly TimeSpan _poll = TimeSpan.FromSeconds(Math.Clamp(config.GetValue<int?>("TransportOfflineSweep:PollSeconds") ?? 20, 5, 300));
    private readonly ConcurrentDictionary<Guid, byte> _offline = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Transport offline sweep failed");
            }
            await Task.Delay(_poll, stoppingToken);
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.Set(null, null, isPlatform: true);
        var repo = scope.ServiceProvider.GetRequiredService<TripRepository>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<ITransportFleetBroadcaster>();
        var live = scope.ServiceProvider.GetRequiredService<ILiveBroadcaster>();

        // Auto-end completed-then-idle and long-abandoned trips first, so they drop out of the
        // stale/offline set below (and free their bus) in the same sweep rather than lingering a cycle.
        await AutoEndTripsAsync(repo, broadcaster, live, ct);

        var stale = await repo.GetStaleActiveTripsAsync(StaleAfter, ct);
        var staleIds = stale.Select(s => s.TripId).ToHashSet();
        var (toNotify, toClear) = TransportOfflineSweepRules.ComputeTransitions(
            new HashSet<Guid>(_offline.Keys), staleIds);

        foreach (var tripId in toNotify)
        {
            var trip = stale.First(s => s.TripId == tripId);
            _offline[tripId] = 0;
            await broadcaster.BroadcastStatusChangedAsync(trip.BusId, tripId, "offline", ct);
        }
        foreach (var tripId in toClear)
            _offline.TryRemove(tripId, out _);
    }

    private async Task AutoEndTripsAsync(
        TripRepository repo, ITransportFleetBroadcaster broadcaster, ILiveBroadcaster live, CancellationToken ct)
    {
        var completed = await repo.GetCompletedTripsToAutoEndAsync(_completedGrace, ct);
        var abandoned = await repo.GetAutoEndCandidatesAsync(_autoEndAfter, ct);

        var ended = new HashSet<Guid>();
        foreach (var (trip, reason) in completed.Select(t => (t, "completed")).Concat(abandoned.Select(t => (t, "abandoned"))))
        {
            // A trip can match both lists (e.g. a long-finished one); end it once.
            if (!ended.Add(trip.TripId)) continue;
            // Mirrors TripService.EndCoreAsync's end + broadcast sequence, but for the platform
            // sweep: the TenantId comes from the row (we run with no tenant context) and the trip
            // may be 'arrived' as well as 'live'. Trip_End is idempotent on an already-ended trip,
            // so a lost broadcast never leaves the state half-applied.
            await repo.EndAsync(trip.TenantId, trip.TripId, ct);
            await broadcaster.BroadcastFleetAsync(trip.TenantId, ct);
            await live.PublishAsync(trip.TenantId, LiveEventTypes.Transport, ct: ct);
            await broadcaster.BroadcastTripEndedAsync(trip.BusId, trip.TripId, DateTime.UtcNow, ct);
            _offline.TryRemove(trip.TripId, out _);
            logger.LogInformation(
                "Auto-ended {Reason} trip {TripId} on bus {BusId} (tenant {TenantId}); last activity {LastActivity:o}",
                reason, trip.TripId, trip.BusId, trip.TenantId, trip.LastPingAt);
        }
    }
}
