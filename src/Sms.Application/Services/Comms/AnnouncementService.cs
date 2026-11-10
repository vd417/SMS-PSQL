using Sms.Application.Common;
using Sms.Application.Interfaces.DAO;
using Sms.Application.Services.Realtime;
using Sms.Modules.Comms;
using Sms.Modules.Sis.Data;
using Sms.Modules.Staffing.Data;
using Sms.Modules.Tenancy.Data;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Results;
using Sms.Shared.Kernel.Tenancy;
using Microsoft.Extensions.Logging;

namespace Sms.Application.Services.Comms;

public interface IAnnouncementService
{
    Task<ApiResult<IReadOnlyList<AnnouncementResponse>>> ListAsync(string? audience, CancellationToken ct = default);
    Task<ApiResult<AnnouncementResponse>> CreateAsync(
        CreateAnnouncementRequest req, Guid? creatorUserId, string? role, CancellationToken ct = default);
}

public sealed class AnnouncementService(
    CommsRepository repo,
    ITenantContext tenant,
    IEmailQueue emailQueue,
    ISmsSender smsSender,
    INoticePdfGenerator noticePdf,
    ClientRepository clients,
    TeacherRepository teachers,
    StudentRepository students,
    IUserProvisioningDao users,
    ILiveBroadcaster live,
    ILogger<AnnouncementService> logger) : IAnnouncementService
{
    public async Task<ApiResult<IReadOnlyList<AnnouncementResponse>>> ListAsync(string? audience, CancellationToken ct = default) =>
        ApiResult<IReadOnlyList<AnnouncementResponse>>.Ok(await repo.ListAnnouncementsAsync(audience, ct));

    public async Task<ApiResult<AnnouncementResponse>> CreateAsync(
        CreateAnnouncementRequest req, Guid? creatorUserId, string? role, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<AnnouncementResponse>.Fail(new Error("forbidden", "no tenant context"), 403);

        var title = Truncate(req.Title?.Trim() ?? "", 200);
        var body = Truncate(req.Body?.Trim() ?? "", 2000);
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
            return ApiResult<AnnouncementResponse>.Fail(new Error("invalid_request", "title and body are required"), 400);

        var type = Truncate(string.IsNullOrWhiteSpace(req.Type) ? "general" : req.Type.Trim(), 20);
        var audience = Truncate(string.IsNullOrWhiteSpace(req.Audience) ? "everyone" : req.Audience.Trim(), 20);
        var channels = ParseChannels(req.Channels);
        var normalized = new CreateAnnouncementRequest(title, body, type, audience);

        var created = await repo.CreateAnnouncementAsync(tid, normalized, creatorUserId, role, ct);
        if (created is null)
            return ApiResult<AnnouncementResponse>.Fail(new Error("internal_error", "failed to create announcement"), 500);

        await live.PublishAsync(tid, LiveEventTypes.Announcement, ct: ct);

        var recipientsJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            emails = req.Emails,
            phones = req.Phones,
            channels = req.Channels,
            school_name = req.SchoolName,
            event_date = req.EventDate,
            event_kind = req.EventKind,
        });
        await repo.SaveAnnouncementDeliveryAsync(
            created.Id, recipientsJson, req.AttachmentFileName, req.AttachmentContentType, ct);

        var schoolName = req.SchoolName?.Trim();
        if (string.IsNullOrWhiteSpace(schoolName))
        {
            try { schoolName = (await clients.GetAsync(tid, ct))?.Name; }
            catch { /* best-effort */ }
        }
        schoolName = string.IsNullOrWhiteSpace(schoolName) ? "your school" : schoolName!;

        var kind = string.IsNullOrWhiteSpace(req.EventKind)
            ? (type.Equals("calendar", StringComparison.OrdinalIgnoreCase) ? "Calendar" : "Announcement")
            : req.EventKind.Trim();
        var displayTitle = StripCalendarPrefix(title);
        var dateLabel = string.IsNullOrWhiteSpace(req.EventDate) ? null : req.EventDate.Trim();
        var details = ExtractDetails(body, displayTitle, dateLabel, schoolName);

        var emailed = 0;
        var smsed = 0;
        var app = 0;

        if (channels.Contains("app"))
        {
            try
            {
                var noticeTitle = Truncate($"{kind}: {displayTitle}", 200);
                var noticeBody = Truncate(
                    string.IsNullOrWhiteSpace(dateLabel) ? details ?? body : $"{dateLabel} · {details ?? body}",
                    1000);
                var notice = await repo.CreateNotificationAsync(tid, new CreateNotificationRequest(
                    Icon: "bell",
                    Tone: "brand",
                    Title: noticeTitle,
                    Body: noticeBody,
                    UserId: req.UserId), ct);
                if (notice is not null)
                {
                    await NotificationEvents.PublishAsync(live, tid, notice, req.UserId, ct);
                    app = 1;
                    logger.LogInformation(
                        "Announcement {Id} in-app notice {NoticeId} for user {UserId}",
                        created.Id, notice.Id, req.UserId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Announcement {Id} app push failed", created.Id);
            }
        }

        try
        {
            if (channels.Contains("email"))
            {
                var emails = await ResolveRecipientEmailsAsync(tid, audience, req.Emails, ct);
                var notice = AnnouncementNoticeEmail.Build(new AnnouncementNoticeEmail.Model(
                    schoolName, kind, displayTitle, dateLabel, details));
                byte[]? noticePdfBytes = null;
                if (string.IsNullOrWhiteSpace(req.AttachmentBase64))
                {
                    try
                    {
                        noticePdfBytes = noticePdf.Generate(new NoticePdfModel(schoolName, kind, displayTitle, dateLabel, details));
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Notice PDF generation failed for announcement {Id}", created.Id);
                    }
                }
                var noticeFile = $"Catre-Notice-{SanitizeFile(kind)}-{DateTime.UtcNow:yyyyMMdd}.pdf";

                EmailAttachment? userFile = null;
                if (!string.IsNullOrWhiteSpace(req.AttachmentBase64) && !string.IsNullOrWhiteSpace(req.AttachmentFileName))
                {
                    try
                    {
                        var b64 = req.AttachmentBase64!.Trim();
                        var comma = b64.IndexOf(',');
                        if (comma > 0) b64 = b64[(comma + 1)..]; // strip data:*;base64, if present
                        b64 = b64.Replace("\r", "").Replace("\n", "").Replace(" ", "");
                        var bytes = Convert.FromBase64String(b64);
                        if (bytes.Length > 0 && bytes.Length <= 5_000_000)
                        {
                            var ctType = string.IsNullOrWhiteSpace(req.AttachmentContentType)
                                ? GuessContentType(req.AttachmentFileName!)
                                : req.AttachmentContentType.Trim();
                            userFile = new EmailAttachment(bytes, SanitizeAttachmentName(req.AttachmentFileName!), ctType);
                            logger.LogInformation(
                                "Announcement {Id} user attachment decoded: {Name} ({Bytes} bytes, {ContentType})",
                                created.Id, userFile.FileName, userFile.Bytes.Length, userFile.ContentType);
                        }
                        else
                        {
                            logger.LogWarning(
                                "Announcement {Id} user attachment rejected (size={Bytes})",
                                created.Id, bytes.Length);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "User attachment decode failed for announcement {Id}", created.Id);
                    }
                }
                else
                {
                    logger.LogInformation(
                        "Announcement {Id} no user attachment in request (base64={HasB64}, name={HasName})",
                        created.Id,
                        !string.IsNullOrWhiteSpace(req.AttachmentBase64),
                        !string.IsNullOrWhiteSpace(req.AttachmentFileName));
                }

                // Prefer the user's photo/file as the primary attachment; Catre notice PDF is secondary.
                byte[]? primaryBytes;
                string? primaryName;
                string? primaryCt;
                IReadOnlyList<EmailAttachment>? extras;
                if (userFile is not null)
                {
                    primaryBytes = userFile.Bytes;
                    primaryName = userFile.FileName;
                    primaryCt = userFile.ContentType;
                    extras = noticePdfBytes is { Length: > 0 }
                        ? [new EmailAttachment(noticePdfBytes, noticeFile, "application/pdf")]
                        : null;
                }
                else
                {
                    primaryBytes = noticePdfBytes;
                    primaryName = noticeFile;
                    primaryCt = "application/pdf";
                    extras = null;
                }

                emailed = AnnouncementEmailDispatch.Enqueue(
                    emailQueue,
                    emails,
                    notice.Subject,
                    notice.Plain,
                    notice.Html,
                    primaryBytes,
                    primaryName,
                    primaryCt,
                    extras);
                logger.LogInformation(
                    "Announcement {Id} email → {Count} (primary={Primary}, extraPdf={HasExtra})",
                    created.Id, emailed, primaryName, extras is { Count: > 0 });
            }

            if (channels.Contains("sms"))
            {
                var phones = await ResolveRecipientPhonesAsync(tid, audience, req.Phones, ct);
                var smsBody = Truncate($"{schoolName}: {kind} — {displayTitle}" +
                    (dateLabel is null ? "" : $" · {dateLabel}") +
                    (string.IsNullOrWhiteSpace(details) ? "" : $" · {details}"), 320);
                foreach (var phone in phones)
                {
                    await smsSender.SendAsync(phone, smsBody, ct);
                    smsed++;
                }
                logger.LogInformation("Announcement {Id} sms → {Count}", created.Id, smsed);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Announcement {Id} saved but channel dispatch failed", created.Id);
        }

        return ApiResult<AnnouncementResponse>.Ok(created with
        {
            Reach = emailed + smsed + app,
            Emails = req.Emails,
            Phones = req.Phones,
            AttachmentFileName = req.AttachmentFileName,
            AttachmentContentType = req.AttachmentContentType,
        }, 201);
    }

    private static string StripCalendarPrefix(string title) =>
        title.StartsWith("[Calendar]", StringComparison.OrdinalIgnoreCase)
            ? title["[Calendar]".Length..].Trim()
            : title;

    private static string? ExtractDetails(string body, string title, string? dateLabel, string schoolName)
    {
        // Drop noisy "Label: value" lines the admin UI used to send; keep real details.
        var lines = body.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(l =>
            {
                if (l.Equals(title, StringComparison.OrdinalIgnoreCase)) return false;
                if (dateLabel is not null && l.Contains(dateLabel, StringComparison.OrdinalIgnoreCase) && l.StartsWith("Date", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (l.StartsWith("School:", StringComparison.OrdinalIgnoreCase)) return false;
                if (l.StartsWith("PTM:", StringComparison.OrdinalIgnoreCase)) return false;
                if (l.StartsWith("Holiday:", StringComparison.OrdinalIgnoreCase)) return false;
                if (l.StartsWith("Exam:", StringComparison.OrdinalIgnoreCase)) return false;
                if (l.StartsWith("Event:", StringComparison.OrdinalIgnoreCase)) return false;
                if (l.StartsWith("Fee due:", StringComparison.OrdinalIgnoreCase)) return false;
                if (l.StartsWith("Details:", StringComparison.OrdinalIgnoreCase))
                    return true;
                return true;
            })
            .Select(l => l.StartsWith("Details:", StringComparison.OrdinalIgnoreCase) ? l["Details:".Length..].Trim() : l)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.Equals(schoolName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static string SanitizeFile(string kind)
    {
        var chars = kind.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray();
        return chars.Length == 0 ? "Notice" : new string(chars);
    }

    private static string SanitizeAttachmentName(string name)
    {
        var file = Path.GetFileName(name.Trim());
        if (string.IsNullOrWhiteSpace(file)) return "attachment.bin";
        return file;
    }

    private static string GuessContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            _ => "application/octet-stream",
        };
    }

    private static HashSet<string> ParseChannels(IReadOnlyList<string>? channels)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (channels is null || channels.Count == 0)
        {
            set.Add("email"); set.Add("sms"); set.Add("app");
            return set;
        }
        foreach (var c in channels)
        {
            var v = (c ?? "").Trim().ToLowerInvariant();
            if (v is "email" or "sms" or "app" or "push") set.Add(v == "push" ? "app" : v);
        }
        if (set.Count == 0) { set.Add("email"); set.Add("sms"); set.Add("app"); }
        return set;
    }

    private async Task<IReadOnlyList<string>> ResolveRecipientEmailsAsync(
        Guid tenantId, string audience, IReadOnlyList<string>? explicitEmails, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (explicitEmails is { Count: > 0 })
            foreach (var e in explicitEmails) AddEmail(set, e);

        var key = audience.Trim().ToLowerInvariant();
        // "specific" means exactly that: only the explicit recipients passed in, no roster
        // expansion — used by callers targeting one individual (e.g. one student's guardian).
        if (key is "specific")
            return set.ToList();

        if (key is "everyone" or "" or "all")
        {
            await AddAllSchoolEmailsAsync(tenantId, set, ct);
            return set.ToList();
        }

        if (key is "teachers")
        {
            foreach (var t in await teachers.ListAsync(null, null, null, ct))
                AddEmail(set, t.Email);
            await AddUserEmailsByRoleAsync(tenantId, set, Policies.Teacher, ct);
            await AddUserEmailsByRoleAsync(tenantId, set, Policies.Principal, ct);
        }
        else if (key is "staff")
        {
            await AddUserEmailsByRoleAsync(tenantId, set, Policies.Staff, ct);
            await AddUserEmailsByRoleAsync(tenantId, set, Policies.SchoolAdmin, ct);
        }
        else if (key is "parents")
        {
            foreach (var s in await students.ListAsync(null, null, null, null, ct))
                AddEmail(set, s.Email);
            await AddUserEmailsByRoleAsync(tenantId, set, Policies.StudentOrParent, ct);
        }
        else if (key is "students" or "grades" or "defaulters")
        {
            foreach (var s in await students.ListAsync(null, null, null, null, ct))
                AddEmail(set, s.Email);
        }
        else
            await AddAllSchoolEmailsAsync(tenantId, set, ct);

        return set.ToList();
    }

    private async Task<IReadOnlyList<string>> ResolveRecipientPhonesAsync(
        Guid tenantId, string audience, IReadOnlyList<string>? explicitPhones, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (explicitPhones is { Count: > 0 })
            foreach (var p in explicitPhones) AddPhone(set, p);

        var key = audience.Trim().ToLowerInvariant();
        // "specific" means exactly that: only the explicit recipients passed in, no roster
        // expansion — used by callers targeting one individual (e.g. one student's guardian).
        if (key is "specific")
            return set.ToList();

        if (key is "teachers" or "everyone" or "" or "all")
        {
            foreach (var t in await teachers.ListAsync(null, null, null, ct))
                AddPhone(set, t.Phone);
            await AddUserPhonesByRoleAsync(tenantId, set, Policies.Teacher, ct);
        }

        if (key is "staff" or "everyone" or "" or "all")
            await AddUserPhonesByRoleAsync(tenantId, set, Policies.Staff, ct);

        if (key is "parents" or "students" or "everyone" or "" or "all" or "grades" or "defaulters")
        {
            foreach (var s in await students.ListAsync(null, null, null, null, ct))
                AddPhone(set, s.GuardianPhone);
            if (key is "parents" or "everyone" or "" or "all")
                await AddUserPhonesByRoleAsync(tenantId, set, Policies.StudentOrParent, ct);
        }

        return set.ToList();
    }

    private async Task AddAllSchoolEmailsAsync(Guid tenantId, HashSet<string> set, CancellationToken ct)
    {
        foreach (var t in await teachers.ListAsync(null, null, null, ct))
            AddEmail(set, t.Email);
        foreach (var s in await students.ListAsync(null, null, null, null, ct))
            AddEmail(set, s.Email);
        foreach (var u in await users.ListByTenantAsync(tenantId, ct))
            AddEmail(set, u.Email);
    }

    private async Task AddUserEmailsByRoleAsync(Guid tenantId, HashSet<string> set, string role, CancellationToken ct)
    {
        foreach (var u in await users.ListByTenantAsync(tenantId, ct))
        {
            if (!UserHasRole(u, role)) continue;
            AddEmail(set, u.Email);
        }
    }

    private async Task AddUserPhonesByRoleAsync(Guid tenantId, HashSet<string> set, string role, CancellationToken ct)
    {
        foreach (var u in await users.ListByTenantAsync(tenantId, ct))
        {
            if (!UserHasRole(u, role)) continue;
            AddPhone(set, u.Phone);
        }
    }

    private static bool UserHasRole(SchoolUserListRow u, string role)
    {
        var roles = (u.Roles ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return roles.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddEmail(HashSet<string> set, string? email)
    {
        var v = (email ?? "").Trim();
        if (v.Contains('@') && v.Length > 3) set.Add(v);
    }

    private static void AddPhone(HashSet<string> set, string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length >= 10) set.Add(digits);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
