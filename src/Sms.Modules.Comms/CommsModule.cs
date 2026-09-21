using Microsoft.Extensions.DependencyInjection;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Comms;

// Online is a trailing init-only property with a secondary 10-param constructor, not a
// primary-constructor parameter: Dapper materializes via a constructor matching the exact
// column count of whatever query ran, and Thread_Create still returns the original 9
// columns while ListThreadsAsync now returns 10 (+ Online).
public sealed record ChatThreadResponse(
    Guid Id, Guid TenantId, string Name, string? Role, string? LastMessage, DateTime? LastAt,
    int Unread, bool Group, Guid? ChildId)
{
    public bool Online { get; init; }
    // Which student this parent thread is about — lets a teacher/principal with
    // several parent threads tell them apart at a glance. Null for staff threads.
    public string? ChildName { get; init; }
    public string? ChildClassLabel { get; init; }
    // The Inbox list's read-receipt tick: whether the OWNER sent the last message (only then
    // does a tick make sense — an incoming last message never shows one), and its delivery
    // state. Null status means the thread has no messages yet.
    public bool LastMessageMine { get; init; }
    public string? LastMessageStatus { get; init; }

    public ChatThreadResponse(
        Guid Id, Guid TenantId, string Name, string? Role, string? LastMessage, DateTime? LastAt,
        int Unread, bool Group, Guid? ChildId, bool Online)
        : this(Id, TenantId, Name, Role, LastMessage, LastAt, Unread, Group, ChildId) =>
        this.Online = Online;

    public ChatThreadResponse(
        Guid Id, Guid TenantId, string Name, string? Role, string? LastMessage, DateTime? LastAt,
        int Unread, bool Group, Guid? ChildId, bool Online, string? ChildName, string? ChildClassLabel)
        : this(Id, TenantId, Name, Role, LastMessage, LastAt, Unread, Group, ChildId, Online)
    {
        this.ChildName = ChildName;
        this.ChildClassLabel = ChildClassLabel;
    }

    public ChatThreadResponse(
        Guid Id, Guid TenantId, string Name, string? Role, string? LastMessage, DateTime? LastAt,
        int Unread, bool Group, Guid? ChildId, bool Online, string? ChildName, string? ChildClassLabel,
        bool LastMessageMine, string? LastMessageStatus)
        : this(Id, TenantId, Name, Role, LastMessage, LastAt, Unread, Group, ChildId, Online, ChildName, ChildClassLabel)
    {
        this.LastMessageMine = LastMessageMine;
        this.LastMessageStatus = LastMessageStatus;
    }
}
/// <param name="ContactKind">"teacher" | "staff" | "student" (student ⇒ message that student's
/// parent) | "user" (another CRM login account, addressed by its own Users id) — when set with
/// <paramref name="ContactId"/>, the server resolves the real recipient account instead of
/// relying on free-text Name matching.</param>
/// <param name="ContactId">The Teachers/Staff/Students row id (not a Users id) matching
/// <paramref name="ContactKind"/> — except "user", where it IS the Users id directly.</param>
public sealed record CreateThreadRequest(
    string Name, string? Role, bool Group, Guid? ChildId,
    string? ContactKind = null, Guid? ContactId = null);
