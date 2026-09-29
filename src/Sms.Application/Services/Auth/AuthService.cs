using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Sms.Application.DTOs.Auth;
using Sms.Application.Common;
using Sms.Application.Interfaces.DAO;
using Sms.Modules.Tenancy.Data;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Results;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Application.Services.Auth;

public interface IAuthService
{
    Task<ApiResult<TokenResponse>> LoginAsync(LoginRequest req, CancellationToken ct = default);
    Task<ApiResult<TokenResponse>> RefreshAsync(RefreshRequest req, CancellationToken ct = default);
    Task<ApiResult<object>> RequestOtpAsync(OtpRequest req, CancellationToken ct = default);
    Task<ApiResult<TokenResponse>> VerifyOtpAsync(OtpVerifyRequest req, CancellationToken ct = default);
    Task<ApiResult<object>> ForgotPasswordAsync(ForgotPasswordRequest req, CancellationToken ct = default);
    /// <summary>Onboard invite: welcome email/SMS with school name + password-setup OTP
    /// (or a magic login link, when method is "link").</summary>
    Task<ApiResult<object>> SendInviteSetupAsync(
        string identifier, string schoolName, string? roleLabel = null, TimeSpan? validFor = null,
        string method = "code", string? customMessage = null, CancellationToken ct = default, string? requestedRole = null);
    Task<ApiResult> ResetPasswordAsync(ResetPasswordRequest req, CancellationToken ct = default);
    Task<ApiResult> SetPasswordAsync(SetPasswordRequest req, CancellationToken ct = default);
    Task<ApiResult> UpdatePhotoAsync(UpdatePhotoRequest req, CancellationToken ct = default);
    Task<ApiResult<object>> GetMeAsync(ClaimsPrincipal user, CancellationToken ct = default);
    Task<ApiResult> LogoutAsync(RefreshRequest req, CancellationToken ct = default);
}

