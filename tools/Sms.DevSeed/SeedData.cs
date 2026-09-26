using Sms.Shared.Kernel.Auth;

namespace Sms.DevSeed;

/// The whole dev school. Columns and types verified against db/postgres/04_tables.sql (see plan Phase 1).
public static class SeedData
{
    public static readonly Guid MainTenantId = SeedIds.Of("tenant.main");
    public static readonly Guid OtherTenantId = SeedIds.Of("tenant.other");

    public const string PrincipalEmail = "principal@seed.schooldesk.test";
    public const string TeacherAEmail = "teacher.a@seed.schooldesk.test";
    public const string TeacherBEmail = "teacher.b@seed.schooldesk.test";
    public const string MultiEmail = "multi@seed.schooldesk.test";
    public const string OtherTeacherEmail = "other.teacher@seed.schooldesk.test";
    // Dev fixtures, not secrets: documented in README.md and mirrored in the app's e2e/support/config.ts.
    public const string PrincipalPassword = "DevSeed-Principal-2026!";
    public const string TeacherPassword = "DevSeed-Teacher-2026!";

    public const double Lat = 18.5204, Lng = 73.8567;

    /// Insert order; also the checksum table list.
    public static readonly string[] Tables =
    [
        "Tenants", "SchoolLocations", "Users", "UserRoles", "Teachers", "Classes", "Students", "Subjects",
        "ClassSubjects", "TimetableSlots", "Exams", "ExamClasses", "ExamPapers", "LeaveRequests",
        "Announcements", "TransportRoutes", "RouteStops", "Buses", "BusStops", "BusAssignments",
        "StudentBusAssignments",
    ];

    private static readonly string[] Days = ["Mon", "Tue", "Wed", "Thu", "Fri"];
    private static readonly (int Period, string Start, string End)[] Periods =
        [(1, "09:00", "09:45"), (2, "09:50", "10:35"), (3, "10:40", "11:25")];

    private static readonly string[] StudentNamesA =
        ["Aarav Shah", "Diya Patil", "Ishaan Rao", "Kavya Joshi", "Rohan Das", "Saanvi Iyer", "Vihaan Nair", "Anaya Gupta", "Arjun Mehta", "Myra Kapoor"];
    private static readonly string[] StudentNamesB =
        ["Aditya Kumar", "Ira Sethi", "Kabir Singh", "Meera Pillai", "Neel Bose", "Riya Desai", "Shaurya Jain", "Tara Menon", "Yash Verma", "Zoya Khan"];

    public static IReadOnlyList<Guid> SeedUserIds =>
        [U("principal"), U("teacher.a"), U("teacher.b"), U("multi.main"), U("multi.other"), U("other.teacher")];

    private static Guid U(string k) => SeedIds.Of($"user.{k}");
    private static Guid T(string k) => SeedIds.Of($"teacher.{k}");
    private static Guid C(string k) => SeedIds.Of($"class.{k}");
    private static Guid S(string cls, int n) => SeedIds.Of($"student.{cls}.{n}");

    private static SeedRow Row(string table, params (string Col, object Val)[] cols) =>
        new(table, cols.ToDictionary(c => c.Col, c => c.Val));

