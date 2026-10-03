namespace Sms.Infrastructure.SQL;

/// Stored procedure names for auth data access.
public static class AuthQueries
{
    public const string GetByEmail = "dbo.User_GetByEmail";
    public const string GetByPhone = "dbo.User_GetByPhone";
    public const string GetById = "dbo.User_GetById";
    public const string GetRoles = "dbo.UserRoles_GetByUser";
    public const string SetPassword = "dbo.User_SetPassword";
    public const string SetPhoto = "dbo.User_SetPhoto";
    public const string SetEmail = "dbo.User_SetEmail";
    public const string OtpInsert = "dbo.Otp_Insert";
    public const string OtpGetActive = "dbo.Otp_GetActive";
    public const string OtpConsume = "dbo.Otp_Consume";
    public const string OtpConsumeAll = "dbo.Otp_ConsumeAllForIdentifier";
    public const string EnsureStudentLogin = "dbo.Student_EnsureLogin";
    public const string EnsureParentLogin = "dbo.Parent_EnsureLogin";
    public const string EnsureStaffLogin = "dbo.Staff_EnsureLogin";
    public const string EnsureTeacherLogin = "dbo.Teacher_EnsureLogin";
    public const string ListByAdmissionId = "dbo.User_ListByAdmissionId";
    public const string GetRosterByAdmissionNo = "dbo.Student_GetByAdmissionNo";
}
