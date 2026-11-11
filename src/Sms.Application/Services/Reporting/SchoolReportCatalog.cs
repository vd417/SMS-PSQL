using Sms.Modules.Reporting.Contracts;

namespace Sms.Application.Services.Reporting;

public sealed record SchoolReportDef(
    string Key,
    string Tab,
    string Title,
    bool Dated,
    string Note,
    string[] Columns,
    string[] Filters);

public static class SchoolReportCatalog
{
    public static readonly IReadOnlyList<SchoolReportDef> All =
    [
        new("student", "student", "Student Report", false,
            "This is the current roster. The date range is printed on the Excel and PDF.",
            ["Admission No.", "Student Name", "Date of Birth", "Gender", "Class", "Section", "Roll No.", "Academic Session", "Admission Date", "Parent/Guardian Name", "Parent Mobile", "Parent Email", "Address", "Transport Required", "Route Name", "Fee Category", "Annual Fee", "Paid Fee", "Pending Fee", "Student Status"],
            ["class", "section", "student", "gender"]),
        new("staff", "staff", "Staff Report", true,
            "Check-in, check-out, work hours, timetable period, subject, and leave for each day in the selected range.",
            ["Employee ID", "Employee Name", "Role", "Department", "Date", "Check-in Time", "Check-out Time", "Working Hours", "Period", "Subject", "Leave", "Attendance Status"],
            ["employee", "department", "designation", "role", "gender"]),
        new("teacher", "staff", "Teacher Report", false,
            "One row per timetable period, with the subject and the period time.",
            ["Teacher ID", "Employee ID", "Teacher Name", "Department", "Subject", "Class", "Section", "Period", "Time", "Class Teacher", "Students Assigned", "Joining Date", "Status"],
            ["teacher", "department", "gender"]),
        new("admin", "admin", "Admin & Other Staff", false,
            "School admin, principal, and owner logins. Responsibilities and reporting manager are not stored.",
            ["Employee ID", "Name", "Designation", "Role", "Department", "Mobile", "Email", "Joining Date", "Responsibilities", "Reporting Manager", "Last Login", "Account Status"],
            ["employee", "department", "role"]),
        new("fee_collection", "fee", "Fee Collection", true,
            "Payments in the selected date range. Discount, late fee, and collected-by are not stored.",
            ["Receipt No.", "Admission No.", "Student Name", "Class", "Section", "Fee Head", "Total Fee", "Discount", "Late Fee", "Paid Amount", "Pending Amount", "Payment Mode", "Transaction ID", "Payment Date", "Collected By", "Receipt Status"],
            ["class", "section", "student", "feeHead", "paymentMode", "paymentStatus"]),
        new("fee_defaulters", "fee", "Fee Defaulters", true,
            "Unpaid invoices due on or before the To date. Last reminder date is not stored.",
            ["Admission No.", "Student Name", "Class", "Section", "Parent Name", "Parent Mobile", "Fee Head", "Total Payable", "Total Paid", "Outstanding Amount", "Due Date", "Days Overdue", "Last Payment Date", "Last Reminder Date", "Defaulter Status"],
            ["class", "section", "student", "feeHead", "paymentStatus"]),
        new("student_attendance", "attendance", "Student Attendance", true,
            "Daily detail: one row per period. A daily mark is included only when that student has no period mark that day. Sundays and holidays are left out. Remarks are not stored, so that column is blank. Marked At is when the period mark was saved.",
            ["Date", "Admission No.", "Student Name", "Father/Guardian Name", "Class", "Section", "Roll No.", "Subject", "Period", "Time", "Attendance Status", "Remarks", "Marked By", "Marked At"],
            ["session", "class", "section", "student", "attendanceStatus", "subject", "period"]),
        new("student_attendance_summary", "attendance", "Monthly Attendance Summary", true,
            "One row per student. Working days are the selected dates except Sundays and holidays. Attendance % is present and late days divided by those working days. A half day counts as present.",
            ["Admission No.", "Student Name", "Father/Guardian Name", "Class", "Section", "Roll No.", "Total Working Days", "Present Days", "Absent Days", "Leave Days", "Late Days", "Attendance Percentage"],
            ["session", "class", "section", "student", "attendanceStatus", "subject", "period"]),
        new("student_attendance_low", "attendance", "Students Below 75%", true,
            "Students whose attendance is under 75% of the working days in this range.",
            ["Admission No.", "Student Name", "Father/Guardian Name", "Class", "Section", "Roll No.", "Total Working Days", "Present Days", "Absent Days", "Leave Days", "Late Days", "Attendance Percentage"],
            ["session", "class", "section", "student", "attendanceStatus", "subject", "period"]),
        new("student_attendance_streak", "attendance", "Consecutive Absences", true,
            "One row per student who was absent on 3 or more consecutive working days. Sundays and holidays do not break the streak.",
            ["Admission No.", "Student Name", "Father/Guardian Name", "Class", "Section", "Roll No.", "Consecutive Absent Days", "From", "To"],
            ["session", "class", "section", "student", "subject", "period"]),
        new("student_attendance_class", "attendance", "Class Attendance Summary", true,
            "One row per class and section. Attendance % uses each student's working days.",
            ["Class", "Section", "Students", "Total Working Days", "Present Days", "Absent Days", "Leave Days", "Late Days", "Attendance Percentage"],
            ["session", "class", "section", "subject", "period"]),
        new("student_attendance_history", "attendance", "Student Attendance History", true,
            "Daily attendance for one student. Search a student name or admission number before generating.",
            ["Date", "Admission No.", "Student Name", "Father/Guardian Name", "Class", "Section", "Roll No.", "Subject", "Period", "Time", "Attendance Status", "Remarks", "Marked By", "Marked At"],
            ["session", "class", "section", "student", "attendanceStatus", "subject", "period"]),
        new("staff_attendance", "attendance", "Staff Attendance", true,
            "Check-in, check-out, work hours, timetable period, subject, and leave for the selected dates.",
            ["Employee ID", "Employee Name", "Department", "Designation", "Date", "Check-in Time", "Check-out Time", "Working Hours", "Period", "Subject", "Leave", "Attendance Status"],
            ["employee", "department", "designation", "attendanceStatus"]),
        new("parent", "student", "Parent Report", false,
            "Parent logins linked to a student. The date range is printed on the file.",
            ["Parent ID", "Parent Name", "Relationship", "Mobile Number", "Email", "Linked Student", "Admission No.", "Class", "Section", "Address", "App Registration Status", "Last Login", "Account Status"],
            ["class", "section", "student"]),
        new("transport", "transport", "Transport Report", false,
            "Students assigned to a bus. Drop point and drop time are not stored separately.",
            ["Student Name", "Admission No.", "Class", "Section", "Route Name", "Vehicle No.", "Driver Name", "Driver Mobile", "Pickup Point", "Drop Point", "Pickup Time", "Drop Time", "Transport Fee", "Transport Status"],
            ["class", "section", "student", "route", "vehicle"]),
        new("academic", "academic", "Academic Performance", true,
            "Marks whose exam date falls in the selected range.",
            ["Admission No.", "Student Name", "Class", "Section", "Subject", "Examination", "Maximum Marks", "Obtained Marks", "Percentage", "Grade", "Result Status", "Teacher Name"],
            ["class", "section", "student", "teacher"]),
    ];

    public static SchoolReportDef? Find(string? key) =>
        All.FirstOrDefault(d => string.Equals(d.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<SchoolReportCatalogItem> ToCatalog() =>
        All.Select(d => new SchoolReportCatalogItem(d.Key, d.Tab, d.Title, d.Dated, d.Note, d.Columns, d.Filters)).ToList();
}