public sealed record ChatMessageResponse(
    Guid Id, Guid ThreadId, Guid? SenderId, string Text, DateTime SentAt, bool IsMine, string? ImageUrl)
{
    public DateTime? DeliveredAt { get; init; }
    public DateTime? ReadAt { get; init; }
    public bool IsDelivered => DeliveredAt is not null || ReadAt is not null;
    public bool IsRead => ReadAt is not null;
}
public sealed record SendMessageRequest(string? Text, string? ImageUrl);
public sealed record AnnouncementResponse(
    Guid Id, Guid TenantId, string Title, string? Body, DateTime Date, string? From, string? Role,
    string Type, bool Pinned, string? Audience)
{
    /// Set after create — how many emails were queued (not a DB column).
    public int Reach { get; init; }
    public IReadOnlyList<string>? Emails { get; init; }
    public IReadOnlyList<string>? Phones { get; init; }
    public string? AttachmentFileName { get; init; }
    public string? AttachmentContentType { get; init; }
}
public sealed record CreateAnnouncementRequest(
    string Title,
    string Body,
    string? Type,
    string? Audience,
    IReadOnlyList<string>? Emails = null,
    IReadOnlyList<string>? Phones = null,
    IReadOnlyList<string>? Channels = null,
    string? SchoolName = null,
    string? EventDate = null,
    string? EventKind = null,
    /// Optional user-uploaded file (base64, no data: prefix) attached to email.
    string? AttachmentBase64 = null,
    string? AttachmentFileName = null,
    string? AttachmentContentType = null,
    /// When set, the "app" channel's in-app notification targets only this user instead of
    /// broadcasting tenant-wide. Has no effect on the email/sms channels.
    Guid? UserId = null);
public sealed record ComplaintResponse(
    Guid Id, Guid TenantId, string Subject, string? From, string? Category, string Priority, string Status,
    string? Age, string? Assignee, string? Body);
public sealed record CreateComplaintRequest(string Subject, string? From, string? Category, string? Priority, string? Body);
public sealed record UpdateComplaintRequest(string? Status, string? Assignee);
public sealed record NotificationResponse(
    Guid Id, Guid TenantId, string? Icon, string? Tone, string Title, string? Body, string? Time, bool Unread);
public sealed record CreateNotificationRequest(string? Icon, string? Tone, string Title, string? Body, Guid? UserId = null);

