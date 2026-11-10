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
        new("staff", "staff", "Staff Report", false,
            "This is the current staff and teacher list. The date range is printed on the file.",
            ["Employee ID", "Employee Name", "Designation", "Role", "Department", "Gender", "Mobile Number", "Official Email", "Joining Date", "Employment Type", "Qualification", "Assigned Class", "Reporting Manager", "Attendance %", "Status"],
            ["employee", "department", "designation", "role", "gender"]),
        new("teacher", "staff", "Teacher Report", false,
            "This is the current teacher list. The date range is printed on the file.",
            ["Teacher ID", "Employee ID", "Teacher Name", "Department", "Subject", "Assigned Classes", "Assigned Sections", "Class Teacher", "Students Assigned", "Weekly Periods", "Joining Date", "Attendance %", "Status"],
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
            "Class attendance for the selected dates. Student check-in and check-out are not stored.",
            ["Date", "Admission No.", "Student Name", "Class", "Section", "Roll No.", "Attendance Status", "Check-in Time", "Check-out Time", "Marked By", "Remarks"],
            ["class", "section", "student", "attendanceStatus"]),
        new("staff_attendance", "attendance", "Staff Attendance", true,
            "Staff and teacher attendance for the selected dates. Late minutes and approver are not stored.",
            ["Employee ID", "Employee Name", "Department", "Designation", "Date", "Check-in Time", "Check-out Time", "Working Hours", "Late Minutes", "Leave Type", "Attendance Status", "Approved By"],
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