public sealed class AuthService(
    IAuthDao users,
    IPasswordHasher hasher,
    IJwtTokenService jwt,
    IRefreshTokenStore tokens,
    IOtpSender otp,
    IEmailQueue emailQueue,
    ISmsSender sms,
    FrontendOptions frontend,
    ClientRepository clients,
    ITenantContext tenant,
    IInvitationDao invitations,
    IProfileDao profiles,
    ILogger<AuthService> logger,
    AuthDiagnosticsOptions diagnostics) : IAuthService
{
    public async Task<ApiResult<TokenResponse>> LoginAsync(LoginRequest req, CancellationToken ct = default)
    {
        var identifier = req.Email is { Length: > 0 } e ? e
            : req.StudentId is { Length: > 0 } sid ? sid
            : req.Phone;
        if (string.IsNullOrWhiteSpace(identifier) || req.Password is null)
            return ApiResult<TokenResponse>.Fail(new Error("invalid_credentials", "email, phone, or student_id and password required"), 422);

        tenant.Set(null, null, isPlatform: true);
        var forceAdmission = !string.IsNullOrWhiteSpace(req.StudentId);
        var (user, passwordMatchRoles, anyCandidates) = await FindUserByPasswordAsync(identifier, req.Password, ct, forceAdmission, req.Role);
        if (user is null)
        {
            if (passwordMatchRoles is not null
                && AppLoginRole.WrongTabMessage(passwordMatchRoles, req.Role) is { } wrongTab)
                return ApiResult<TokenResponse>.Fail(new Error("wrong_role", wrongTab), 403);
            if (await NeedsPasswordSetupAsync(identifier, ct, forceAdmission))
                return ApiResult<TokenResponse>.Fail(new Error("password_not_set",
                    "No password yet. Use set up or reset password."), 409);
            if (!anyCandidates)
                return ApiResult<TokenResponse>.Fail(NotRegisteredError(identifier), 404);
            return ApiResult<TokenResponse>.Fail(new Error("invalid_credentials", "bad email or password"), 401);
        }
        var userRoles = user.Status == "inactive" ? await users.GetRolesAsync(user.Id, ct) : null;
        if (AccessBlockedError(user, userRoles) is { } blocked)
            return ApiResult<TokenResponse>.Fail(blocked, 403);

        return await IssueTokensAsync(user, ct);
    }

    /// Null when the row is free to sign in; an Error when it's "removed" or "inactive"
    /// (paused/removed by the school admin) — same wording either path (password/OTP).
    /// "inactive" for a teacher/staff account uses the suspension wording; every other
    /// role keeps the original "deactivated" wording unchanged. `roles` is omitted by
    /// the two candidate-ordering call sites, which only need the null/non-null result.
    private static Error? AccessBlockedError(UserRecord user, IReadOnlyList<string>? roles = null) => user.Status switch
    {
        "removed" => new Error("access_removed", "Your access to this school has been removed by the admin."),
        "inactive" when roles is not null && IsTeacherOrStaffRole(roles) && !IsElevatedRole(roles) =>
            new Error("access_suspended", "Your account has been suspended by your school. Please contact your school administrator."),
        "inactive" => new Error("access_inactive", "Your access to this school has been deactivated by the admin."),
        _ => null,
    };

    private static bool IsTeacherOrStaffRole(IReadOnlyList<string> roles) =>
        roles.Any(r => string.Equals(r, Policies.Teacher, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(r, Policies.Staff, StringComparison.OrdinalIgnoreCase));

    private static bool IsElevatedRole(IReadOnlyList<string> roles) =>
        roles.Any(r => string.Equals(r, Policies.SchoolOwner, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(r, Policies.SchoolAdmin, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(r, Policies.Principal, StringComparison.OrdinalIgnoreCase));

    public async Task<ApiResult<TokenResponse>> RefreshAsync(RefreshRequest req, CancellationToken ct = default)
    {
        var hash = Sha256(req.RefreshToken);
        var userId = await tokens.GetActiveUserIdAsync(hash, ct);
        if (userId is null)
            return ApiResult<TokenResponse>.Fail(new Error("invalid_token", "refresh token invalid"), 401);

        await tokens.RevokeAsync(hash, ct);
        var newRefresh = jwt.NewRefreshToken();
        await tokens.SaveAsync(userId.Value, Sha256(newRefresh), DateTime.UtcNow.AddDays(30), ct);

        tenant.Set(null, null, isPlatform: true);
        var user = await users.GetByIdAsync(userId.Value, ct);
        if (user is null)
            return ApiResult<TokenResponse>.Fail(new Error("invalid_token", "user no longer exists"), 401);

        var roles = await users.GetRolesAsync(user.Id, ct);
        var access = jwt.IssueAccess(user.Id, user.TenantId, roles, user.IsPlatform);
        return ApiResult<TokenResponse>.Ok(new TokenResponse(access, newRefresh));
    }

    public Task<ApiResult<object>> RequestOtpAsync(OtpRequest req, CancellationToken ct = default) =>
        SendOtpToRegisteredAsync(req.Identifier, null, ct);

    public async Task<ApiResult<TokenResponse>> VerifyOtpAsync(OtpVerifyRequest req, CancellationToken ct = default)
    {
        tenant.Set(null, null, isPlatform: true);
        var activeHash = await users.OtpActiveHashAsync(req.Identifier, ct);
        if (activeHash is null || activeHash != Sha256(req.Code))
            return ApiResult<TokenResponse>.Fail(new Error("invalid_code", "code invalid or expired"), 401);

        if (!await users.OtpConsumeAsync(req.Identifier, activeHash, ct))
            return ApiResult<TokenResponse>.Fail(new Error("invalid_code", "code invalid or expired"), 401);
        var user = await FindUserByIdentifierAsync(req.Identifier, ct);
        if (user is null)
            return ApiResult<TokenResponse>.Fail(new Error("invalid_code", "user not found"), 401);
        var otpUserRoles = user.Status == "inactive" ? await users.GetRolesAsync(user.Id, ct) : null;
        if (AccessBlockedError(user, otpUserRoles) is { } blocked)
            return ApiResult<TokenResponse>.Fail(blocked, 403);

        return await IssueTokensAsync(user, ct);
    }

    public async Task<ApiResult<object>> ForgotPasswordAsync(ForgotPasswordRequest req, CancellationToken ct = default)
    {
        /* First-time / invite users (no password yet): welcome email with school name + OTP.
           Existing users: keep the generic verification-code mail. */
        tenant.Set(null, null, isPlatform: true);
        var user = await FindUserByIdentifierAsync(req.Identifier, ct);
        if (user is not null && string.IsNullOrEmpty(user.PasswordHash))
        {
            var schoolName = "your school";
            string? roleLabel = null;
            if (user.TenantId is Guid tid)
            {
                var school = await clients.GetAsync(tid, ct);
                if (!string.IsNullOrWhiteSpace(school?.Name)) schoolName = school.Name;
                var roles = await users.GetRolesAsync(user.Id, ct);
                roleLabel = RoleLabel(roles.FirstOrDefault());
            }
            return await SendInviteSetupAsync(req.Identifier, schoolName, roleLabel, TimeSpan.FromHours(24), ct: ct, requestedRole: req.Role);
        }
        return await SendOtpToRegisteredAsync(req.Identifier, req.Role, ct);
    }

    public async Task<ApiResult<object>> SendInviteSetupAsync(
        string identifier, string schoolName, string? roleLabel = null, TimeSpan? validFor = null,
        string method = "code", string? customMessage = null, CancellationToken ct = default, string? requestedRole = null)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return ApiResult<object>.Fail(new Error("invalid_request", "email or phone required"), 422);

        tenant.Set(null, null, isPlatform: true);
        var id = identifier.Trim();
        var user = await FindUserByIdentifierAsync(id, ct);
        if (user is null)
            return ApiResult<object>.Fail(NotRegisteredError(id), 404);

        // Deliver to student email first; if missing/invalid, fall back to linked parent —
        // unless the caller explicitly asked for the parent (role=parent), which targets the
        // guardian contact directly. Never treat an admission ID as a deliverable address.
        var delivery = await ResolveOtpDeliveryAsync(user, id, requestedRole, ct);
        if (delivery is null)
            return ApiResult<object>.Fail(
                new Error("no_delivery_channel",
                    "No email or phone on file for this student or linked parent."), 404);
        var (target, channel, recipient) = delivery.Value;

        // "link" method: a long opaque token flows through the exact same OTP
        // storage/consume path as a 6-digit code (Otp_Insert/Otp_Consume compare a
        // SHA256 hash either way) — only the value shape and delivery text differ.
        var useLink = string.Equals(method, "link", StringComparison.OrdinalIgnoreCase);
        var code = useLink
            ? Convert.ToHexString(RandomNumberGenerator.GetBytes(24))
            : RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var expiresAt = DateTime.UtcNow.Add(validFor ?? TimeSpan.FromMinutes(10));
        await users.OtpInsertAsync(id, channel, Sha256(code), expiresAt, ct);

        var link = useLink
            ? $"{frontend.BaseUrl.TrimEnd('/')}/?identifier={Uri.EscapeDataString(id)}&code={Uri.EscapeDataString(code)}"
            : null;

        if (channel == "email")
        {
            emailQueue.Enqueue(InviteWelcomeEmail.Build(target, schoolName, code, roleLabel, link, customMessage));
            // Dev fallback: the welcome email is dispatched out-of-band by EmailDispatchWorker, whose
            // SMTP failures are logged and dropped. Without this line a new admin whose SMTP is down
            // has no way to obtain the setup code. Mirrors EmailOtpSender's [DEV OTP/email] logging.
            if (diagnostics.LogOtpCodes)
                logger.LogWarning("[DEV OTP/email] {Identifier} -> {Code} (development only, invite/setup)", target, code);
        }
        else
            await sms.SendAsync(target, InviteWelcomeEmail.SmsBody(schoolName, code, roleLabel, link, customMessage), ct);

        return ApiResult<object>.Ok(new
        {
            sent = true,
            channel,
            sent_to = MaskDestination(target, channel),
            recipient,
        });
    }

    private static string? RoleLabel(string? role) => role?.ToLowerInvariant() switch
    {
        "school.owner" => "Owner",
        "school.admin" => "Admin",
        "school.principal" => "Principal",
        "school.vice_principal" or "school.vice-principal" => "Vice-Principal",
        "school.teacher" => "Teacher",
        "staff" => "Staff",
        _ => role,
    };

    public async Task<ApiResult> ResetPasswordAsync(ResetPasswordRequest req, CancellationToken ct = default)
    {
        if (req.Password is null || req.Password.Length < 8)
            return ApiResult.Fail(new Error("weak_password", "password must be at least 8 characters"), 422);

        tenant.Set(null, null, isPlatform: true);
        var activeHash = await users.OtpActiveHashAsync(req.Identifier, ct);
        if (activeHash is null || req.Code is null || activeHash != Sha256(req.Code))
            return ApiResult.Fail(new Error("invalid_code", "code invalid or expired"), 401);

        if (!await users.OtpConsumeAsync(req.Identifier, activeHash, ct))
            return ApiResult.Fail(new Error("invalid_code", "code invalid or expired"), 401);
        // Same identifier can own several tenant-scoped rows (invited to multiple
        // schools) — set the password on ALL of them so one setup step works
        // everywhere they were invited, not just the row a tiebreak happens to pick.
        var peers = await ListByIdentifierAsync(req.Identifier, ct);
        if (peers.Count == 0)
            return ApiResult.Fail(new Error("invalid_code", "user not found"), 401);

        var passwordHash = hasher.Hash(req.Password);
        foreach (var peer in peers)
        {
            await users.SetPasswordAsync(peer.Id, passwordHash, ct);
            await invitations.MarkAcceptedByUserIdAsync(peer.Id, ct);
        }
        return ApiResult.NoContent();
    }

    public async Task<ApiResult> SetPasswordAsync(SetPasswordRequest req, CancellationToken ct = default)
    {
        if (tenant.UserId is not { } uid)
            return ApiResult.Fail(new Error("unauthorized", "unauthorized"), 401);
        await users.SetPasswordAsync(uid, hasher.Hash(req.Password), ct);
        return ApiResult.NoContent();
    }

    public async Task<ApiResult> UpdatePhotoAsync(UpdatePhotoRequest req, CancellationToken ct = default)
    {
        if (tenant.UserId is not { } uid)
            return ApiResult.Fail(new Error("unauthorized", "unauthorized"), 401);

        if (ImageUrlValidation.Validate(req.PhotoUrl) is { } error)
            return ApiResult.Fail(error, 422);

        await users.SetPhotoAsync(uid, ImageUrlValidation.Normalize(req.PhotoUrl), ct);
        return ApiResult.NoContent();
    }

    public async Task<ApiResult<object>> GetMeAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var sub = user.FindFirst("sub")?.Value;
        if (sub is null || !Guid.TryParse(sub, out var userId))
            return ApiResult<object>.Fail(new Error("unauthorized", "unauthorized"), 401);

        var record = await users.GetByIdAsync(userId, ct);
        if (record is null)
            return ApiResult<object>.Fail(new Error("unauthorized", "unauthorized"), 401);

        var jwtRoles = user.FindAll("role").Select(c => c.Value);
        var dbRoles = await users.GetRolesAsync(userId, ct);
        var roles = jwtRoles.Concat(dbRoles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roles.Length == 0 && !string.IsNullOrWhiteSpace(record.StudentId))
            roles = ["student"];
        var fields = await ResolveMeFieldsAsync(record, roles, ct);

        string? tenantName = null;
        string? tenantLogoUrl = null;
        string? tenantImageUrl = null;
        string? tier = null;
        string? planName = null;
        if (record.TenantId is Guid tid)
        {
            var school = await clients.GetAsync(tid, ct);
            tenantName = school?.Name;
            // Always return both; the student app picks a paint-able mark (logo → image).
            tenantLogoUrl = ImageUrlValidation.Normalize(school?.LogoUrl);
            tenantImageUrl = ImageUrlValidation.Normalize(school?.ImageUrl);
            tier = school?.Tier;
            planName = school?.PlanName;
        }

        return ApiResult<object>.Ok(new
        {
            id = sub,
            tenant_id = user.FindFirst("tenant_id")?.Value,
            roles,
            is_platform = user.FindFirst("is_platform")?.Value == "1",
            name = record.Name,
            email = fields.Email,
            phone = fields.Phone,
            employee = fields.Employee,
            classroom = fields.Classroom,
            joined = fields.Joined,
            tenant_name = tenantName,
            tenant_logo_url = tenantLogoUrl,
            tenant_image_url = tenantImageUrl,
            tier = tier ?? "silver",
            plan_name = planName,
            must_set_password = record.MustSetPassword,
            title = fields.Title,
            photo_url = fields.PhotoUrl,
            student_id = record.StudentId,
            role_key = fields.RoleKey,
            duty_post = fields.DutyPost,
        });
    }

    private sealed record MeFields(
        string? Email, string? Phone, string? Employee, string? Classroom,
        string? Joined, string? Title, string? PhotoUrl,
        /// <summary>Canonical duty-role key (driver/conductor/guard/peon/sweeper/gardener),
        /// derived from the authenticated user's own dbo.Staff.Role row — never from client
        /// input. Null for non-staff users (teachers, admins, parents, students) and for staff
        /// whose CRM role label doesn't map to a duty role (e.g. "Principal").</summary>
        string? RoleKey,
        /// <summary>Staff-only duty post (Staff.Route, else Staff.Department, else ""). Null
        /// when there is no linked Staff row at all.</summary>
        string? DutyPost);

    private async Task<MeFields> ResolveMeFieldsAsync(
        UserRecord record, IReadOnlyList<string> roles, CancellationToken ct)
    {
        var teacher = await profiles.GetLinkedTeacherAsync(
            record.Id, record.TenantId, record.Email, record.Name, ct);
        var staff = await profiles.GetLinkedStaffAsync(
            record.Id, record.TenantId, record.Email, record.Name, ct);

        var roleLeaves = roles
            .Select(r => r.Split('.').LastOrDefault() ?? r)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isPrincipal = roleLeaves.Contains("principal");

        var resolvedPhone = await ResolveSharedPhoneAsync(record, teacher?.Phone, staff?.Phone, ct);
        var photoUrl = await ResolvePhotoUrlAsync(record, ct);

        // Classroom only applies when this person is linked to a class in this school.
        var classroom = isPrincipal && teacher is null
            ? null
            : FirstNonEmpty(teacher?.ClassTeacher, teacher?.HomeroomClassName);

        var joinedAt = teacher?.JoinedAt ?? staff?.JoinedAt ?? record.CreatedAt;
        var joined = joinedAt.Year > 1 ? joinedAt.Year.ToString() : null;

        string? title;
        if (isPrincipal)
            title = "Principal";
        else if (roleLeaves.Contains("teacher"))
            title = teacher?.Designation;
        else
            title = staff?.Designation;

        return new MeFields(
            Email: FirstNonEmpty(record.Email, teacher?.Email, staff?.Email),
            Phone: resolvedPhone,
            Employee: FirstNonEmpty(teacher?.EmployeeCode, staff?.EmployeeCode),
            Classroom: classroom,
            Joined: joined,
            Title: title,
            PhotoUrl: photoUrl,
            // Authoritative duty identity — sourced only from this user's own linked Staff
            // row, never from anything the client sent at login. Non-staff users (teachers,
            // admins, parents, students) have no Staff row, so both stay null.
            RoleKey: staff is null ? null : StaffRoleMapper.ToRoleKey(staff.Designation),
            DutyPost: staff?.DutyPost);
    }

    /// Phone is shared across all schools for the same email — resolve from Users,
    /// current-school Teachers/Staff, then any school's Teachers/Staff row.
    private async Task<string?> ResolveSharedPhoneAsync(
        UserRecord record, string? teacherPhone, string? staffPhone, CancellationToken ct)
    {
        var local = FirstNonEmpty(record.Phone, teacherPhone, staffPhone);
        if (!string.IsNullOrWhiteSpace(local))
        {
            // Persist roster/local phone onto Users (and peer rows) so school switches
            // and future /auth/me calls do not depend on a cross-tenant roster lookup.
            if (string.IsNullOrWhiteSpace(record.Phone))
                await users.SetPhoneAsync(record.Id, local, ct);
            return local;
        }

        var prevTenant = tenant.TenantId;
        var prevUser = tenant.UserId;
        var wasPlatform = tenant.IsPlatform;
        tenant.Set(null, null, isPlatform: true);
        try
        {
            var shared = await SharedPhoneResolver.ResolveAsync(
                users, profiles, record.Email, record.Name, ct);
            if (!string.IsNullOrWhiteSpace(shared))
                await users.SetPhoneAsync(record.Id, shared, ct);
            return shared;
        }
        finally
        {
            tenant.Set(prevTenant, prevUser, wasPlatform);
        }
    }

    private async Task<string?> ResolvePeerPhoneAsync(UserRecord record, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(record.Email)) return null;
        var peers = await ListPeersByEmailAsync(record.Email, ct);
        return peers.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Phone))?.Phone;
    }

    private async Task<string?> ResolvePhotoUrlAsync(UserRecord record, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(record.PhotoUrl)) return record.PhotoUrl;
        if (string.IsNullOrWhiteSpace(record.Email)) return null;
        var peers = await ListPeersByEmailAsync(record.Email, ct);
        return peers.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.PhotoUrl))?.PhotoUrl;
    }

    private async Task<IReadOnlyList<UserRecord>> ListPeersByEmailAsync(string email, CancellationToken ct)
    {
        var prevTenant = tenant.TenantId;
        var prevUser = tenant.UserId;
        var wasPlatform = tenant.IsPlatform;
        tenant.Set(null, null, isPlatform: true);
        try
        {
            return await users.ListByEmailAsync(email, ct);
        }
        finally
        {
            tenant.Set(prevTenant, prevUser, wasPlatform);
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return null;
    }

    public async Task<ApiResult> LogoutAsync(RefreshRequest req, CancellationToken ct = default)
    {
        await tokens.RevokeAsync(Sha256(req.RefreshToken), ct);
        return ApiResult.NoContent();
    }

    private async Task<ApiResult<TokenResponse>> IssueTokensAsync(UserRecord user, CancellationToken ct)
    {
        var roles = (await users.GetRolesAsync(user.Id, ct)).ToList();
        if (roles.Count == 0 && !string.IsNullOrWhiteSpace(user.StudentId))
            roles.Add("student");
        var access = jwt.IssueAccess(user.Id, user.TenantId, roles, user.IsPlatform);
        var refresh = jwt.NewRefreshToken();
        await tokens.SaveAsync(user.Id, Sha256(refresh), DateTime.UtcNow.AddDays(30), ct);
        return ApiResult<TokenResponse>.Ok(new TokenResponse(access, refresh));
    }

    private async Task<ApiResult<object>> SendOtpToRegisteredAsync(string identifier, string? role, CancellationToken ct)
    {
        tenant.Set(null, null, isPlatform: true);
        var user = await FindUserByIdentifierAsync(identifier, ct);

        if (user is null)
            return ApiResult<object>.Fail(NotRegisteredError(identifier), 404);

        // Student email first (up to 2 delivery attempts). Missing/invalid email or
        // repeated send failure → linked parent/caregiver contact. role="parent" targets
        // the guardian contact directly instead.
        var delivery = await ResolveOtpDeliveryAsync(user, identifier, role, ct);
        if (delivery is null)
            return ApiResult<object>.Fail(
                new Error("no_delivery_channel",
                    "No email or phone on file for this student or linked parent."), 404);

        var (target, channel, recipient) = delivery.Value;
        string? code = null;
        Exception? lastSendError = null;

        // Prefer the resolved target; if it is the student's email and send fails twice,
        // re-resolve forcing parent fallback.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                code = await otp.SendAsync(target, channel, ct);
                lastSendError = null;
                break;
            }
            catch (Exception ex)
            {
                lastSendError = ex;
                if (recipient != "self" || attempt >= 2) break;
            }
        }

        if (code is null && recipient == "self")
        {
            var parent = await FindParentDeliveryAsync(user, identifier, ct);
            if (parent is not null)
            {
                (target, channel, recipient) = parent.Value;
                try
                {
                    code = await otp.SendAsync(target, channel, ct);
                    lastSendError = null;
                }
                catch (Exception ex)
                {
                    lastSendError = ex;
                }
            }
        }

        if (code is null)
            return ApiResult<object>.Fail(
                new Error("delivery_failed",
                    lastSendError?.Message ?? "Could not deliver verification code."), 502);

        await users.OtpInsertAsync(identifier, channel, Sha256(code), DateTime.UtcNow.AddMinutes(10), ct);
        return ApiResult<object>.Ok(new
        {
            sent = true,
            channel,
            sent_to = MaskDestination(target, channel),
            recipient,
        });
    }

    /// <summary>
    /// OTP delivery for admission-ID / student flows:
    /// 1) student-role account email (never the parent row that shares StudentId)
    /// 2) student phone
    /// 3) linked parent/caregiver email or phone
    /// Non-admission lookups still use the resolved account's own contact.
    /// </summary>
    private async Task<(string Target, string Channel, string Recipient)?> ResolveOtpDeliveryAsync(
        UserRecord user, string identifier, string? role, CancellationToken ct)
    {
        var admissionId = !string.IsNullOrWhiteSpace(user.StudentId)
            ? user.StudentId!
            : IdentifierClassifier.Classify(identifier) == IdentifierKind.AdmissionId
                ? identifier.Trim()
                : null;

        if (admissionId is not null)
        {
            // role=parent asks for the guardian contact directly — never deliver a
            // parent's reset code to the student's own address. Everything else
            // (role=student or unspecified) keeps the student-first behavior below.
            var wantsParent = string.Equals(role, "parent", StringComparison.OrdinalIgnoreCase);

            if (wantsParent)
            {
                // Direct delivery to the identified parent — "self" (the requester's own
                // contact), distinct from the "parent" tag used below when a *student*
                // lookup falls back to notifying their linked parent instead.
                var roster = await users.GetRosterByAdmissionIdAsync(admissionId, ct);
                if (IsUsableEmail(roster?.GuardianEmail))
                    return (roster!.GuardianEmail!.Trim(), "email", "self");
                if (!string.IsNullOrWhiteSpace(roster?.GuardianPhone))
                    return (roster!.GuardianPhone!.Trim(), "sms", "self");
            }
            else
            {
                // Admin student form writes Students.Email. The Users login row can still
                // hold a parent/creator email — roster email is the student destination.
                var rosterEmail = await users.GetRosterEmailByAdmissionIdAsync(admissionId, ct);
                if (IsUsableEmail(rosterEmail))
                    return (rosterEmail!.Trim(), "email", "self");
            }

            var peers = await users.ListByAdmissionIdAsync(admissionId, ct);
            UserRecord? student = null;
            UserRecord? parent = null;

            foreach (var peer in peers)
            {
                var roles = await users.GetRolesAsync(peer.Id, ct);
                if (IsParentRole(roles))
                    parent ??= peer;
                else if (IsStudentRole(roles))
                    student ??= peer;
            }

            // If roles are missing on older rows, treat the looked-up user as student
            // only when it is not a parent; otherwise keep searching peers.
            if (student is null)
            {
                var lookedUpRoles = await users.GetRolesAsync(user.Id, ct);
                if (!IsParentRole(lookedUpRoles))
                    student = user;
            }

            if (!wantsParent)
            {
                if (student is not null && IsUsableEmail(student.Email))
                    return (student.Email!.Trim(), "email", "self");
                if (student is not null && !string.IsNullOrWhiteSpace(student.Phone))
                    return (student.Phone.Trim(), "sms", "self");
            }

            if (parent is not null && IsUsableEmail(parent.Email))
                return (parent.Email!.Trim(), "email", "parent");
            if (parent is not null && !string.IsNullOrWhiteSpace(parent.Phone))
                return (parent.Phone.Trim(), "sms", "parent");

            // Last resort: any other peer with contact (exclude the student — even when
            // targeting the parent, never fall back to the student's own address).
            foreach (var peer in peers.Where(p => student is null || p.Id != student.Id))
            {
                if (IsUsableEmail(peer.Email))
                    return (peer.Email!.Trim(), "email", "parent");
                if (!string.IsNullOrWhiteSpace(peer.Phone))
                    return (peer.Phone.Trim(), "sms", "parent");
            }

            return null;
        }

        if (IsUsableEmail(user.Email))
            return (user.Email!.Trim(), "email", "self");
        if (!string.IsNullOrWhiteSpace(user.Phone))
            return (user.Phone.Trim(), "sms", "self");
        return null;
    }

    private async Task<(string Target, string Channel, string Recipient)?> FindParentDeliveryAsync(
        UserRecord user, string identifier, CancellationToken ct)
    {
        var admissionId = !string.IsNullOrWhiteSpace(user.StudentId)
            ? user.StudentId!
            : IdentifierClassifier.Classify(identifier) == IdentifierKind.AdmissionId
                ? identifier.Trim()
                : null;
        if (admissionId is null) return null;

        var peers = await users.ListByAdmissionIdAsync(admissionId, ct);
        foreach (var peer in peers)
        {
            var roles = await users.GetRolesAsync(peer.Id, ct);
            if (!IsParentRole(roles)) continue;
            if (IsUsableEmail(peer.Email))
                return (peer.Email!.Trim(), "email", "parent");
            if (!string.IsNullOrWhiteSpace(peer.Phone))
                return (peer.Phone.Trim(), "sms", "parent");
        }
        return null;
    }

    private static bool IsParentRole(IReadOnlyList<string> roles) => AppLoginRole.IsParent(roles);

    private static bool IsStudentRole(IReadOnlyList<string> roles) => AppLoginRole.IsStudent(roles);

    private static bool IsUsableEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        var at = email.IndexOf('@');
        return at > 0 && at < email.Length - 1 && email.IndexOf('.', at) > at;
    }

    private static string MaskDestination(string value, string channel)
    {
        if (channel == "sms")
        {
            var digits = new string(value.Where(char.IsDigit).ToArray());
            if (digits.Length < 4) return "****";
            return new string('*', Math.Max(4, digits.Length - 4)) + digits[^4..];
        }

        var at = value.IndexOf('@');
        if (at <= 0) return "***";
        return $"{value[0]}***{value[at..]}";
    }

    /// <summary>
    /// Resolves login across multi-tenant peers that share the same email/phone.
    /// Picks the first password match, preferring platform accounts, and preferring
    /// a fully-active row over a removed/inactive one when several match (so losing
    /// access to one school never blocks signing in to another with the same creds).
    /// </summary>
    private async Task<(UserRecord? User, IReadOnlyList<string>? PasswordMatchRoles, bool AnyCandidates)> FindUserByPasswordAsync(
        string identifier, string password, CancellationToken ct, bool forceAdmission = false,
        string? requestedRole = null)
    {
        var candidates = await ListByIdentifierAsync(identifier, ct, forceAdmission);
        var passwordMatched = new List<(UserRecord User, List<string> Roles)>();
        foreach (var u in candidates)
        {
            if (u.PasswordHash is null || !hasher.Verify(password, u.PasswordHash)) continue;
            var roles = (await users.GetRolesAsync(u.Id, ct)).ToList();
            if (roles.Count == 0 && !string.IsNullOrWhiteSpace(u.StudentId))
                roles.Add("student");
            passwordMatched.Add((u, roles));
        }

        var matched = passwordMatched
            .Where(x => AppLoginRole.Matches(x.Roles, requestedRole))
            .Select(x => x.User)
            .ToList();
        var user = matched
            .OrderByDescending(u => u.IsPlatform)
            .ThenBy(u => AccessBlockedError(u) is null ? 0 : 1)
            .FirstOrDefault();
        IReadOnlyList<string>? wrongTabRoles = user is null && passwordMatched.Count > 0
            ? passwordMatched[0].Roles
            : null;
        return (user, wrongTabRoles, candidates.Count > 0);
    }

    private async Task<bool> NeedsPasswordSetupAsync(string identifier, CancellationToken ct, bool forceAdmission)
    {
        var candidates = await ListByIdentifierAsync(identifier, ct, forceAdmission);
        if (candidates.Any(u => string.IsNullOrEmpty(u.PasswordHash)))
            return true;

        var kind = forceAdmission ? IdentifierKind.AdmissionId : IdentifierClassifier.Classify(identifier);
        if (kind == IdentifierKind.Email) return false;
        if (await PickStudentAsync(candidates, ct) is not null) return false;

        var provisioned = await EnsureStudentUserFromRosterAsync(identifier, ct);
        return provisioned is not null;
    }

    private async Task<UserRecord?> FindUserByIdentifierAsync(
        string identifier, CancellationToken ct, bool forceAdmission = false)
    {
        var trimmed = identifier.Trim();
        var kind = forceAdmission ? IdentifierKind.AdmissionId : IdentifierClassifier.Classify(trimmed);
        var candidates = await ListByIdentifierAsync(trimmed, ct, forceAdmission);
        var asAdmission = forceAdmission || kind == IdentifierKind.AdmissionId;

        if (asAdmission)
        {
            var student = await PickStudentAsync(candidates, ct);
            if (student is not null) return student;

            var provisioned = await EnsureStudentUserFromRosterAsync(trimmed, ct);
            if (provisioned is not null) return provisioned;
        }
        else if (kind == IdentifierKind.Phone && candidates.Count == 0)
        {
            var provisioned = await EnsureStudentUserFromRosterAsync(trimmed, ct);
            if (provisioned is not null) return provisioned;
        }

        if (candidates.Count == 0) return null;
        return PickBest(candidates);
    }

    /// <summary>
    /// Roster students imported before login provisioning have a Students row but no Users row.
    /// dbo.Student_EnsureLogin fetches the roster and creates the student login when missing.
    /// </summary>
    private Task<UserRecord?> EnsureStudentUserFromRosterAsync(string admissionId, CancellationToken ct) =>
        users.EnsureStudentLoginAsync(admissionId, ct);

    private async Task<UserRecord?> PickStudentAsync(IReadOnlyList<UserRecord> candidates, CancellationToken ct)
    {
        foreach (var c in PickBestOrder(candidates))
        {
            var roles = await users.GetRolesAsync(c.Id, ct);
            if (IsStudentRole(roles)) return c;
            if (roles.Count == 0 && !string.IsNullOrWhiteSpace(c.StudentId)) return c;
        }
        return null;
    }

    private static UserRecord? PickBest(IReadOnlyList<UserRecord> candidates) =>
        PickBestOrder(candidates).FirstOrDefault();

    private static IEnumerable<UserRecord> PickBestOrder(IReadOnlyList<UserRecord> candidates) =>
        candidates
            .OrderByDescending(u => u.IsPlatform)
            .ThenBy(u => AccessBlockedError(u) is null ? 0 : 1);

    private async Task<IReadOnlyList<UserRecord>> ListByIdentifierAsync(
        string identifier, CancellationToken ct, bool forceAdmission = false)
    {
        var trimmed = identifier.Trim();
        var kind = forceAdmission ? IdentifierKind.AdmissionId : IdentifierClassifier.Classify(trimmed);
        switch (kind)
        {
            case IdentifierKind.Email:
            {
                var byEmail = await users.ListByEmailAsync(trimmed, ct);
                if (byEmail.Count > 0) return byEmail;
                // Admin stores email on Students; Users.Email may still be empty or a parent copy.
                var viaRoster = await TryResolveStudentByRosterEmailAsync(trimmed, ct);
                if (viaRoster is not null) return [viaRoster];
                // Onboarded staff (dbo.Staff.Email) with no login yet — self-serve via OTP,
                // no admin invite required. CRM roles (admin/principal/vice_principal) already
                // get a Users row from Send invite, so this only ever fires for the rest.
                var viaStaff = await users.EnsureStaffLoginAsync(trimmed, ct);
                if (viaStaff is not null) return [viaStaff];
                // Not a student's own address — check whether it's a guardian email on file
                // and lazily provision/find that parent's login (no admission ID or role
                // hint required; the plain guardian email is enough to resolve it).
                var viaParent = await TryResolveParentByGuardianEmailAsync(trimmed, ct);
                return viaParent is null ? Array.Empty<UserRecord>() : [viaParent];
            }
            case IdentifierKind.Phone:
            {
                var byPhone = await users.ListByPhoneAsync(trimmed, ct);
                if (byPhone.Count > 0) return byPhone;
                return await users.ListByAdmissionIdAsync(trimmed, ct);
            }
            default:
                return await users.ListByAdmissionIdAsync(trimmed, ct);
        }
    }

    /// <summary>
    /// Find student login via Students.Email when Users.Email does not match (common after roster import).
    /// Syncs Users.Email so subsequent email login / forgot-password resolve directly.
    /// </summary>
    private async Task<UserRecord?> TryResolveStudentByRosterEmailAsync(string email, CancellationToken ct)
    {
        var roster = await users.GetRosterByEmailAsync(email, ct);
        if (roster is null || string.IsNullOrWhiteSpace(roster.AdmissionNo)) return null;

        await users.EnsureStudentLoginAsync(roster.AdmissionNo, ct);
        var candidates = await users.ListByAdmissionIdAsync(roster.AdmissionNo, ct);
        var student = await PickStudentAsync(candidates, ct);
        if (student is null) return null;

        var normalized = email.Trim();
        if (!string.Equals(student.Email?.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
        {
            await users.SetEmailAsync(student.Id, normalized, ct);
            student = await users.GetByIdAsync(student.Id, ct) ?? student;
        }
        return student;
    }

    /// <summary>
    /// Find (or lazily provision) the parent login for a guardian email on file, when the
    /// identifier does not match any Users row or student roster email directly.
    /// </summary>
    private async Task<UserRecord?> TryResolveParentByGuardianEmailAsync(string email, CancellationToken ct)
    {
        var roster = await users.GetRosterByGuardianEmailAsync(email, ct);
        if (roster is null || string.IsNullOrWhiteSpace(roster.AdmissionNo)) return null;
        return await users.EnsureParentLoginAsync(roster.AdmissionNo, ct);
    }

    private static Error NotRegisteredError(string identifier)
    {
        var kind = IdentifierClassifier.Classify(identifier);
        var message = kind switch
        {
            IdentifierKind.Email => "Email is not registered.",
            IdentifierKind.Phone => "Phone is not registered.",
            _ => "No student account found for this ID. Contact your school.",
        };
        return new Error("not_registered", message);
    }

    private static string Sha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }
}