public sealed class CommsRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private sealed record MessageRow(
        Guid Id, Guid ThreadId, Guid? SenderId, string Text, string? ImageUrl, DateTime SentAt,
        DateTime? DeliveredAt, DateTime? ReadAt);
    private sealed record ThreadInfoRow(Guid Id, string Name, string? Role, Guid OwnerUserId, bool IsGroup, Guid? ContactUserId, Guid? ChildId);
    private sealed record UserIdRow(Guid Id);
    private sealed record UserNameRow(string? Name);
    private sealed record RoleLabelRow(string? RoleLabel);

    private const string ComplaintCols = "\"Id\", \"TenantId\", \"Subject\", \"From\", \"Category\", \"Priority\", \"Status\", \"Age\", \"Assignee\", \"Body\"";
    private const string NotificationCols = "\"Id\", \"TenantId\", \"Icon\", \"Tone\", \"Title\", \"Body\", \"Time\", \"Unread\"";

    public Task<IReadOnlyList<ComplaintResponse>> ListComplaintsAsync(
        string? status, Guid? createdByUserId, CancellationToken ct = default) =>
        QueryInlineAsync<ComplaintResponse>(
            $"SELECT {ComplaintCols} FROM \"dbo\".\"Complaints\" WHERE (@status::text IS NULL OR \"Status\" = @status::text) " +
            "AND (@createdByUserId::uuid IS NULL OR \"CreatedByUserId\" = @createdByUserId::uuid) ORDER BY \"Priority\"",
            new { status, createdByUserId }, ct);

    public async Task<ComplaintResponse?> CreateComplaintAsync(
        Guid tenantId, CreateComplaintRequest r, Guid? createdByUserId, CancellationToken ct = default)
    {
        var created = await QuerySingleProcAsync<ComplaintResponse>("dbo.Complaint_Create",
            new { TenantId = tenantId, r.Subject, r.From, r.Category, r.Priority, r.Body }, ct);
        if (created is null || createdByUserId is null) return created;
        await ExecuteInlineAsync(
            "UPDATE \"dbo\".\"Complaints\" SET \"CreatedByUserId\" = @createdByUserId WHERE \"Id\" = @id",
            new { id = created.Id, createdByUserId }, ct);
        return created;
    }

    public async Task<ComplaintResponse?> GetComplaintAsync(Guid id, Guid tenantId, CancellationToken ct = default) =>
        (await QueryInlineAsync<ComplaintResponse>(
            $"SELECT {ComplaintCols} FROM \"dbo\".\"Complaints\" WHERE \"Id\" = @id AND \"TenantId\" = @tenantId", new { id, tenantId }, ct))
        .FirstOrDefault();

    public Task<ComplaintResponse?> UpdateComplaintAsync(Guid id, Guid tenantId, string? status, string? assignee, CancellationToken ct = default) =>
        QuerySingleProcAsync<ComplaintResponse>(
            "dbo.Complaint_Update", new { Id = id, TenantId = tenantId, Status = status, Assignee = assignee }, ct);

    public Task<IReadOnlyList<NotificationResponse>> ListNotificationsAsync(Guid? userId, CancellationToken ct = default) =>
        QueryInlineAsync<NotificationResponse>(
            $"SELECT {NotificationCols} FROM \"dbo\".\"Notifications\" WHERE \"UserId\" IS NULL OR \"UserId\" = @userId ORDER BY \"Unread\" DESC, \"Time\" DESC",
            new { userId }, ct);

    public Task<int> MarkNotificationsReadAsync(Guid? userId, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            "UPDATE \"dbo\".\"Notifications\" SET \"Unread\" = false WHERE \"Unread\" = true AND (\"UserId\" IS NULL OR \"UserId\" = @userId)",
            new { userId }, ct);

    public Task<NotificationResponse?> CreateNotificationAsync(Guid tenantId, CreateNotificationRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<NotificationResponse>("dbo.Notification_Create",
            new { TenantId = tenantId, r.Icon, r.Tone, r.Title, r.Body, r.UserId }, ct);

    // ContactUserId is the real link where present (set explicitly on create, or backfilled);
    // older threads without one fall back to matching Name against Users, same as delivery.
    public Task<IReadOnlyList<ChatThreadResponse>> ListThreadsAsync(Guid ownerUserId, CancellationToken ct = default) =>
        QueryInlineAsync<ChatThreadResponse>(@"
SELECT th.""Id"", th.""TenantId"", th.""Name"", th.""Role"", th.""LastMessage"", th.""LastAt"", th.""Unread"", th.""IsGroup"" AS ""Group"", th.""ChildId"",
       (u.""LastSeenAt"" IS NOT NULL AND u.""LastSeenAt"" > (now() AT TIME ZONE 'UTC') - interval '5 minutes') AS ""Online"",
       c.""Name"" AS ""ChildName"", c.""ClassLabel"" AS ""ChildClassLabel"",
       (lm.""SenderId"" = th.""OwnerUserId"") AS ""LastMessageMine"",
       CASE
           WHEN lm.""SenderId"" IS NULL THEN NULL
           WHEN lm.""ReadAt"" IS NOT NULL THEN 'read'
           WHEN lm.""DeliveredAt"" IS NOT NULL THEN 'delivered'
           ELSE 'sent'
       END AS ""LastMessageStatus""
FROM ""dbo"".""ChatThreads"" th
LEFT JOIN ""dbo"".""Users"" u
    ON (th.""ContactUserId"" IS NOT NULL AND u.""Id"" = th.""ContactUserId"")
    OR (th.""ContactUserId"" IS NULL AND u.""TenantId"" = th.""TenantId"" AND u.""Name"" = th.""Name"")
LEFT JOIN ""dbo"".""Students"" c ON c.""Id"" = th.""ChildId""
LEFT JOIN LATERAL (
    SELECT m.""SenderId"", m.""DeliveredAt"", m.""ReadAt""
    FROM ""dbo"".""ChatMessages"" m
    WHERE m.""ThreadId"" = th.""Id""
    ORDER BY m.""SentAt"" DESC
    LIMIT 1
) lm ON true
WHERE th.""OwnerUserId"" = @ownerUserId
ORDER BY th.""LastAt"" DESC", new { ownerUserId }, ct);

    public async Task<ChatThreadResponse?> CreateThreadAsync(
        Guid tenantId, Guid ownerUserId, CreateThreadRequest r, CancellationToken ct = default)
    {
        var contactUserId = await ResolveExplicitContactUserIdAsync(tenantId, r.ContactKind, r.ContactId, ct);
        return await QuerySingleProcAsync<ChatThreadResponse>("dbo.Thread_Create",
            new { TenantId = tenantId, OwnerUserId = ownerUserId, r.Name, r.Role, IsGroup = r.Group, r.ChildId, ContactUserId = contactUserId }, ct);
    }

    /// <summary>
    /// Resolves a client-supplied (ContactKind, ContactId) to the real Users.Id the new thread
    /// should target. Teacher/Staff ids resolve via their linked UserId; a Student id resolves
    /// to a linked parent via ParentStudentLinks (first match when a student has more than one
    /// guardian); a "user" id is already a Users.Id (another CRM login — owner/admin/principal/
    /// vice_principal) and is only checked for tenant membership.
    /// </summary>
    private async Task<Guid?> ResolveExplicitContactUserIdAsync(
        Guid tenantId, string? contactKind, Guid? contactId, CancellationToken ct)
    {
        if (contactId is not { } id || string.IsNullOrWhiteSpace(contactKind))
            return null;

        var sql = contactKind.Trim().ToLowerInvariant() switch
        {
            "teacher" => "SELECT \"UserId\" AS \"Id\" FROM \"dbo\".\"Teachers\" WHERE \"Id\" = @id AND \"TenantId\" = @tenantId AND \"UserId\" IS NOT NULL",
            "staff" => "SELECT \"UserId\" AS \"Id\" FROM \"dbo\".\"Staff\" WHERE \"Id\" = @id AND \"TenantId\" = @tenantId AND \"UserId\" IS NOT NULL",
            "student" => "SELECT \"ParentUserId\" AS \"Id\" FROM \"dbo\".\"ParentStudentLinks\" WHERE \"StudentId\" = @id AND \"TenantId\" = @tenantId ORDER BY \"CreatedAt\" LIMIT 1",
            // "user": id IS already a Users.Id (e.g. another owner/admin/principal/vice_principal
            // CRM account) — just confirm it's a real account in this tenant.
            "user" => "SELECT \"Id\" FROM \"dbo\".\"Users\" WHERE \"Id\" = @id AND \"TenantId\" = @tenantId",
            _ => null,
        };
        if (sql is null)
            return null;

        var rows = await QueryInlineAsync<UserIdRow>(sql, new { id, tenantId }, ct);
        return rows.FirstOrDefault()?.Id;
    }

    public async Task<bool> UserOwnsThreadAsync(Guid threadId, Guid ownerUserId, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<int>(
            "SELECT CAST(COUNT(1) AS int) FROM \"dbo\".\"ChatThreads\" WHERE \"Id\" = @threadId AND \"OwnerUserId\" = @ownerUserId",
            new { threadId, ownerUserId }, ct);
        return rows.FirstOrDefault() > 0;
    }

    public async Task<IReadOnlyList<ChatMessageResponse>> ListMessagesAsync(
        Guid threadId, Guid ownerUserId, Guid? callerId, CancellationToken ct = default)
    {
        if (!await UserOwnsThreadAsync(threadId, ownerUserId, ct))
            return Array.Empty<ChatMessageResponse>();

        await MarkThreadReadAsync(threadId, ownerUserId, ct);

        var rows = await QueryInlineAsync<MessageRow>(
            "SELECT \"Id\", \"ThreadId\", \"SenderId\", \"Text\", \"ImageUrl\", \"SentAt\", \"DeliveredAt\", \"ReadAt\" FROM \"dbo\".\"ChatMessages\" WHERE \"ThreadId\" = @threadId ORDER BY \"SentAt\"",
            new { threadId }, ct);
        return rows.Select(m => ToResponse(m, callerId)).ToList();
    }

    public async Task<ChatMessageResponse?> AddMessageAsync(
        Guid tenantId, Guid threadId, Guid ownerUserId, Guid? senderId, string text, string? imageUrl,
        CancellationToken ct = default)
    {
        if (!await UserOwnsThreadAsync(threadId, ownerUserId, ct))
            return null;

        var correlationId = Guid.NewGuid();
        var m = await QuerySingleProcAsync<MessageRow>("dbo.Message_Add",
            new
            {
                TenantId = tenantId,
                ThreadId = threadId,
                SenderId = senderId,
                Text = text,
                ImageUrl = imageUrl,
                CorrelationId = correlationId,
            }, ct);
        if (m is null)
            return null;

        var delivered = await DeliverToPeerInboxAsync(tenantId, threadId, senderId, text, imageUrl, correlationId, ct);
        DateTime? deliveredAt = m.DeliveredAt;
        if (delivered)
        {
            await ExecuteInlineAsync(
                "UPDATE \"dbo\".\"ChatMessages\" SET \"DeliveredAt\" = now() AT TIME ZONE 'UTC' WHERE \"Id\" = @id AND \"DeliveredAt\" IS NULL",
                new { id = m.Id }, ct);
            deliveredAt = DateTime.UtcNow;
        }

        return ToResponse(m, senderId, deliveredAt);
    }

    /// <summary>
    /// Opening a thread is the read receipt: clear the inbox badge and stamp ReadAt
    /// on the sender's copy (matched by CorrelationId) so they get double blue ticks.
    /// </summary>
    private Task MarkThreadReadAsync(Guid threadId, Guid ownerUserId, CancellationToken ct) =>
        ExecuteInlineAsync(@"
UPDATE ""dbo"".""ChatThreads""
SET ""Unread"" = 0
WHERE ""Id"" = @threadId AND ""OwnerUserId"" = @ownerUserId AND ""Unread"" <> 0;

UPDATE ""dbo"".""ChatMessages"" m
SET ""ReadAt"" = now() AT TIME ZONE 'UTC'
FROM ""dbo"".""ChatMessages"" incoming
WHERE incoming.""CorrelationId"" = m.""CorrelationId""
   AND incoming.""ThreadId"" = @threadId
   AND incoming.""SenderId"" IS NOT NULL
   AND incoming.""SenderId"" <> @ownerUserId
   AND m.""ReadAt"" IS NULL;",
            new { threadId, ownerUserId }, ct);

    private static ChatMessageResponse ToResponse(MessageRow m, Guid? callerId, DateTime? deliveredAt = null) =>
        new(m.Id, m.ThreadId, m.SenderId, m.Text, m.SentAt, m.SenderId == callerId, m.ImageUrl)
        {
            DeliveredAt = deliveredAt ?? m.DeliveredAt,
            ReadAt = m.ReadAt,
        };

    /// <summary>
    /// Mirror an outbound message into the contact's private inbox thread so chat is two-way.
    /// Prefers the thread's own ContactUserId (set explicitly on create, or backfilled); falls
    /// back to best-effort name matching only for threads that still have neither.
    /// </summary>
    private async Task<bool> DeliverToPeerInboxAsync(
        Guid tenantId, Guid sourceThreadId, Guid? senderId, string text, string? imageUrl,
        Guid correlationId, CancellationToken ct)
    {
        if (senderId is not { } sid)
            return false;

        var threadRows = await QueryInlineAsync<ThreadInfoRow>(
            "SELECT \"Id\", \"Name\", \"Role\", \"OwnerUserId\", \"IsGroup\", \"ContactUserId\", \"ChildId\" FROM \"dbo\".\"ChatThreads\" WHERE \"Id\" = @sourceThreadId",
            new { sourceThreadId }, ct);
        var thread = threadRows.FirstOrDefault();
        if (thread is null || thread.IsGroup)
            return false;

        var recipientId = thread.ContactUserId ?? await ResolveContactUserIdAsync(tenantId, thread.Name, sid, ct);
        if (recipientId is null || recipientId == sid)
            return false;

        // dbo.User_Create never sets Users.Name (only Teacher/Staff onboarding backfills it) —
        // accounts created straight from signup (owner/admin/principal with no Teacher/Staff
        // row) can have Users.Name = NULL. Delivery must not silently drop the message just
        // because that account never got a display name; fall back to a role label.
        var senderName = await ResolveSenderDisplayNameAsync(tenantId, sid, ct);
        if (string.IsNullOrWhiteSpace(senderName))
            return false;

        var senderRole = await ResolveSenderRoleLabelAsync(tenantId, sid, ct) ?? "Staff";
        // Prefer the child explicitly set on the sender's own thread (parent messaging about a
        // specific one of several kids); fall back to resolving it from the sender's identity
        // (a student messaging as themselves, or a parent with only one linked child) so the
        // teacher's mirrored inbox thread still carries class/section context.
        var childId = thread.ChildId ?? await ResolveChildIdForSenderAsync(tenantId, sid, ct);

        var peerThread = await QuerySingleProcAsync<ChatThreadResponse>("dbo.Thread_Create",
            new
            {
                TenantId = tenantId,
                OwnerUserId = recipientId,
                Name = senderName,
                Role = senderRole,
                IsGroup = false,
                ChildId = childId,
                ContactUserId = (Guid?)sid,
            }, ct);
        if (peerThread is null)
            return false;

        var peer = await QuerySingleProcAsync<MessageRow>("dbo.Message_Add",
            new
            {
                TenantId = tenantId,
                ThreadId = peerThread.Id,
                SenderId = sid,
                Text = text,
                ImageUrl = imageUrl,
                CorrelationId = correlationId,
            }, ct);
        if (peer is null)
            return false;

        await ExecuteInlineAsync(
            "UPDATE \"dbo\".\"ChatThreads\" SET \"Unread\" = \"Unread\" + 1 WHERE \"Id\" = @threadId",
            new { threadId = peerThread.Id }, ct);

        var preview = string.IsNullOrWhiteSpace(text)
            ? (string.IsNullOrWhiteSpace(imageUrl) ? "New message" : "[Image]")
            : text.Trim();
        if (preview.Length > 80) preview = preview[..77] + "...";
        await CreateNotificationAsync(tenantId, new CreateNotificationRequest(
            Icon: "chatbubbles",
            Tone: "chat",
            Title: senderName,
            Body: preview,
            UserId: recipientId), ct);
        return true;
    }

    private const string ParentSuffix = " (parent)";

    private async Task<Guid?> ResolveContactUserIdAsync(
        Guid tenantId, string contactName, Guid senderId, CancellationToken ct)
    {
        // The admin's "message this student's parent" contact stores the thread name as
        // "<Student> (parent)" — that never matches a real account name, so strip the suffix
        // and match the student roster instead (only when the name is unambiguous).
        string? parentContactName = contactName.EndsWith(ParentSuffix, StringComparison.OrdinalIgnoreCase)
            ? contactName[..^ParentSuffix.Length].Trim()
            : null;

        var rows = await QueryInlineAsync<UserIdRow>(@"
SELECT x.""Id""
FROM (
    SELECT u.""Id"", 1 AS ""Pri""
    FROM ""dbo"".""Users"" u
    WHERE u.""TenantId"" = @tenantId AND u.""Name"" = @contactName AND u.""Id"" <> @senderId
    UNION ALL
    SELECT t.""UserId"", 2 AS ""Pri""
    FROM ""dbo"".""Teachers"" t
    WHERE t.""TenantId"" = @tenantId AND t.""Name"" = @contactName AND t.""UserId"" IS NOT NULL AND t.""UserId"" <> @senderId
    UNION ALL
    SELECT s.""UserId"", 3 AS ""Pri""
    FROM ""dbo"".""Staff"" s
    WHERE s.""TenantId"" = @tenantId AND s.""Name"" = @contactName AND s.""UserId"" IS NOT NULL AND s.""UserId"" <> @senderId
    UNION ALL
    SELECT pl.""ParentUserId"", 4 AS ""Pri""
    FROM ""dbo"".""Students"" st
    INNER JOIN ""dbo"".""ParentStudentLinks"" pl ON pl.""StudentId"" = st.""Id"" AND pl.""TenantId"" = st.""TenantId""
    WHERE @parentContactName::text IS NOT NULL
      AND st.""TenantId"" = @tenantId
      AND st.""Name"" = @parentContactName
      AND pl.""ParentUserId"" <> @senderId
      AND (SELECT COUNT(1) FROM ""dbo"".""Students"" st2 WHERE st2.""TenantId"" = @tenantId AND st2.""Name"" = @parentContactName) = 1
) x
WHERE x.""Id"" IS NOT NULL
ORDER BY x.""Pri""
LIMIT 1", new { tenantId, contactName, senderId, parentContactName }, ct);
        return rows.FirstOrDefault()?.Id;
    }

    /// <summary>
    /// Users.Name, falling back to Teachers/Staff.Name, then a friendly label built from the
    /// account's role(s) — so an owner/admin/principal account that never set a display name
    /// (User_Create has no Name parameter at all) still gets a real chat identity instead of
    /// silently blocking delivery.
    /// </summary>
    private async Task<string?> ResolveSenderDisplayNameAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        var rows = await QueryInlineAsync<UserNameRow>(@"
SELECT COALESCE(
    NULLIF(btrim(u.""Name""), ''),
    NULLIF(btrim(t.""Name""), ''),
    NULLIF(btrim(s.""Name""), ''),
    CASE
        WHEN EXISTS (SELECT 1 FROM ""dbo"".""UserRoles"" ur WHERE ur.""UserId"" = u.""Id"" AND ur.""Role"" = 'school.owner') THEN 'School Owner'
        WHEN EXISTS (SELECT 1 FROM ""dbo"".""UserRoles"" ur WHERE ur.""UserId"" = u.""Id"" AND ur.""Role"" = 'school.admin') THEN 'School Admin'
        WHEN EXISTS (SELECT 1 FROM ""dbo"".""UserRoles"" ur WHERE ur.""UserId"" = u.""Id"" AND ur.""Role"" = 'school.principal') THEN 'Principal'
        WHEN EXISTS (SELECT 1 FROM ""dbo"".""UserRoles"" ur WHERE ur.""UserId"" = u.""Id"" AND ur.""Role"" LIKE '%vice%principal%') THEN 'Vice Principal'
        ELSE 'School Office'
    END) AS ""Name""
FROM ""dbo"".""Users"" u
LEFT JOIN ""dbo"".""Teachers"" t ON t.""UserId"" = u.""Id"" AND t.""TenantId"" = @tenantId
LEFT JOIN ""dbo"".""Staff"" s ON s.""UserId"" = u.""Id"" AND s.""TenantId"" = @tenantId
WHERE u.""Id"" = @userId
LIMIT 1", new { tenantId, userId }, ct);
        return rows.FirstOrDefault()?.Name?.Trim();
    }

    private async Task<string?> ResolveSenderRoleLabelAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        // Teachers/Staff cover every staff sender. A sender matching neither is either a
        // student (their own Users.StudentId links straight to their admission no — students
        // DO get their own login here, they're not only reachable via a parent) or a parent
        // (via ParentStudentLinks) — check both before falling back to "Teacher", which used
        // to be the default for ANY unmatched sender, mislabeling every student/parent reply
        // in the teacher's inbox as a teacher.
        var rows = await QueryInlineAsync<RoleLabelRow>(@"
SELECT COALESCE(
    NULLIF(btrim(t.""Designation""), ''),
    NULLIF(btrim(st.""Role""), ''),
    CASE WHEN NULLIF(btrim(u.""StudentId""), '') IS NOT NULL THEN 'Student' END,
    CASE WHEN EXISTS (
        SELECT 1 FROM ""dbo"".""ParentStudentLinks"" pl WHERE pl.""ParentUserId"" = u.""Id"" AND pl.""TenantId"" = @tenantId
    ) THEN 'Parent' END,
    'Teacher') AS ""RoleLabel""
FROM ""dbo"".""Users"" u
LEFT JOIN ""dbo"".""Teachers"" t ON t.""UserId"" = u.""Id"" AND t.""TenantId"" = @tenantId
LEFT JOIN ""dbo"".""Staff"" st ON st.""UserId"" = u.""Id"" AND st.""TenantId"" = @tenantId
WHERE u.""Id"" = @userId
LIMIT 1", new { tenantId, userId }, ct);
        return rows.FirstOrDefault()?.RoleLabel;
    }

    /// <summary>
    /// Which student a chat is "about", for a sender who IS one (their own Users.StudentId,
    /// matched to Students.AdmissionNo) or who is that student's linked parent
    /// (ParentStudentLinks — first-linked child when there's more than one).
    /// </summary>
    private async Task<Guid?> ResolveChildIdForSenderAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        var rows = await QueryInlineAsync<UserIdRow>(@"
SELECT ""ChildId"" AS ""Id"" FROM (
    SELECT s.""Id"" AS ""ChildId"", 1 AS ""Pri""
    FROM ""dbo"".""Users"" u
    INNER JOIN ""dbo"".""Students"" s
        ON s.""TenantId"" = @tenantId AND lower(btrim(s.""AdmissionNo"")) = lower(btrim(u.""StudentId""))
    WHERE u.""Id"" = @userId AND NULLIF(btrim(u.""StudentId""), '') IS NOT NULL
    UNION ALL
    SELECT pl.""StudentId"" AS ""ChildId"", 2 AS ""Pri""
    FROM ""dbo"".""ParentStudentLinks"" pl
    WHERE pl.""ParentUserId"" = @userId AND pl.""TenantId"" = @tenantId
) x
ORDER BY ""Pri""
LIMIT 1", new { tenantId, userId }, ct);
        return rows.FirstOrDefault()?.Id;
    }

    public Task<IReadOnlyList<AnnouncementResponse>> ListAnnouncementsAsync(string? audience, CancellationToken ct = default) =>
        QueryInlineAsync<AnnouncementResponse>(@"
SELECT a.""Id"", a.""TenantId"", a.""Title"", a.""Body"", a.""Date"", COALESCE(u.""Name"", a.""Role"") AS ""From"", a.""Role"", a.""Type"", a.""Pinned"", a.""Audience""
FROM ""dbo"".""Announcements"" a
LEFT JOIN ""dbo"".""Users"" u ON u.""Id"" = a.""CreatorUserId""
WHERE (@audience::text IS NULL OR a.""Audience"" IS NULL OR a.""Audience"" = @audience::text)
ORDER BY a.""Date"" DESC", new { audience }, ct);

    public Task<AnnouncementResponse?> CreateAnnouncementAsync(
        Guid tenantId, CreateAnnouncementRequest r, Guid? creatorUserId, string? role, CancellationToken ct = default) =>
        QuerySingleProcAsync<AnnouncementResponse>("dbo.Announcement_Create",
            new { TenantId = tenantId, r.Title, r.Body, From = role, Role = role, CreatorUserId = creatorUserId, r.Type, r.Audience }, ct);

    public Task<int> SaveAnnouncementDeliveryAsync(
        Guid id, string recipientsJson, string? attachmentFileName, string? attachmentContentType,
        CancellationToken ct = default) =>
        ExecuteInlineAsync(
            """
            UPDATE "dbo"."Announcements" SET
                "RecipientsJson" = @recipientsJson,
                "AttachmentFileName" = @attachmentFileName,
                "AttachmentContentType" = @attachmentContentType
            WHERE "Id" = @id
            """,
            new { id, recipientsJson, attachmentFileName, attachmentContentType }, ct);
}

public static class CommsModule
{
    public static IServiceCollection AddCommsModule(this IServiceCollection services)
    {
        services.AddScoped<CommsRepository>();
        services.AddScoped<UserAppSettingsRepository>();
        return services;
    }
}
