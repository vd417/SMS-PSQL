namespace Sms.Api.Swagger;

/// Single source of truth for which frontend app(s) consume each API route.
/// Drives the per-app Swagger documents: an endpoint appears in every app listed for its route.
/// Edit THIS table when an endpoint's audience changes — no module files need touching.
///
/// Matching is by ordered, segment-aware route prefix (most specific first), because some
/// prefixes are shared: e.g. "/v1/staff/trips" is the Staff app, but "/v1/staff" (HR records)
/// is School Admin.
public static class ApiAudienceMap
{
    public const string CatreAdmin = "catre-admin";
    public const string SchoolAdmin = "school-admin";
    public const string Teacher = "teacher";
    public const string Student = "student";
    public const string Staff = "staff";

    /// The five Swagger documents, in dropdown order.
    public static readonly IReadOnlyList<(string Key, string Title)> Apps =
    [
        (CatreAdmin, "Catre Super-Admin API"),
        (SchoolAdmin, "School Admin API"),
        (Teacher, "Teacher App API"),
        (Student, "Student & Parent App API"),
        (Staff, "Staff App API"),
    ];

    private static readonly string[] All = [CatreAdmin, SchoolAdmin, Teacher, Student, Staff];

    // Ordered route-prefix -> consuming apps. MOST SPECIFIC FIRST (first match wins).
    private static readonly (string Prefix, string[] Apps)[] Rules =
    [
        ("v1/auth",          All),
        ("v1/me/schools/fee-summary", [SchoolAdmin]),
        ("v1/me/schools",    [SchoolAdmin]),
        ("v1/me/plans",      [SchoolAdmin]),
        ("v1/me/upgrade-requests", [SchoolAdmin]),
        ("v1/me/switch-school", [SchoolAdmin]),
        ("v1/me/children",   [Student]),                // parent app · child's live bus
        ("v1/parents",       [Student]),                // parent app · linked children
        ("v1/ptm",           [Student]),                // parent app · parent-teacher meetings
        ("v1/me/settings",   [Student, Teacher, SchoolAdmin]),
        ("v1/webhooks/razorpay", All),
        ("health",           All),

        // Catre platform admin
        ("v1/tenancy",       [CatreAdmin]),
        ("v1/clients",       [CatreAdmin]),
        ("v1/plans",         [CatreAdmin]),
        ("v1/invoices",      [CatreAdmin]),
        ("v1/subscriptions", [CatreAdmin]),
        ("v1/upgrade-requests", [CatreAdmin]),
        ("v1/dashboard/stats", [Teacher]),             // teacher dashboard stats — must precede v1/dashboard
        ("v1/dashboard",     [CatreAdmin]),
        ("v1/onboarding",    [CatreAdmin]),
        ("v1/tickets",       [CatreAdmin]),
        ("v1/team",          [CatreAdmin]),
        ("v1/audit",         [CatreAdmin]),
        ("v1/reports",       [CatreAdmin]),

        // Staff mobile app lives under /v1/staff/trips|trip — list these BEFORE /v1/staff (HR).
        ("v1/staff/trips",   [Staff]),
        ("v1/staff/trip",    [Staff]),
        // Task/checklist endpoints: staff app does its own list/complete/photo; the manager
        // create + list-all endpoints on the same prefix are also consumed by the CRM.
        ("v1/staff/tasks",   [Staff, SchoolAdmin]),
        ("v1/staff",         [SchoolAdmin]),               // HR staff records

        ("v1/bus",           [Teacher]),                   // bus routes + boarding for teacher app
        ("v1/transport",     [SchoolAdmin]),               // admin Operations · Transport KPIs
        ("v1/hostel",        [SchoolAdmin]),               // admin Operations · Hostel
        ("v1/sports",        [SchoolAdmin]),               // admin Operations · Sports
        ("v1/users",         [SchoolAdmin]),

        ("v1/students",      [SchoolAdmin, Teacher, Student]),
        ("v1/teachers",      [SchoolAdmin, Student]),
        ("v1/classes",       [SchoolAdmin, Teacher]),
        ("v1/subjects",      [SchoolAdmin, Teacher, Student]),
        ("v1/exam-papers",   [SchoolAdmin, Teacher, Student]),
        ("v1/exams",         [SchoolAdmin]),
        ("v1/grades",        [SchoolAdmin, Teacher, Student]),
        ("v1/homework",      [Teacher, Student]),
        ("v1/achievements",  [Teacher, Student, SchoolAdmin]),
        ("v1/fees",          [SchoolAdmin, Student]),
        ("v1/payslips",      [SchoolAdmin, Teacher, Staff]),
        ("v1/payroll",       [SchoolAdmin]),               // salary master + monthly run/approve
        ("v1/principal",     [Teacher]),
        ("v1/approvals",     [SchoolAdmin, Teacher]),
        ("v1/leave",         [SchoolAdmin, Staff, Student]),
        ("v1/announcements", [SchoolAdmin, Teacher, Student]),
        ("v1/threads",       [SchoolAdmin, Teacher, Student]),
        ("v1/complaints",    [SchoolAdmin]),
        ("v1/notifications", [SchoolAdmin, Teacher, Student]),
        ("v1/timetable",     [Teacher, Student]),
        ("v1/calendar",      [SchoolAdmin, Teacher, Student]),
        ("v1/library/summary", [SchoolAdmin]),             // admin Operations · Library KPIs
        ("v1/library",       [Teacher]),
        ("v1/assignments",   [SchoolAdmin, Teacher, Student]),
        ("v1/me/attendance", [Teacher, Staff]),            // geofenced self check-in
    ];

    private static (string Prefix, string[] Apps)? Match(string? relativePath)
    {
        var path = relativePath?.Trim('/') ?? "";
        foreach (var rule in Rules)
            if (path.Equals(rule.Prefix, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(rule.Prefix + "/", StringComparison.OrdinalIgnoreCase))
                return rule;
        return null;
    }

    /// The apps that should list this route; empty if unmapped (excluded from all docs).
    public static IReadOnlyCollection<string> AppsFor(string? relativePath) =>
        Match(relativePath)?.Apps ?? [];

    /// Swagger tag for in-document grouping — the last segment of the matched route prefix
    /// (e.g. "/v1/staff/trips" -> "trips", "/v1/me/attendance" -> "attendance").
    public static string TagFor(string? relativePath)
    {
        var prefix = Match(relativePath)?.Prefix;
        if (string.IsNullOrEmpty(prefix)) return "general";
        var segs = prefix.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segs[^1];
    }
}
