using Sms.Application.Services.Attendance;
using Sms.Modules.Academics.Data;
using Sms.Modules.Attendance;
using Sms.Modules.Comms;
using Sms.Modules.Sis.Data;
using Sms.Modules.Tenancy.Data;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Api.Workers;

/// <summary>
/// Server-side daily absence-alert scheduler. Wakes on a low-frequency poll, finds tenants whose
/// per-school schedule is due (auto-send on, wall-clock time passed, not yet sent today), computes
/// consecutive-absence streaks from persisted <c>AttendanceRecords</c>, then raises an in-app notice
/// and (for streaks past the email threshold) queues escalation emails to guardians.
///
/// Load profile: one platform read per poll, then one small scope per *due* tenant per day.
/// </summary>
public sealed class AbsenceAlertWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<AbsenceAlertWorker> logger) : BackgroundService
{
    private readonly TimeSpan _poll =
        TimeSpan.FromMinutes(Math.Clamp(config.GetValue<int?>("AbsenceAlerts:PollMinutes") ?? 15, 1, 240));
    private readonly TimeSpan _offset = ParseOffset(config["AbsenceAlerts:UtcOffset"]);
    private readonly int _lookbackDays =
        Math.Clamp(config.GetValue<int?>("AbsenceAlerts:LookbackDays") ?? 45, 7, 180);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting before the first sweep.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Absence-alert sweep failed"); }

            try { await Task.Delay(_poll, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var localNow = DateTimeOffset.UtcNow.ToOffset(_offset);
        var today = localNow.Date;
        var nowMinutes = localNow.Hour * 60 + localNow.Minute;

        IReadOnlyList<DueAlertTenant> due;
        using (var scope = scopeFactory.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(null, null, true);
            var repo = scope.ServiceProvider.GetRequiredService<AttendanceAlertConfigRepository>();
            due = await repo.ListDueAsync(today, nowMinutes, ct);
        }

        if (due.Count == 0) return;
        logger.LogInformation("Absence-alert sweep: {Count} tenant(s) due at {Local:HH:mm}", due.Count, localNow);

        foreach (var tenant in due)
        {
            if (ct.IsCancellationRequested) break;
            try { await ProcessTenantAsync(tenant, today, ct); }
            catch (Exception ex) { logger.LogError(ex, "Absence-alert send failed for tenant {Tenant}", tenant.TenantId); }
        }
    }

    private async Task ProcessTenantAsync(DueAlertTenant cfg, DateTime today, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().Set(cfg.TenantId, null, false);

        var configRepo = sp.GetRequiredService<AttendanceAlertConfigRepository>();
        var attendance = sp.GetRequiredService<AttendanceRepository>();

        var marks = await attendance.ListSinceAsync(today.AddDays(-_lookbackDays), ct);
        var flagged = AbsenceStreak.Flag(marks.Select(m => (m.StudentId, m.Date, m.Status)), cfg.NoticeDays);

        if (flagged.Count == 0)
        {
            // Still record the sweep so we don't re-run for this tenant until tomorrow.
            await configRepo.MarkAutoSentAsync(cfg.TenantId, today, ct);
            return;
        }

        var students = (await sp.GetRequiredService<StudentRepository>().ListAsync(null, null, null, null, ct))
            .ToDictionary(s => s.Id);
        var schoolName = (await sp.GetRequiredService<ClientRepository>().GetAsync(cfg.TenantId, ct))?.Name ?? "your school";
        var comms = sp.GetRequiredService<CommsRepository>();

        var names = string.Join(", ", flagged.Take(6)
            .Select(f => students.TryGetValue(f.StudentId, out var s) ? s.Name : "a student"));
        var moreSuffix = flagged.Count > 6 ? $" +{flagged.Count - 6} more" : "";
        var summaryTitle = $"Absence alerts · {flagged.Count} flagged";

        // Always drop an in-app notice for the office.
        var officeNotice = await comms.CreateNotificationAsync(cfg.TenantId, new CreateNotificationRequest(
            "bell", "warn", summaryTitle,
            $"{flagged.Count} absent {cfg.NoticeDays}+ day(s) in a row: {names}{moreSuffix}"), ct);
        if (officeNotice is not null)
            await Sms.Application.Services.Comms.NotificationEvents.PublishAsync(
                sp.GetRequiredService<Sms.Application.Services.Realtime.ILiveBroadcaster>(),
                cfg.TenantId, officeNotice, null, ct);

        // Persist an announcement row for the history feed.
        await comms.CreateAnnouncementAsync(cfg.TenantId,
            new CreateAnnouncementRequest(summaryTitle, BuildAnnouncementBody(flagged, students, cfg), "attendance", "parents"),
            null, "system", ct);

        // Email escalation for streaks at/above the email threshold, when the channel allows it.
        var emailed = 0;
        var channel = (cfg.AutoChannel ?? "app").Trim().ToLowerInvariant();
        if (channel == "email")
        {
            var emails = flagged
                .Where(f => f.Streak >= cfg.EmailDays)
                .Select(f => students.TryGetValue(f.StudentId, out var s) ? s.Email : null)
                .Where(e => !string.IsNullOrWhiteSpace(e) && e!.Contains('@'))
                .Select(e => e!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (emails.Count > 0)
            {
                var notice = AnnouncementNoticeEmail.Build(new AnnouncementNoticeEmail.Model(
                    schoolName,
                    "Attendance alert",
                    "Consecutive absences noticed",
                    today.ToString("dd MMM yyyy"),
                    $"Our records show {cfg.EmailDays}+ school days of consecutive absence. " +
                    "Please contact the school office if this is unexpected or if your ward is unwell."));
                var from = await sp.GetRequiredService<Sms.Application.Services.Tenancy.ISchoolMailIdentity>()
                    .ForTenantAsync(cfg.TenantId, ct);
                emailed = AnnouncementEmailDispatch.Enqueue(
                    sp.GetRequiredService<IEmailQueue>(), emails, notice.Subject, notice.Plain, notice.Html, sender: from);
            }
        }

        await configRepo.MarkAutoSentAsync(cfg.TenantId, today, ct);
        logger.LogInformation(
            "Absence alerts for tenant {Tenant}: {Flagged} flagged, {Emailed} email(s) queued",
            cfg.TenantId, flagged.Count, emailed);
    }

    private static string BuildAnnouncementBody(
        IReadOnlyList<AbsenceStreak.Flagged> flagged,
        IReadOnlyDictionary<Guid, Sms.Modules.Sis.Contracts.StudentResponse> students,
        DueAlertTenant cfg)
    {
        var lines = flagged.Select(f =>
        {
            var label = students.TryGetValue(f.StudentId, out var s)
                ? $"{s.Name}{(string.IsNullOrWhiteSpace(s.ClassLabel) ? "" : $" ({s.ClassLabel})")}"
                : f.StudentId.ToString();
            return $"• {label} — {f.Streak} day(s), last absent {f.LastDate:dd MMM}";
        });
        return $"Students absent {cfg.NoticeDays}+ days in a row:\n" + string.Join("\n", lines);
    }

    private static TimeSpan ParseOffset(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return TimeSpan.FromMinutes(330); // default IST (+05:30)
        var negative = s.StartsWith('-');
        s = s.TrimStart('+', '-');
        return TimeSpan.TryParse(s, out var ts) ? (negative ? -ts : ts) : TimeSpan.FromMinutes(330);
    }
}