    public static IReadOnlyList<SeedRow> Build(IPasswordHasher hasher)
    {
        var rows = new List<SeedRow>();
        var main = MainTenantId;
        var other = OtherTenantId;

        // Tenants: active + platinum (trial is blocked by BillingStateMiddleware; transport/geofence need platinum).
        rows.Add(Row("Tenants", ("Id", main), ("Name", "SchoolDesk Dev Seed"), ("Slug", "devseed-main"),
            ("Status", "active"), ("Tier", "platinum"), ("PlanName", "Platinum"), ("Lat", Lat), ("Lng", Lng),
            ("GeofenceRadiusMeters", 200), ("ContactEmail", PrincipalEmail)));
        rows.Add(Row("Tenants", ("Id", other), ("Name", "Dev Seed Other School"), ("Slug", "devseed-other"),
            ("Status", "active"), ("Tier", "platinum"), ("PlanName", "Platinum")));

        rows.Add(Row("SchoolLocations", ("Id", SeedIds.Of("location.main")), ("TenantId", main),
            ("Lat", Lat), ("Lng", Lng), ("RadiusMeters", 200), ("Name", "Dev Seed Campus")));

        // Users + roles. multi@ exists in both tenants → /me/schools returns 2 and switch-school is testable.
        var principalHash = hasher.Hash(PrincipalPassword);
        var teacherHash = hasher.Hash(TeacherPassword);
        void AddUser(string key, Guid tenant, string email, string name, string hash, string role)
        {
            rows.Add(Row("Users", ("Id", U(key)), ("TenantId", tenant), ("Email", email), ("Name", name),
                ("PasswordHash", hash), ("Status", "active")));
            rows.Add(Row("UserRoles", ("UserId", U(key)), ("Role", role)));
        }
        AddUser("principal", main, PrincipalEmail, "Priya Deshmukh", principalHash, "school.principal");
        AddUser("teacher.a", main, TeacherAEmail, "Asha Kulkarni", teacherHash, "school.teacher");
        AddUser("teacher.b", main, TeacherBEmail, "Bharat Menon", teacherHash, "school.teacher");
        AddUser("multi.main", main, MultiEmail, "Maya Fernandes", teacherHash, "school.teacher");
        AddUser("multi.other", other, MultiEmail, "Maya Fernandes", teacherHash, "school.teacher");
        AddUser("other.teacher", other, OtherTeacherEmail, "Omar Qureshi", teacherHash, "school.teacher");

        void AddTeacher(string key, Guid tenant, string userKey, string name, string email, string code,
            string designation, string subjects, string classTeacher) =>
            rows.Add(Row("Teachers", ("Id", T(key)), ("TenantId", tenant), ("UserId", U(userKey)), ("Name", name),
                ("Email", email), ("EmployeeCode", code), ("Designation", designation),
                ("SubjectsCsv", subjects), ("ClassTeacher", classTeacher), ("Status", "active")));
        AddTeacher("a", main, "teacher.a", "Asha Kulkarni", TeacherAEmail, "DS-T001", "Mathematics Teacher", "Mathematics", "IX-A");
        AddTeacher("b", main, "teacher.b", "Bharat Menon", TeacherBEmail, "DS-T002", "Science Teacher", "Science", "IX-B");
        AddTeacher("multi", main, "multi.main", "Maya Fernandes", MultiEmail, "DS-T003", "English Teacher", "English", "");
        AddTeacher("other", other, "other.teacher", "Omar Qureshi", OtherTeacherEmail, "OS-T001", "Mathematics Teacher", "Mathematics", "IX-A");

        // Classes: StudentCount left at its default on purpose; whether the API reports the live count is a matrix check.
        void AddClass(string key, Guid tenant, string name, string section, string classTeacherKey, string room) =>
            rows.Add(Row("Classes", ("Id", C(key)), ("TenantId", tenant), ("Name", name), ("Grade", "IX"),
                ("Section", section), ("Subject", "Mathematics"), ("Room", room), ("ClassTeacherId", T(classTeacherKey))));
        AddClass("ix-a", main, "IX-A", "A", "a", "R101");
        AddClass("ix-b", main, "IX-B", "B", "b", "R102");
        AddClass("other.ix-a", other, "IX-A", "A", "other", "R1");

        void AddStudents(string cls, Guid tenant, string label, string section, string prefix, string[] names)
        {
            for (var i = 0; i < names.Length; i++)
                rows.Add(Row("Students", ("Id", S(cls, i + 1)), ("TenantId", tenant), ("AdmissionNo", $"{prefix}-{i + 1:00}"),
                    ("Name", names[i]), ("Grade", "IX"), ("Section", section), ("ClassLabel", label), ("Roll", i + 1),
                    ("GuardianName", $"Guardian of {names[i]}"), ("GuardianPhone", $"+9190000{i + 1:00}{section}0".Replace("A", "1").Replace("B", "2")),
                    ("Status", "active")));
        }
        AddStudents("ix-a", main, "IX-A", "A", "DS-A", StudentNamesA);
        AddStudents("ix-b", main, "IX-B", "B", "DS-B", StudentNamesB);
        AddStudents("other.ix-a", other, "IX-A", "A", "OS-A", ["Other Student One", "Other Student Two", "Other Student Three"]);

        var subjects = new[] { ("Mathematics", "MATH", "a"), ("Science", "SCI", "b"), ("English", "ENG", "multi") };
        foreach (var (name, shortName, teacher) in subjects)
            rows.Add(Row("Subjects", ("Id", SeedIds.Of($"subject.{name}")), ("TenantId", main), ("Name", name),
                ("Short", shortName), ("TeacherId", T(teacher))));
        foreach (var cls in new[] { "ix-a", "ix-b" })
            foreach (var (name, _, _) in subjects)
                rows.Add(Row("ClassSubjects", ("Id", SeedIds.Of($"classsubject.{cls}.{name}")), ("TenantId", main),
                    ("ClassId", C(cls)), ("SubjectId", SeedIds.Of($"subject.{name}")), ("Name", name)));

        // Timetable Mon–Fri. IX-A: P1 Maths (A = class teacher AND first period), P2 Science (B), P3 English (multi).
        // IX-B: P1 Science (B), P2 Maths (A), P3 English (multi). B is never first-period/class teacher of IX-A → 403 on its roll-call.
        var plan = new (string Cls, string Label, int Period, string Subject, string Teacher)[]
        {
            ("ix-a", "IX-A", 1, "Mathematics", "a"), ("ix-a", "IX-A", 2, "Science", "b"), ("ix-a", "IX-A", 3, "English", "multi"),
            ("ix-b", "IX-B", 1, "Science", "b"), ("ix-b", "IX-B", 2, "Mathematics", "a"), ("ix-b", "IX-B", 3, "English", "multi"),
        };
        foreach (var day in Days)
            foreach (var p in plan)
            {
                var (_, start, end) = Periods.Single(x => x.Period == p.Period);
                rows.Add(Row("TimetableSlots", ("Id", SeedIds.Of($"slot.{p.Cls}.{day}.{p.Period}")), ("TenantId", main),
                    ("Day", day), ("Period", p.Period), ("Subject", p.Subject), ("ClassId", C(p.Cls)), ("ClassName", p.Label),
                    ("Room", p.Cls == "ix-a" ? "R101" : "R102"), ("StartTime", start), ("EndTime", end), ("TeacherId", T(p.Teacher))));
            }
        foreach (var day in Days)
            rows.Add(Row("TimetableSlots", ("Id", SeedIds.Of($"slot.other.{day}.1")), ("TenantId", other), ("Day", day),
                ("Period", 1), ("Subject", "Mathematics"), ("ClassId", C("other.ix-a")), ("ClassName", "IX-A"),
                ("StartTime", "09:00"), ("EndTime", "09:45"), ("TeacherId", T("other"))));

        var exam = SeedIds.Of("exam.ut1");
        rows.Add(Row("Exams", ("Id", exam), ("TenantId", main), ("Name", "Dev Seed Unit Test 1"), ("Type", "unit"),
            ("Grades", "IX"), ("FromDate", new DateOnly(2026, 11, 2)), ("ToDate", new DateOnly(2026, 11, 6)),
            ("SubjectCount", 2), ("Published", true)));
        foreach (var cls in new[] { "ix-a", "ix-b" })
            rows.Add(Row("ExamClasses", ("Id", SeedIds.Of($"examclass.{cls}")), ("TenantId", main), ("ExamId", exam), ("ClassId", C(cls))));
        void AddPaper(string key, string cls, string name, string subject, DateOnly date, string invigilator) =>
            rows.Add(Row("ExamPapers", ("Id", SeedIds.Of($"paper.{key}")), ("TenantId", main), ("ExamId", exam),
                ("ClassId", C(cls)), ("Name", name), ("Subject", subject), ("SubjectId", SeedIds.Of($"subject.{subject}")),
                ("Date", date), ("StartTime", "09:00"), ("DurationMin", 60), ("MaxMarks", 50), ("Status", "upcoming"),
                ("Invigilator1", invigilator), ("Topics", "Algebra")));
        AddPaper("ix-a.math", "ix-a", "IX-A Mathematics", "Mathematics", new DateOnly(2026, 11, 2), "Asha Kulkarni");
        AddPaper("ix-a.sci", "ix-a", "IX-A Science", "Science", new DateOnly(2026, 11, 3), "Bharat Menon");
        AddPaper("ix-b.math", "ix-b", "IX-B Mathematics", "Mathematics", new DateOnly(2026, 11, 2), "Asha Kulkarni");

        rows.Add(Row("LeaveRequests", ("Id", SeedIds.Of("leave.b.pending")), ("TenantId", main), ("RequesterId", U("teacher.b")),
            ("Type", "casual"), ("FromDate", new DateOnly(2026, 11, 10)), ("ToDate", new DateOnly(2026, 11, 11)),
            ("Reason", "Dev seed pending leave"), ("Status", "pending"), ("AppliedOn", new DateOnly(2026, 9, 26)),
            ("Priority", "medium")));

        rows.Add(Row("Announcements", ("Id", SeedIds.Of("announcement.welcome")), ("TenantId", main), ("Title", "Dev Seed Welcome"),
            ("Body", "Welcome to the SchoolDesk dev seed school."), ("Date", new DateTime(2026, 9, 26, 8, 0, 0, DateTimeKind.Utc)),
            ("From", "Priya Deshmukh"), ("Role", "principal"), ("Type", "info"), ("Audience", "all"), ("CreatorUserId", U("principal"))));

        // Transport: one route, one bus, teacher A on duty, IX-A students 1–5 riding.
        var route = SeedIds.Of("route.1");
        var bus = SeedIds.Of("bus.ds01");
        rows.Add(Row("TransportRoutes", ("Id", route), ("TenantId", main), ("Name", "Dev Seed Route 1")));
        var stops = new[] { ("Shivaji Nagar", 18.5308, 73.8475, "07:30"), ("FC Road", 18.5236, 73.8412, "07:40"), ("Campus Gate", Lat, Lng, "07:55") };
        for (var i = 0; i < stops.Length; i++)
        {
            var (name, lat, lng, time) = stops[i];
            rows.Add(Row("RouteStops", ("Id", SeedIds.Of($"routestop.{i + 1}")), ("TenantId", main), ("RouteId", route),
                ("Name", name), ("Seq", i + 1), ("Lat", lat), ("Lng", lng)));
            rows.Add(Row("BusStops", ("Id", SeedIds.Of($"busstop.{i + 1}")), ("TenantId", main), ("BusId", bus),
                ("Name", name), ("Time", time), ("Seq", i + 1), ("Lat", lat), ("Lng", lng)));
        }
        rows.Add(Row("Buses", ("Id", bus), ("TenantId", main), ("BusNo", "DS-01"), ("RouteName", "Dev Seed Route 1"),
            ("RouteId", route), ("Driver", "Dev Seed Driver"), ("DriverPhone", "+919000000999"), ("Capacity", 40)));
        rows.Add(Row("BusAssignments", ("Id", SeedIds.Of("busassignment.a")), ("TenantId", main),
            ("TeacherUserId", U("teacher.a")), ("BusId", bus)));
        for (var i = 1; i <= 5; i++)
            rows.Add(Row("StudentBusAssignments", ("Id", SeedIds.Of($"studentbus.{i}")), ("TenantId", main),
                ("StudentId", S("ix-a", i)), ("BusId", bus), ("StopId", SeedIds.Of($"busstop.{(i - 1) % 3 + 1}")), ("RouteId", route)));

        return rows;
    }
}
